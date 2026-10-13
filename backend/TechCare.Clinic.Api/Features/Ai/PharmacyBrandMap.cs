using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;

namespace TechCare.Clinic.Api.Features.Ai;

/// <summary>An Egyptian brand name found in the question, together with the international name behind it.</summary>
public sealed record BrandMatch(string Brand, string Inn);

/// <summary>
/// Egyptian brand names and their international (INN) names, learned from the indexed catalogue instead of
/// a hand-maintained dictionary. The mapping is one line in each catalogue chunk:
///   title: "Acetaminophen (Pain Relief, ...) - Egypt: Cetal, Doliprane, ..."  (drugs.jsonl)
///   title: "Quetiapine - Egyptian brands: Quitapex"                           (egypt_drugs.jsonl)
///   text:  "الأسماء التجارية في مصر: دوليبران، سيتال، ..."
/// — the international name is in the title while the products sold in Egypt sit in the body. That is
/// enough to answer "كويتابيكس" questions, and the mapping follows the catalogue whenever the clinic
/// refreshes it. It is read once into memory and only consulted for names the semantic pass cannot resolve.
/// </summary>
public partial class PharmacyBrandMap
{
    /// <summary>The catalogue files are the ones whose name ends with "drugs.jsonl".</summary>
    private const string CatalogueSuffix = "drugs.jsonl";

    private const int PageSize = 500;
    private const int MaxMatches = 4;
    private const int MaxBrandsPerInn = 8;
    private const int MaxBrandWords = 3;

    private static readonly Catalogue EmptyCatalogue = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    private readonly ILogger<PharmacyBrandMap> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile Catalogue? _catalogue;

    public PharmacyBrandMap(ILogger<PharmacyBrandMap> logger) => _logger = logger;

    /// <summary>Forgets the learned catalogue; called after the library is re-indexed.</summary>
    public void Invalidate() => _catalogue = null;

    /// <summary>
    /// Every Egyptian brand name mentioned in <paramref name="text"/>, resolved to its international name.
    /// Matching is word based on a normalised form (Arabic letter shapes and separators folded), so
    /// "البروفين" and "بروفين" both find ibuprofen without matching inside longer words.
    /// </summary>
    public async Task<IReadOnlyList<BrandMatch>> ResolveAsync(ClinicDbContext db, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<BrandMatch>();
        var catalogue = await GetCatalogueAsync(db, ct);
        if (catalogue.BrandToInn.Count == 0) return Array.Empty<BrandMatch>();

        var words = WordRegex().Matches(text).Select(m => Normalize(m.Value)).ToList();
        var found = new List<BrandMatch>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < words.Count; i++)
        {
            var key = "";
            for (var n = 1; n <= MaxBrandWords && i + n <= words.Count; n++)
            {
                key += words[i + n - 1];
                if (key.Length < 3 || !catalogue.BrandToInn.TryGetValue(key, out var match)) continue;

                if (seen.Add(match.Inn))
                {
                    found.Add(match);
                    if (found.Count == MaxMatches) return found;
                }

                i += n - 1;
                break;
            }
        }

