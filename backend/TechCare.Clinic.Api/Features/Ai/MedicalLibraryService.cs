using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;

namespace TechCare.Clinic.Api.Features.Ai;

public record LibraryHit(int ChunkId, string DocumentTitle, string FileName, int? PageNumber, string Text, double Score,
    string? Section = null, string? Url = null, string? SourceType = null, string? Evidence = null, int? Year = null)
{
    /// <summary>Chunk text without the metadata header that knowledge-base chunks start with.</summary>
    public string Body => Section != null && Text.IndexOf("\n\n", StringComparison.Ordinal) is var i and > 0 ? Text[(i + 2)..] : Text;
}

/// <summary>
/// Retrieval over the medical library. When a <see cref="VectorStore"/> (PostgreSQL + pgvector) is
/// configured, the nearest chunks are found there and only those rows are read back, so the library can
/// hold hundreds of thousands of chunks. Otherwise it falls back to an in-memory pass of dot products,
/// which is fine for thousands of chunks.
/// </summary>
public class LibraryVectorCache
{
    private record Entry(int ChunkId, string Title, string FileName, int? Page, string Text, string? Section, string? Url, string? SourceType, string? Evidence, int? Year, float[] Vector);

    private volatile List<Entry>? _entries;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly VectorStore _vectors;

    public LibraryVectorCache(VectorStore vectors) => _vectors = vectors;

    public void Invalidate() => _entries = null;

    /// <summary>
    /// Nearest hits for <paramref name="query"/>. When <paramref name="sourceTypes"/> is given, only chunks of
    /// those kinds are considered, so a question is answered from the evidence it asked for instead of, say,
    /// a random PubMed abstract.
    /// </summary>
    public async Task<List<LibraryHit>> SearchAsync(ClinicDbContext db, float[] query, int k, IReadOnlyList<string>? sourceTypes, CancellationToken ct)
    {
        Normalize(query);
        var wanted = sourceTypes is { Count: > 0 } ? new HashSet<string>(sourceTypes, StringComparer.OrdinalIgnoreCase) : null;

        // With the vector store the nearest chunks are found in the database and only those rows are read
        // back, so neither every vector nor every chunk's text has to fit in memory.
        if (_vectors.Ready)
            return await SearchStoreAsync(db, query, k, sourceTypes, ct);

        var entries = _entries ?? await LoadAsync(db, ct);
        return entries
            .Where(e => wanted == null || (e.SourceType != null && wanted.Contains(e.SourceType)))
            .Select(e => (e, score: Dot(e.Vector, query)))
            .OrderByDescending(x => x.score)
            .Take(k)
            .Select(x => new LibraryHit(x.e.ChunkId, x.e.Title, x.e.FileName, x.e.Page, x.e.Text, Math.Round(x.score, 3), x.e.Section, x.e.Url, x.e.SourceType, x.e.Evidence, x.e.Year))
            .ToList();
    }

