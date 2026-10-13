using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;

namespace TechCare.Clinic.Api.Features.Ai;

/// <summary>
/// Optional PostgreSQL + pgvector index for the medical library. The clinic data itself stays on the
/// configured database (SQLite by default); only the chunk vectors move here, so a search becomes an
/// approximate nearest-neighbour lookup instead of a full scan over every vector held in memory.
/// While <see cref="Enabled"/> is false (or the server is unreachable) the app keeps using the
/// in-memory index, so nothing breaks if the store is not running.
/// </summary>
public class VectorStoreOptions
{
    public bool Enabled { get; set; }

    /// <summary>PostgreSQL connection string of the pgvector database (see scripts/start-vector-db.ps1).</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Must match the embedding model's output size (nomic-embed-text v1.5 = 768).</summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>Rows per round trip for label updates, which are cheap integer writes.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Vectors per INSERT. Each row is a 768-d embedding plus an HNSW update, so this stays small.</summary>
    public int WriteBatchSize { get; set; } = 40;

    /// <summary>Seconds allowed for one vector INSERT. The driver default (30) dies while the HNSW index absorbs a batch.</summary>
    public int WriteTimeoutSeconds { get; set; } = 180;
}

/// <summary>A chunk id with its cosine similarity to the query (1 = identical, matching the in-memory score).</summary>
public sealed record VectorHit(int ChunkId, double Score);

/// <summary>
/// Sidecar vector index. One table keyed by the chunk id of the clinic database, so the library rows
/// themselves never leave SQLite and deleting a document just deletes its ids here.
/// </summary>
public sealed class VectorStore : IAsyncDisposable
{
    private const string Table = "medical_chunk_vectors";

    private readonly VectorStoreOptions _options;
    private readonly ILogger<VectorStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NpgsqlDataSource? _source;

    public VectorStore(IOptions<VectorStoreOptions> options, ILogger<VectorStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Configured at all; <see cref="Ready"/> additionally means the schema check succeeded.</summary>
    public bool Enabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ConnectionString);

    public bool Ready => _source != null;

    /// <summary>Connects and creates the extension, table and HNSW index if they are missing. Safe to call repeatedly.</summary>
    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        if (!Enabled || Ready) return Ready;