        return found;
    }

    /// <summary>Brand names sold in Egypt for an international name, e.g. quetiapine to كويتابيكس.</summary>
    public async Task<IReadOnlyList<string>> BrandsForAsync(ClinicDbContext db, string inn, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inn)) return Array.Empty<string>();
        var catalogue = await GetCatalogueAsync(db, ct);
        return catalogue.InnToBrands.TryGetValue(Normalize(inn), out var brands) ? brands : Array.Empty<string>();
    }

    private async Task<Catalogue> GetCatalogueAsync(ClinicDbContext db, CancellationToken ct)
    {
        if (_catalogue is { } ready) return ready;

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_catalogue is { } again) return again;

            var catalogue = await LoadAsync(db, ct);
            _catalogue = catalogue;
            _logger.LogInformation("خريطة الأسماء التجارية المصرية: {Brands} اسم تجاري لـ {Inns} مادة فعالة",
                catalogue.BrandToInn.Count, catalogue.InnToBrands.Count);
            return catalogue;
        }
        catch (Exception ex)
        {
            // Deliberately not cached: the next question may arrive after the database comes back.
            _logger.LogWarning(ex, "تعذّر تحميل الأسماء التجارية المصرية من الكتالوج");
            return EmptyCatalogue;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<Catalogue> LoadAsync(ClinicDbContext db, CancellationToken ct)
    {
        var brandToInn = new Dictionary<string, BrandMatch>(StringComparer.Ordinal);
        var innToBrands = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var docIds = await db.LibraryDocuments.AsNoTracking()
            .Where(d => d.FileName.EndsWith(CatalogueSuffix))
            .Select(d => d.Id)
            .ToListAsync(ct);
        if (docIds.Count == 0) return EmptyCatalogue;

        var lastId = 0;
        while (true)
        {
            var page = await db.LibraryChunks.AsNoTracking()
                .Where(c => c.Id > lastId && docIds.Contains(c.DocumentId))
                .OrderBy(c => c.Id)
                .Take(PageSize)
                .Select(c => new { c.Id, c.Title, c.Text })
                .ToListAsync(ct);
            if (page.Count == 0) break;
            lastId = page[^1].Id;

            foreach (var row in page)
            {
                var header = TitleLine(row.Text) ?? row.Title;
                var inn = InnFromTitle(header);
                if (inn == null) continue;

                var innKey = Normalize(inn);
                foreach (var brand in BrandsInText(row.Text).Concat(BrandsInTitle(header)))
                {
                    var brandKey = Normalize(brand);
                    if (brandKey.Length < 3 || brandKey == innKey) continue;

                    brandToInn.TryAdd(brandKey, new BrandMatch(brand, inn));
                    if (innToBrands.TryGetValue(innKey, out var list))
                    {
                        if (list.Count < MaxBrandsPerInn && !list.Contains(brand, StringComparer.Ordinal)) list.Add(brand);
                    }
                    else
                    {
                        innToBrands[innKey] = new List<string> { brand };
                    }
                }
            }
        }

        return new Catalogue(brandToInn, innToBrands);
    }

    /// <summary>Knowledge-base chunks open with "Title: ..."; that line names the drug the chunk is about.</summary>
    private static string? TitleLine(string text)
    {
        var match = TitleLineRegex().Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>International name in front of the marker, e.g. "Acetaminophen (Pain Relief) - Egypt: Cetal".</summary>
    private static string? InnFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Both markers start with "- Egypt" ("- Egypt: ..." and "- Egyptian brands: ...").
        var cut = title.LastIndexOf("- Egypt", StringComparison.OrdinalIgnoreCase);
        var raw = cut > 0 ? title[..cut] : title;

        raw = ParentheticalRegex().Replace(raw, " ");
        raw = StrengthRegex().Replace(raw, " ");
        raw = WhitespaceRegex().Replace(raw, " ").Trim().Trim('-', ':', '،', ' ');
        if (raw.Length is < 2 or > 120 || !LatinRegex().IsMatch(raw)) return null;
        return raw;
    }

    /// <summary>Latin brand names after the marker in the title ("... - Egypt: Cetal, Doliprane").</summary>
    private static IEnumerable<string> BrandsInTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) yield break;

        var cut = title.LastIndexOf("- Egypt", StringComparison.OrdinalIgnoreCase);
        if (cut <= 0) yield break;

        var colon = title.IndexOf(':', cut);
        if (colon < 0) yield break;

        foreach (var brand in SplitBrands(title[(colon + 1)..])) yield return brand;
    }

    /// <summary>Arabic trade names on the "الأسماء التجارية في مصر: ..." line of the chunk body.</summary>
    private static IEnumerable<string> BrandsInText(string text)
    {
        var match = BrandLineRegex().Match(text);
        if (!match.Success) yield break;

        foreach (var brand in SplitBrands(match.Groups[1].Value)) yield return brand;
    }

    private static IEnumerable<string> SplitBrands(string value)
    {
        foreach (var part in value.Split(BrandSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var brand = part.Trim().Trim('.', '(', ')', '؛');
            if (brand.Length is > 1 and < 60) yield return brand;
        }
    }

    private static readonly char[] BrandSeparators = { '،', ',', ';', '|', '/', '·' };

    /// <summary>
    /// Lookup form: letters and digits only, lower case, with Arabic letter variants folded so that
    /// "أموكسيل" and "اموكسيل" — the same name typed two ways — share one key.
    /// </summary>
    private static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            var c = ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ئ' => 'ي',
                'ک' => 'ك',
                'ی' => 'ي',
                _ => char.ToLowerInvariant(ch)
            };

            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }

        return sb.ToString();
    }

    private sealed record Catalogue(Dictionary<string, BrandMatch> BrandToInn, Dictionary<string, List<string>> InnToBrands);

    [GeneratedRegex(@"^Title:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex TitleLineRegex();

    [GeneratedRegex(@"الأسماء التجارية[^\n:]*:\s*(.+)")]
    private static partial Regex BrandLineRegex();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ParentheticalRegex();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s*(?:mg|mcg|µg|g|ml|iu|%|tabs?|caps?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StrengthRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[A-Za-z]")]
    private static partial Regex LatinRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