    /// <summary>Ranks in pgvector, then reads just the winning rows from the clinic database.</summary>
    private async Task<List<LibraryHit>> SearchStoreAsync(ClinicDbContext db, float[] query, int k, IReadOnlyList<string>? sourceTypes, CancellationToken ct)
    {
        var ranked = await _vectors.SearchAsync(query, k, sourceTypes, ct);
        if (ranked.Count == 0) return new List<LibraryHit>();

        var ids = ranked.Select(r => r.ChunkId).ToArray();
        var rows = await db.LibraryChunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, Title = c.Title ?? c.Document!.Title, c.Document!.FileName, c.PageNumber, c.Text, c.Section, c.SourceUrl, c.SourceType, c.Evidence, c.Year })
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);

        return ranked
            .Where(r => byId.ContainsKey(r.ChunkId))
            .Select(r =>
            {
                var row = byId[r.ChunkId];
                return new LibraryHit(row.Id, row.Title, row.FileName, row.PageNumber, row.Text, Math.Round(r.Score, 3), row.Section, row.SourceUrl, row.SourceType, row.Evidence, row.Year);
            })
            .ToList();
    }

    private async Task<List<Entry>> LoadAsync(ClinicDbContext db, CancellationToken ct)
    {
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_entries != null) return _entries;
            var rows = await db.LibraryChunks.AsNoTracking()
                .Select(c => new { c.Id, Title = c.Title ?? c.Document!.Title, c.Document!.FileName, c.PageNumber, c.Text, c.Section, c.SourceUrl, c.SourceType, c.Evidence, c.Year, c.Embedding })
                .ToListAsync(ct);
            _entries = rows.Select(r => new Entry(r.Id, r.Title, r.FileName, r.PageNumber, r.Text, r.Section, r.SourceUrl, r.SourceType, r.Evidence, r.Year, FromBytes(r.Embedding))).ToList();
            return _entries;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public static byte[] ToBytes(float[] vector)
    {
        Normalize(vector);
        return MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
    }

    /// <summary>Rebuilds a vector from the bytes kept in the clinic database (used to backfill the vector store).</summary>
    public static float[] ToVector(byte[] bytes) => FromBytes(bytes);

    private static float[] FromBytes(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();

    private static void Normalize(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm == 0) return;
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
    }

    private static double Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < n; i++) sum += a[i] * b[i];
        return sum;
    }
}

public class LibraryIndexStatus
{
    public bool IsRunning { get; set; }
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public string? CurrentFile { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public List<string> Errors { get; set; } = new();

    /// <summary>Number of vectors currently held in the external store (0 when it is disabled).</summary>
    public long VectorChunks { get; set; }

    /// <summary>True when a PostgreSQL + pgvector store answered; searches then run in the database.</summary>
    public bool VectorStoreReady { get; set; }
}

public class MedicalLibraryService
{
    private const int EmbedBatchSize = 16;

    /// <summary>Upper bound on rewritten query strings per question (one embedding call, batched).</summary>
    private const int MaxQueries = 4;
    private static readonly LibraryIndexStatus Status = new();
    private static readonly SemaphoreSlim IndexLock = new(1, 1);

    private readonly ClinicDbContext _db;
    private readonly LmStudioClient _ai;
    private readonly LibraryVectorCache _cache;
    private readonly VectorStore _vectors;
    private readonly PharmacyBrandMap _brands;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MedicalLibraryService> _logger;
    private readonly string _libraryPath;
    private readonly AiOptions _aiOptions;

