using Microsoft.Data.Sqlite;

namespace TechCare.Clinic.Api.Tests;

/// <summary>Temporary: reports what occupies the developer's clinic.db so we can decide how to shrink it.</summary>
public class DbSizeDiagnosticsTests
{
    [Fact]
    public async Task Diagnostics()
    {
        var path = Path.GetFullPath(@"..\..\..\..\TechCare.Clinic.Api\clinic.db");
        Assert.True(File.Exists(path), path);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        long pages = 0, pageSize = 0, freeList = 0;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA page_count"; pages = (long)(await cmd.ExecuteScalarAsync())!;
            cmd.CommandText = "PRAGMA page_size"; pageSize = (long)(await cmd.ExecuteScalarAsync())!;
            cmd.CommandText = "PRAGMA freelist_count"; freeList = (long)(await cmd.ExecuteScalarAsync())!;
        }

        var tables = new List<(string Name, long Rows)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EFMigrationsHistory' ORDER BY name";
            var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                await using var countCmd = connection.CreateCommand();
                countCmd.CommandText = $"SELECT count(*) FROM \"{name}\"";
                tables.Add((name, (long)(await countCmd.ExecuteScalarAsync())!));
            }
        }
        var top = tables.OrderByDescending(t => t.Rows).Take(12)
            .Select(t => $"{t.Name}={t.Rows}")
            .ToList();

        throw new Exception($"FILE_MB={(pages * pageSize) / 1048576} USED_MB={(Math.Max(0, (pages - freeList) * pageSize) / 1048576)} FREE_MB={(freeList * pageSize) / 1048576} TOP=[{string.Join("|", top)}]");
    }
}
