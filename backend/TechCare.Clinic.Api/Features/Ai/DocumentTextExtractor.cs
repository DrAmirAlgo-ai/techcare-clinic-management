using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace TechCare.Clinic.Api.Features.Ai;

public record PageText(int? PageNumber, string Text);

/// <summary>A piece of text ready to embed; Title/Section/Url come from knowledge-base chunk files.</summary>
public record LibraryPiece(int? PageNumber, string Text, string? Title = null, string? Section = null, string? Url = null,
    string? Evidence = null, int? Year = null);

public static class DocumentTextExtractor
{
    public static readonly string[] SupportedExtensions = { ".pdf", ".txt", ".md", ChunkFileReader.Extension };

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static List<PageText> Extract(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var pdf = PdfDocument.Open(path);
            return pdf.GetPages()
                .Select(p => new PageText(p.Number, Normalize(string.Join(" ", p.GetWords().Select(w => w.Text)))))
                .Where(p => p.Text.Length > 0)
                .ToList();
        }

        return new List<PageText> { new(null, Normalize(File.ReadAllText(path, Encoding.UTF8))) };
    }

    private static string Normalize(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.Replace("\r", ""), @"[ \t]+", " ").Trim();
}

/// <summary>
/// Reads pre-chunked knowledge-base files (one JSON object per line with at least "text", optionally
/// "title", "sections", "url", "source") as produced by knowledge/download_medical_kb.py.
/// Chunks are kept exactly as written so their headers and boundaries survive into the index.
/// </summary>
public static partial class ChunkFileReader
{
    public const string Extension = ".jsonl";

    /// <summary>Well within nomic-embed's 2048-token window; longer lines are re-split.</summary>
    private const int MaxChunkChars = 6000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class Line
    {
        public string? Text { get; set; }
        public string? Title { get; set; }
        public List<string>? Sections { get; set; }
        public string? Url { get; set; }
        public string? Source { get; set; }
        public string? Updated { get; set; }
    }

    public static bool IsChunkFile(string path) =>
        Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    /// <returns>The distinct "source" values (for the document title) and the chunks; bad lines are reported in <paramref name="warnings"/>.</returns>
    public static (List<string> Sources, List<LibraryPiece> Pieces) Read(string path, List<string> warnings)
    {
        var sources = new List<string>();
        var pieces = new List<LibraryPiece>();
        var badLines = 0;
        var lineNumber = 0;

        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            Line? line;
            try { line = JsonSerializer.Deserialize<Line>(raw, JsonOptions); }
            catch (JsonException) { line = null; }

            var text = line?.Text?.Replace("\r", "").Trim();
            if (string.IsNullOrEmpty(text))
            {
                if (badLines++ == 0) warnings.Add($"{Path.GetFileName(path)}: سطر {lineNumber} غير صالح");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line!.Source) && !sources.Contains(line.Source))
                sources.Add(line.Source);

            var title = Clip(line.Title, 300);
            var section = Clip(line.Sections is { Count: > 0 } s ? string.Join("، ", s) : null, 300);
            var url = Clip(line.Url, 500);
            // PLAN.md 2.2 — pubmed chunk headers carry "Evidence: Meta-analysis | Journal: …" and the line's
            // "updated" field carries the year; both let a search rank the studies, not just their similarity.
            var evidence = Clip(EvidenceRegex().Match(text).Groups[1].Value, 60);
            var year = ParseYear(line.Updated);
            var parts = text.Length <= MaxChunkChars ? new[] { text } : TextChunker.Chunk(text, MaxChunkChars / 2, 300);
            pieces.AddRange(parts.Select(t => new LibraryPiece(null, t, title, section, url, evidence, year)));
        }

        if (badLines > 1) warnings.Add($"{Path.GetFileName(path)}: تم تجاهل {badLines} سطر غير صالح");
        return (sources, pieces);
    }

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value.Trim() : value[..max].Trim();

    private static int? ParseYear(string? updated)
    {
        var m = YearRegex().Match(updated ?? string.Empty);
        return m.Success && int.TryParse(m.Groups[1].Value, out var year) && year is >= 1990 and < 2100 ? year : null;
    }

    [GeneratedRegex(@"\bEvidence:\s*([^|\n]{2,40})")]
    private static partial Regex EvidenceRegex();

    [GeneratedRegex(@"(\d{4})")]
    private static partial Regex YearRegex();
}

public static class TextChunker
{
    /// <summary>
    /// Splits text into overlapping windows, preferring paragraph/sentence boundaries,
    /// small enough that several fit into a 8k-token context together with the question.
    /// </summary>
    public static IEnumerable<string> Chunk(string text, int size = 900, int overlap = 150)
    {
        if (text.Length <= size)
        {
            if (text.Length > 40) yield return text;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + size, text.Length);
            if (end < text.Length)
            {
                var window = text[start..end];
                var cut = Math.Max(window.LastIndexOf("\n\n", StringComparison.Ordinal),
                          Math.Max(window.LastIndexOf(". ", StringComparison.Ordinal), window.LastIndexOf('\n')));
                if (cut > size / 2) end = start + cut + 1;
            }

            var chunk = text[start..end].Trim();
            if (chunk.Length > 40) yield return chunk;
            if (end >= text.Length) break;
            start = Math.Max(end - overlap, start + 1);
        }
    }
}