    public MedicalLibraryService(ClinicDbContext db, LmStudioClient ai, LibraryVectorCache cache, VectorStore vectors,
        PharmacyBrandMap brands, IServiceScopeFactory scopes, IOptions<AiOptions> options, IWebHostEnvironment env,
        ILogger<MedicalLibraryService> logger)
    {
        _db = db;
        _ai = ai;
        _cache = cache;
        _vectors = vectors;
        _brands = brands;
        _scopes = scopes;
        _logger = logger;
        _libraryPath = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.LibraryPath));
        _aiOptions = options.Value;
        Directory.CreateDirectory(_libraryPath);
    }

    public string LibraryPath => _libraryPath;

    public LibraryIndexStatus GetStatus()
    {
        Status.VectorStoreReady = _vectors.Ready;
        return Status;
    }

    /// <summary>
    /// Plain search without routing — the doctor's words are embedded as they are. Used by the library page
    /// to preview what a query would match; the assistant always goes through
    /// <see cref="SearchAsync(QueryPlan, IReadOnlyList{BrandMatch}, CancellationToken)"/>.
    /// </summary>
    public async Task<List<LibraryHit>> SearchAsync(string query, int k, CancellationToken ct = default)
    {
        if (!await _db.LibraryChunks.AnyAsync(ct)) return new();
        var vector = (await _ai.EmbedAsync(new[] { query }, isQuery: true, ct))[0];
        return await _cache.SearchAsync(_db, vector, k, null, ct);
    }

    /// <summary>
    /// Retrieval driven by the router: the plan's English query, its source types and its top-k decide what is
    /// searched, and the Egyptian trade names learned from the catalogue are folded in, so a question asked
    /// with a brand name still lands on the chunk that names the active ingredient.
    /// </summary>
    public async Task<List<LibraryHit>> SearchAsync(QueryPlan plan, IReadOnlyList<BrandMatch> brands, CancellationToken ct = default)
    {
        if (!await _db.LibraryChunks.AnyAsync(ct)) return new();

        // PLAN.md 2.4: a resolved brand name or a known drug reaches the label's own chunks directly — an exact
        // title match does not need an embedding distance at all.
        var direct = await DirectLabelHitsAsync(plan, brands, ct);

        var queries = await BuildQueriesAsync(plan, brands, ct);
        var vectors = await _ai.EmbedAsync(queries, isQuery: true, ct);

        var hits = await SearchVectorsAsync(vectors, plan, ct);

        // Routing can be wrong for an unusual question: rather than answer "no evidence", the primary query is
        // repeated over every source before giving up.
        if (hits.Count == 0 && plan.SourceTypes.Count > 0)
            hits = await SearchVectorsAsync(new[] { vectors[0] }, plan with { SourceTypes = Array.Empty<string>() }, ct);

        // PLAN_ADDENDUM.md 7.7: no question text in the ordinary logs — only the threshold and the outcome.
        if (hits.Count == 0 && direct.Count == 0)
            _logger.LogInformation("لا نتائج فوق الحد {MinScore}", _aiOptions.MinScore);

        if (direct.Count == 0) return hits;
        var seen = new HashSet<int>(direct.Select(h => h.ChunkId));
        return direct.Concat(hits.Where(h => seen.Add(h.ChunkId))).Take(plan.TopK).ToList();
    }

    /// <summary>Runs every rewritten query and keeps the best score per chunk.</summary>
    private async Task<List<LibraryHit>> SearchVectorsAsync(IReadOnlyList<float[]> vectors, QueryPlan plan, CancellationToken ct)
    {
        var best = new Dictionary<int, LibraryHit>();
        foreach (var vector in vectors)
        {
            foreach (var hit in await _cache.SearchAsync(_db, vector, plan.TopK, plan.SourceTypes, ct))
                if (!best.TryGetValue(hit.ChunkId, out var kept) || hit.Score > kept.Score)
                    best[hit.ChunkId] = hit;
        }

        // PLAN.md 2.3: every source kind keeps its own similarity floor instead of one global bar.
        var ranked = best.Values.Where(h => h.Score >= _aiOptions.ThresholdFor(h.SourceType)).ToList();
        if (ranked.Count == 0) return ranked;

        // PLAN.md 2.1 + 2.2: the question's kind orders the source kinds (order and weight only — no kind is
        // dropped), and pubmed studies rank by evidence tier, then by the newest year.
        var ordered = QueryRouter.OrderedTypes(plan.Kind);
        ranked.Sort((a, b) =>
        {
            var aPubmed = a.SourceType == LibrarySourceTypes.PubMed;
            var bPubmed = b.SourceType == LibrarySourceTypes.PubMed;
            if (aPubmed && bPubmed)
            {
                var tier = QueryRouter.EvidenceTier(a.Evidence).CompareTo(QueryRouter.EvidenceTier(b.Evidence));
                if (tier != 0) return tier;
                var year = (b.Year ?? 0).CompareTo(a.Year ?? 0);
                if (year != 0) return year;
                return b.Score.CompareTo(a.Score);
            }

            var weight = (b.Score * QueryRouter.WeightFor(b.SourceType, ordered))
                .CompareTo(a.Score * QueryRouter.WeightFor(a.SourceType, ordered));
            if (weight != 0) return weight;
            return b.Score.CompareTo(a.Score);
        });

        return ranked.Take(plan.TopK).ToList();
    }

    /// <summary>
    /// The strings to embed: the router's English rewording first, then one string per drug that mixes the
    /// trade name the doctor typed with the spellings sold in Egypt.
    /// </summary>
    private async Task<List<string>> BuildQueriesAsync(QueryPlan plan, IReadOnlyList<BrandMatch> brands, CancellationToken ct)
    {
        var queries = new List<string> { plan.SearchQuery };
        if (!string.IsNullOrWhiteSpace(plan.Terms))
            queries.Add($"{plan.SearchQuery} {plan.Terms}");

        foreach (var match in brands)
            AddBrandQuery(queries, await _brands.BrandsForAsync(_db, match.Inn, ct), match.Inn, match.Brand);

        foreach (var drug in plan.DrugNames)
            AddBrandQuery(queries, await _brands.BrandsForAsync(_db, drug, ct), drug, null);

        return queries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueries)
            .ToList();
    }

    /// <summary>Adds \"&lt;INN&gt; &lt;trade name as typed&gt; &lt;trade names sold in Egypt&gt;\" once.</summary>
    private static void AddBrandQuery(List<string> queries, IReadOnlyList<string> egyptian, string inn, string? brand)
    {
        var words = new List<string> { inn };
        if (!string.IsNullOrWhiteSpace(brand)) words.Add(brand);
        if (words.Count == 1 && egyptian.Count == 0) return;

        words.AddRange(egyptian.Take(4));
        var query = string.Join(' ', words.Distinct(StringComparer.OrdinalIgnoreCase));
        if (!queries.Any(q => q.Contains(query, StringComparison.OrdinalIgnoreCase))) queries.Add(query);
    }

    /// <summary>
    /// PLAN.md 2.4 — the Egyptian brand name the doctor typed, or the drug the router resolved, goes straight to
    /// the chunks whose title is the active ingredient's own label, before the semantic pass. When the label holds
    /// several of its sections, the one the question is about (dose, interaction, …) wins the pick.
    /// </summary>
    private async Task<List<LibraryHit>> DirectLabelHitsAsync(QueryPlan plan, IReadOnlyList<BrandMatch> brands, CancellationToken ct)
    {
        var names = brands.Select(b => b.Inn).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        if (names.Count == 0)
            names = plan.DrugNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        if (names.Count == 0) return new List<LibraryHit>();

        var hint = SectionHint(plan.Kind);
        var labels = await _db.LibraryChunks.AsNoTracking()
            .Where(c => c.SourceType == LibrarySourceTypes.DrugLabel && c.Title != null)
            .Select(c => new { c.Id, c.Title, c.Section })
            .ToListAsync(ct);

        var picked = new List<int>();
        foreach (var inn in names)
        {
            var word = inn.Split(' ').First();
            foreach (var row in labels
                .Where(c => c.Title!.Contains(inn, StringComparison.OrdinalIgnoreCase)
                    || (word.Length >= 5 && c.Title.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => hint is null || (c.Section?.Contains(hint, StringComparison.OrdinalIgnoreCase) ?? false) ? 0 : 1)
                .Take(2))
                picked.Add(row.Id);
        }

        if (picked.Count == 0) return new List<LibraryHit>();

        var ids = picked.Distinct().ToList();
        var rows = await _db.LibraryChunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Take(4)
            .Select(c => new
            {
                c.Id,
                Title = c.Title ?? c.Document!.Title,
                c.Document!.FileName,
                c.PageNumber,
                c.Text,
                c.Section,
                c.SourceUrl,
                c.SourceType,
                c.Evidence,
                c.Year
            })
            .ToListAsync(ct);
        return rows
            .Select(c => new LibraryHit(c.Id, c.Title, c.FileName, c.PageNumber, c.Text, 0.98, c.Section, c.SourceUrl, c.SourceType, c.Evidence, c.Year))
            .ToList();
    }

    private static string? SectionHint(QueryKind kind) => kind switch
    {
        QueryKind.Drug => "dosage",
        QueryKind.Interaction => "interaction",
        QueryKind.Indication => "indic",
        _ => null
    };

    /// <summary>Removes a reference file from disk and from the index; returns false while indexing is running.</summary>
    public async Task<bool> DeleteAsync(string relativeName)
    {
        if (!IndexLock.Wait(0)) return false;
        try
        {
            var path = Path.GetFullPath(Path.Combine(_libraryPath, relativeName));
            if (path.StartsWith(_libraryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                File.Delete(path);

            var docs = await _db.LibraryDocuments.Where(d => d.FileName == relativeName).ToListAsync();
            var docIds = docs.Select(d => d.Id).ToList();
            var chunkIds = await _db.LibraryChunks.Where(c => docIds.Contains(c.DocumentId)).Select(c => c.Id).ToListAsync();
            _db.LibraryDocuments.RemoveRange(docs);
            await _db.SaveChangesAsync();
            await _vectors.DeleteAsync(chunkIds);
            Status.VectorChunks = await _vectors.CountAsync();
            _cache.Invalidate();
            _brands.Invalidate();
            return true;
        }
        finally
        {
            IndexLock.Release();
        }
    }

    /// <summary>Starts indexing in the background; returns false if a run is already in progress.</summary>
    public bool StartReindex()
    {
        if (!IndexLock.Wait(0)) return false;
        Status.IsRunning = true;
        Status.Errors.Clear();

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<MedicalLibraryService>();
                await service.ReindexCoreAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Library indexing failed");
                Status.Errors.Add(ex.Message);
            }
            finally
            {
                Status.IsRunning = false;
                Status.CurrentFile = null;
                Status.LastRunUtc = DateTime.UtcNow;
                IndexLock.Release();
            }
        });
        return true;
    }

    /// <summary>Indexes new or changed files and drops documents whose files were removed.</summary>
    private async Task ReindexCoreAsync()
    {
        var files = Directory.EnumerateFiles(_libraryPath, "*", SearchOption.AllDirectories)
            .Where(DocumentTextExtractor.IsSupported)
            .ToList();
        var existing = await _db.LibraryDocuments.ToListAsync();

        var relativeNames = files.Select(f => Path.GetRelativePath(_libraryPath, f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = existing.Where(d => !relativeNames.Contains(d.FileName)).ToList();
        if (removed.Count > 0)
        {
            var removedDocIds = removed.Select(d => d.Id).ToList();
            var removedChunks = await _db.LibraryChunks.Where(c => removedDocIds.Contains(c.DocumentId)).Select(c => c.Id).ToListAsync();
            _db.LibraryDocuments.RemoveRange(removed);
            await _db.SaveChangesAsync();
            await _vectors.DeleteAsync(removedChunks);
        }

        Status.TotalFiles = files.Count;
        Status.ProcessedFiles = 0;

        foreach (var file in files)
        {
            var name = Path.GetRelativePath(_libraryPath, file);
            Status.CurrentFile = name;
            try
            {
                var info = new FileInfo(file);
                var doc = existing.FirstOrDefault(d => d.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (doc != null && doc.FileSize == info.Length && doc.FileModifiedUtc == info.LastWriteTimeUtc && doc.ChunkCount > 0)
                    continue;

                await IndexFileAsync(file, name, info, doc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index {File}", name);
                Status.Errors.Add($"{name}: {ex.Message}");
            }
            finally
            {
                Status.ProcessedFiles++;
            }
        }

        _cache.Invalidate();
        await BackfillSourceTypesAsync();
        await BackfillVectorsAsync();
        _brands.Invalidate();
    }

    /// <summary>
    /// Labels chunks that were indexed before source routing existed (and re-labels them when a file is
    /// renamed). Only the label column is written — no text is re-read and no vector recomputed.
    /// </summary>
    private async Task BackfillSourceTypesAsync(CancellationToken ct = default)
    {
        var docs = await _db.LibraryDocuments.AsNoTracking()
            .Select(d => new { d.Id, d.FileName })
            .ToListAsync(ct);
        if (docs.Count == 0) return;

        var labelled = 0;
        foreach (var doc in docs)
        {
            var type = LibrarySourceTypes.FromFileName(doc.FileName);
            labelled += await _db.LibraryChunks
                .Where(c => c.DocumentId == doc.Id && c.SourceType != type)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.SourceType, type), ct);
        }

        if (labelled > 0) _logger.LogInformation("صُنِّف {Count} مقطع بنوع المصدر", labelled);

        // The vector store keeps its own copy of the label, for the chunks it holds.
        if (_vectors.Ready && await _vectors.UntaggedCountAsync(ct) > 0)
        {
            var lastId = 0;
            while (true)
            {
                var rows = await _db.LibraryChunks.AsNoTracking()
                    .Where(c => c.Id > lastId)
                    .OrderBy(c => c.Id)
                    .Take(500)
                    .Select(c => new { c.Id, c.SourceType, c.Document!.FileName })
                    .ToListAsync(ct);
                if (rows.Count == 0) break;
                lastId = rows[^1].Id;

                await _vectors.BackfillSourceTypesAsync(
                    rows.Select(r => (r.Id, r.SourceType ?? LibrarySourceTypes.FromFileName(r.FileName))).ToList(), ct);
            }
        }
    }

    /// <summary>
    /// Copies vectors for chunks that were indexed before the vector store existed. Cheap when everything
    /// is already there: it compares counts first, then walks ids in bulk.
    /// </summary>
    private async Task BackfillVectorsAsync(CancellationToken ct = default)
    {
        Status.VectorStoreReady = _vectors.Ready;
        if (!_vectors.Ready)
        {
            Status.VectorChunks = 0;
            return;
        }

        var chunks = await _db.LibraryChunks.CountAsync(ct);
        var known = await _vectors.ExistingIdsAsync(ct);
        if (known.Count < chunks)
        {
            _logger.LogInformation("نسخ المتجهات الناقصة: {Missing} من {Total}", chunks - known.Count, chunks);
            var lastId = 0;
            while (true)
            {
                var page = await _db.LibraryChunks.AsNoTracking()
                    .Where(c => c.Id > lastId)
                    .OrderBy(c => c.Id)
                    .Take(2000)
                    .Select(c => new { c.Id, c.SourceType, FileName = c.Document!.FileName })
                    .ToListAsync(ct);
                if (page.Count == 0) break;
                lastId = page[^1].Id;

                var missing = page.Where(r => !known.Contains(r.Id)).ToList();
                if (missing.Count == 0) continue;

                var ids = missing.Select(r => r.Id).ToList();
                var stored = await _db.LibraryChunks.AsNoTracking()
                    .Where(c => ids.Contains(c.Id))
                    .Select(c => new { c.Id, c.Embedding })
                    .ToListAsync(ct);
                var byId = stored.ToDictionary(r => r.Id);

                var todo = new List<(int Id, float[] Vector, string? SourceType)>(missing.Count);
                foreach (var row in missing)
                {
                    if (!byId.TryGetValue(row.Id, out var hit) || hit.Embedding.Length == 0) continue;
                    todo.Add((row.Id, LibraryVectorCache.ToVector(hit.Embedding),
                        row.SourceType ?? LibrarySourceTypes.FromFileName(row.FileName)));
                }
                if (todo.Count == 0) continue;

                Status.CurrentFile = $"فهرسة المتجهات ({known.Count:N0}/{chunks:N0})";
                await _vectors.UpsertAsync(todo, ct);
                foreach (var (id, _, _) in todo) known.Add(id);
                Status.CurrentFile = $"فهرسة المتجهات ({known.Count:N0}/{chunks:N0})";
            }
        }

        Status.VectorChunks = await _vectors.CountAsync(ct);
        Status.CurrentFile = null;
    }

    private async Task IndexFileAsync(string path, string name, FileInfo info, LibraryDocument? doc)
    {
        var title = Path.GetFileNameWithoutExtension(name);
        var sourceType = LibrarySourceTypes.FromFileName(name);
        List<LibraryPiece> pieces;
        if (ChunkFileReader.IsChunkFile(path))
        {
            var (sources, chunks) = ChunkFileReader.Read(path, Status.Errors);
            pieces = chunks;
            if (sources.Count > 0) title = string.Join("، ", sources);
        }
        else
        {
            pieces = DocumentTextExtractor.Extract(path)
                .SelectMany(p => TextChunker.Chunk(p.Text).Select(t => new LibraryPiece(p.PageNumber, t)))
                .ToList();
        }
        if (pieces.Count == 0)
            throw new InvalidOperationException("الملف لا يحتوي على نص قابل للقراءة");

        // Knowledge-base files hold thousands of chunks; when one is regenerated, only new or edited chunks are re-embedded.
        var previous = new Dictionary<string, byte[]>();
        if (doc != null)
            foreach (var c in await _db.LibraryChunks.AsNoTracking().Where(c => c.DocumentId == doc.Id).Select(c => new { c.Text, c.Embedding }).ToListAsync())
                previous.TryAdd(c.Text, c.Embedding);

        var embeddings = new byte[pieces.Count][];
        var missing = new List<int>();
        for (var i = 0; i < pieces.Count; i++)
        {
            if (previous.TryGetValue(pieces[i].Text, out var stored)) embeddings[i] = stored;
            else missing.Add(i);
        }

        for (var b = 0; b < missing.Count; b += EmbedBatchSize)
        {
            if (missing.Count > EmbedBatchSize) Status.CurrentFile = $"{name} ({b}/{missing.Count})";
            var batch = missing.Skip(b).Take(EmbedBatchSize).ToList();
            var vectors = await _ai.EmbedAsync(batch.Select(i => pieces[i].Text).ToList(), isQuery: false);
            for (var j = 0; j < batch.Count; j++) embeddings[batch[j]] = LibraryVectorCache.ToBytes(vectors[j]);
        }

        if (doc != null)
        {
            // The old chunks are replaced below, so their vectors must not linger in the store.
            var superseded = await _db.LibraryChunks.Where(c => c.DocumentId == doc.Id).ToListAsync();
            _db.RemoveRange(superseded);
            await _vectors.DeleteAsync(superseded.Select(c => c.Id).ToList());
        }

        // A reindexed file keeps its document's id: cited answers point at it through the index row, so the row is
        // updated in place instead of being replaced — a fresh id would break every citation of an older answer.
        var record = doc ?? new LibraryDocument
        {
            FileName = name,
            Title = title,
            FileSize = info.Length,
            FileModifiedUtc = info.LastWriteTimeUtc
        };
        record.ChunkCount = pieces.Count;
        record.Chunks = pieces.Select((p, i) => new LibraryChunk
        {
            Index = i,
            PageNumber = p.PageNumber,
            Title = p.Title,
            Section = p.Section,
            SourceUrl = p.Url,
            SourceType = sourceType,
            Evidence = p.Evidence,
            Year = p.Year,
            Text = p.Text,
            Embedding = embeddings[i]
        }).ToList();
        if (doc == null)
            _db.LibraryDocuments.Add(record);
        await _db.SaveChangesAsync();

        // The vectors are also mirrored into PostgreSQL so searches never touch the SQLite copy.
        if (_vectors.Ready)
        {
            await _vectors.UpsertAsync(record.Chunks.Select((c, i) => (c.Id, LibraryVectorCache.ToVector(embeddings[i]), (string?)sourceType)).ToList());
            Status.VectorChunks = await _vectors.CountAsync();
        }
    }
}