        await _gate.WaitAsync(ct);
        try
        {
            if (Ready) return true;
            var source = NpgsqlDataSource.Create(_options.ConnectionString);
            try
            {
                await using var cmd = source.CreateCommand($"""
                    CREATE EXTENSION IF NOT EXISTS vector;
                    CREATE TABLE IF NOT EXISTS {Table} (
                        chunk_id integer PRIMARY KEY,
                        embedding vector({_options.Dimensions}) NOT NULL
                    );
                    ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS source_type text;
                    CREATE INDEX IF NOT EXISTS {Table}_hnsw
                        ON {Table} USING hnsw (embedding vector_cosine_ops);
                    CREATE INDEX IF NOT EXISTS {Table}_source_type
                        ON {Table} (source_type);
                    """);
                cmd.CommandTimeout = 15;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch
            {
                await source.DisposeAsync();
                throw;
            }

            _source = source;
            _logger.LogInformation("مخزن المتجهات جاهز ({Table}، {Dimensions} بُعد)", Table, _options.Dimensions);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "مخزن المتجهات غير متاح — سيتم الرجوع للفهرس في الذاكرة");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Nearest chunks to <paramref name="query"/>, best first. Empty when the store is not ready.
    /// <paramref name="sourceTypes"/> restricts the candidates to the kind of evidence the question needs
    /// (guideline / drug_label / egypt_catalog / pubmed / uploaded), so an off-topic source can never win.
    /// </summary>
    public async Task<List<VectorHit>> SearchAsync(float[] query, int k, IReadOnlyList<string>? sourceTypes = null, CancellationToken ct = default)
    {
        if (!Ready || k <= 0) return new List<VectorHit>();

        var typed = sourceTypes is { Count: > 0 };
        await using var cmd = _source!.CreateCommand($"""
            SELECT chunk_id, 1 - (embedding <=> $1::vector) AS score
            FROM {Table}
            {(typed ? "WHERE source_type = ANY($3::text[])" : "")}
            ORDER BY embedding <=> $1::vector
            LIMIT $2::int
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = ToLiteral(query) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = k });
        if (typed) cmd.Parameters.Add(new NpgsqlParameter { Value = sourceTypes!.ToArray() });

        var hits = new List<VectorHit>(k);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            hits.Add(new VectorHit(reader.GetInt32(0), reader.GetDouble(1)));
        return hits;
    }

    /// <summary>Inserts or refreshes vectors (and the source type) for existing chunks (indexing and backfill).</summary>
    public async Task UpsertAsync(IReadOnlyList<(int ChunkId, float[] Vector, string? SourceType)> rows, CancellationToken ct = default)
    {
        if (!Ready || rows.Count == 0) return;

        var batch = Math.Max(1, _options.WriteBatchSize);
        for (var offset = 0; offset < rows.Count; offset += batch)
        {
            var slice = rows.Skip(offset).Take(batch).ToList();
            await using var cmd = _source!.CreateCommand($"""
                INSERT INTO {Table} (chunk_id, embedding, source_type)
                SELECT id, v::vector, st FROM UNNEST($1::int[], $2::text[], $3::text[]) AS t(id, v, st)
                ON CONFLICT (chunk_id) DO UPDATE SET embedding = EXCLUDED.embedding,
                    source_type = COALESCE(EXCLUDED.source_type, {Table}.source_type)
                """);
            cmd.CommandTimeout = Math.Max(30, _options.WriteTimeoutSeconds);
            cmd.Parameters.Add(new NpgsqlParameter { Value = slice.Select(r => r.ChunkId).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = slice.Select(r => ToLiteral(r.Vector)).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = slice.Select(r => r.SourceType).ToArray() });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Tags rows that were copied here before the source type existed. Only the label column is written,
    /// so no embedding is ever recomputed.
    /// </summary>
    public async Task BackfillSourceTypesAsync(IReadOnlyList<(int ChunkId, string SourceType)> rows, CancellationToken ct = default)
    {
        if (!Ready || rows.Count == 0) return;

        for (var offset = 0; offset < rows.Count; offset += _options.BatchSize)
        {
            var slice = rows.Skip(offset).Take(_options.BatchSize).ToList();
            await using var cmd = _source!.CreateCommand($"""
                UPDATE {Table} v SET source_type = m.st
                FROM UNNEST($1::int[], $2::text[]) AS m(id, st)
                WHERE v.chunk_id = m.id AND v.source_type IS DISTINCT FROM m.st
                """);
            cmd.Parameters.Add(new NpgsqlParameter { Value = slice.Select(r => r.ChunkId).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = slice.Select(r => r.SourceType).ToArray() });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Rows that still carry no source type (0 once the backfill has caught up).</summary>
    public async Task<long> UntaggedCountAsync(CancellationToken ct = default)
    {
        if (!Ready) return 0;

        await using var cmd = _source!.CreateCommand($"SELECT count(*) FROM {Table} WHERE source_type IS NULL");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>Forgets chunks that were deleted or replaced in the clinic database.</summary>
    public async Task DeleteAsync(IReadOnlyCollection<int> chunkIds, CancellationToken ct = default)
    {
        if (!Ready || chunkIds.Count == 0) return;

        await using var cmd = _source!.CreateCommand($"DELETE FROM {Table} WHERE chunk_id = ANY($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = chunkIds.ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        if (!Ready) return 0;

        await using var cmd = _source!.CreateCommand($"SELECT count(*) FROM {Table}");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>Ids already indexed — used to backfill chunks that predate the vector store.</summary>
    public async Task<HashSet<int>> ExistingIdsAsync(CancellationToken ct = default)
    {
        var ids = new HashSet<int>();
        if (!Ready) return ids;

        await using var cmd = _source!.CreateCommand($"SELECT chunk_id FROM {Table}");
        cmd.CommandTimeout = Math.Max(30, _options.WriteTimeoutSeconds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    /// <summary>pgvector text form: <c>[0.1,0.2,...]</c> (vectors are stored normalized by the caller).</summary>
    private static string ToLiteral(float[] vector)
    {
        var sb = new StringBuilder(vector.Length * 8 + 2);
        sb.Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vector[i].ToString(CultureInfo.InvariantCulture));
        }
        return sb.Append(']').ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_source != null) await _source.DisposeAsync();
        _gate.Dispose();
    }
}
