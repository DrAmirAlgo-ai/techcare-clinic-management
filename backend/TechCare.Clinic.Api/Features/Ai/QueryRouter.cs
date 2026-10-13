using System.Text.Json;

namespace TechCare.Clinic.Api.Features.Ai;

/// <summary>What the doctor is really asking for — it decides which kind of evidence is worth retrieving.</summary>
public enum QueryKind
{
    /// <summary>Dose, alternatives, side effects, contraindications, mechanism — drug centric.</summary>
    Drug,

    /// <summary>Drug–drug or drug–food interaction.</summary>
    Interaction,

    /// <summary>What a medicine is used for.</summary>
    Indication,

    /// <summary>Symptoms, differential diagnosis, workup, management.</summary>
    Clinical,

    /// <summary>The doctor asks for a study, a trial or the body of evidence itself.</summary>
    Research,

    /// <summary>The library cannot answer it (prices, stock, appointments, unrelated questions).</summary>
    General
}

/// <summary>How one question should be searched: what to embed, where to look, and how much to trust it.</summary>
public sealed record QueryPlan(
    QueryKind Kind,
    string SearchQuery,
    string Terms,
    IReadOnlyList<string> SourceTypes,
    IReadOnlyList<string> DrugNames,
    int TopK,
    bool Weak);

/// <summary>
/// Turns the doctor's Arabic question into an English library search.
/// The library is indexed in English (DailyMed labels, StatPearls guidelines, PubMed abstracts), so an
/// Arabic question embedded as-is lands near random chunks — which is how an unrelated "Depression"
/// article could outrank the actual drug label. One short, cheap chat call therefore
/// (a) classifies the question, (b) resolves Egyptian brand names to their international name, and
/// (c) writes the English query that is actually embedded.
/// When the router model is unreachable, or rewriting is switched off, the question is searched
/// unchanged — exactly the previous behaviour.
/// </summary>
public class QueryRouter
{
    /// <summary>The evidence kinds the library holds; anything else a model returns is ignored.</summary>
    public static readonly string[] KnownSourceTypes = LibrarySourceTypes.All;

    private const int PlannerTokens = 400;

    private static readonly string[] InteractionHints = { "تداخل", "مع بعض", "مع بعضهم", "سوا", "ياخدهم", "يأخدهم", "interaction" };
    private static readonly string[] IndicationHints = { "استخدامات", "استعمالات", "يستخدم", "بتستخدم", "بيستخدم", "دواعي", "indication" };
    private static readonly string[] ClinicalHints = { "ألم", "الم", "ضيق", "سخونية", "حرارة", "أعراض", "اعراض", "تشخيص", "احتمالات", "أسباب", "اسباب", "تورم" };
    private static readonly string[] ResearchHints = { "دراسة", "دراسات", "بحث سريري", "إثباتات", "evidence", "trial", "studies", "study" };

    private const string PlannerPrompt = """
        You plan searches for a clinic's medical library. The library holds exactly these source types:
        - drug_label: English prescribing information (FDA / DailyMed style).
        - guideline: English clinical guidelines and review articles (StatPearls / OpenEvidence style).
        - egypt_catalog: Egyptian market products - the English active ingredient plus the Arabic brand names sold in Egypt.
        - pubmed: English research abstracts.
        - uploaded: PDFs the clinic uploaded itself.

        The library never contains billing, appointment, insurance or stock data.

        Read the doctor's Arabic question and reply with ONE JSON object and nothing else:
        {
          "kind": "drug|interaction|indication|clinical|research|admin",
          "drugs": ["english generic (INN) names, lowercase, never brand names"],
          "english_query": "the question restated in English medical wording, phrased the way a drug label or guideline would",
          "terms": "extra English keywords, comma separated",
          "search_types": ["the source types worth searching, best first"],
          "top_k": 4,
          "weak": false
        }

        Rules:
        - Translate everything to English. The doctor is answered in Arabic, but the library is English.
        - Resolve Egyptian brand names to the international name: كويتابيكس = quetiapine, سيدوفاج = metformin, بروفين = ibuprofen, أوجمنتين = amoxicillin clavulanate.
        - search_types must list every source type, in this order: drug_label, egypt_catalog, guideline, pubmed, uploaded. Never return pubmed alone, and never drop uploaded PDFs or guidelines.
        - kind=interaction for anything about combining drugs. kind=drug for dose, alternatives, side effects, contraindications. kind=indication for "what is it used for".
        - kind=clinical for symptoms, diagnosis, differential, management.
        - kind=research when the doctor asks for a study, a trial or the body of evidence (e.g. "ما الدليل على...").
        - kind=admin when the library cannot contain the answer, including unrelated chit-chat.
        - weak=true when a drug name is unknown or imaginary, or when no clear medical term can be extracted.
        - top_k: 6 for interactions or broad clinical questions, 3 for one precise fact, never above 8.
        """;

    private readonly LmStudioClient _ai;
    private readonly ILogger<QueryRouter> _logger;

    public QueryRouter(LmStudioClient ai, ILogger<QueryRouter> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    /// <summary>Whether questions are rewritten before they are embedded.</summary>
    public bool Enabled => _ai.Options.TranslateQueries;

    public async Task<QueryPlan> PlanAsync(string question, int defaultTopK, CancellationToken ct = default)
    {
        if (!Enabled) return KeywordPlan(question, defaultTopK);

        try
        {
            var model = string.IsNullOrWhiteSpace(_ai.Options.RouterModel) ? null : _ai.Options.RouterModel;
            var raw = await _ai.ChatAsync(
                new[] { new ChatMessage("system", PlannerPrompt), new ChatMessage("user", question) },
                temperature: 0, ct: ct, maxTokens: PlannerTokens, model: model);

            if (Parse(raw, defaultTopK) is { } plan)
            {
                // PLAN_ADDENDUM.md 7.7: no question text in the ordinary logs — kind, source types and the weak
                // flag are enough to debug routing.
                _logger.LogInformation("خطة البحث: {Kind} [{Types}] weak={Weak}",
                    plan.Kind, string.Join(", ", plan.SourceTypes), plan.Weak);
                return plan;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر تخطيط السؤال — سيتم البحث بنص السؤال كما هو");
        }

        return KeywordPlan(question, defaultTopK);
    }

    private static QueryPlan? Parse(string raw, int defaultTopK)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
        var root = doc.RootElement;

        var query = Str(root, "english_query");
        if (string.IsNullOrWhiteSpace(query)) query = Str(root, "terms");
        if (string.IsNullOrWhiteSpace(query)) return null;

        var kind = ParseKind(Str(root, "kind"));
        // The model may narrow search_types to PubMed. Every question is still matched against the whole
        // library: drug labels, the Egypt catalogue, guidelines, PubMed, and uploaded PDFs.
        var types = AllSources();

        var drugs = Strings(root, "drugs")
            .Select(d => d.ToLowerInvariant())
            .Where(d => d.Length > 1)
            .Distinct()
            .Take(4)
            .ToList();

        var topK = defaultTopK;
        if (root.TryGetProperty("top_k", out var k) && k.ValueKind == JsonValueKind.Number)
            topK = Math.Clamp(k.GetInt32(), 1, 8);

        var weak = root.TryGetProperty("weak", out var w) && w.ValueKind == JsonValueKind.True;

        return new QueryPlan(kind, query.Trim(), Str(root, "terms"), types, drugs, topK, weak);
    }

    /// <summary>Every question searches the whole library. <paramref name="kind"/> still classifies the question, but it no longer drops PDFs, guidelines, or PubMed.</summary>
    public static IReadOnlyList<string> DefaultTypes(QueryKind kind)
    {
        _ = kind;
        return KnownSourceTypes;
    }

    /// <summary>
    /// PLAN.md 2.1 — the order in which the source kinds count for one question: the drug question leads with the
    /// label, the clinical one with the guideline, the research one with PubMed. Nothing is dropped — every kind
    /// is still searched; this order only weighs the hits against each other.
    /// </summary>
    public static IReadOnlyList<string> OrderedTypes(QueryKind kind) => kind switch
    {
        QueryKind.Drug or QueryKind.Interaction or QueryKind.Indication
            => new[] { LibrarySourceTypes.DrugLabel, LibrarySourceTypes.EgyptCatalog, LibrarySourceTypes.Guideline, LibrarySourceTypes.Uploaded, LibrarySourceTypes.PubMed },
        QueryKind.Clinical
            => new[] { LibrarySourceTypes.Guideline, LibrarySourceTypes.Uploaded, LibrarySourceTypes.PubMed, LibrarySourceTypes.DrugLabel, LibrarySourceTypes.EgyptCatalog },
        QueryKind.Research
            => new[] { LibrarySourceTypes.PubMed, LibrarySourceTypes.Guideline, LibrarySourceTypes.DrugLabel, LibrarySourceTypes.EgyptCatalog, LibrarySourceTypes.Uploaded },
        _ => new[] { LibrarySourceTypes.EgyptCatalog, LibrarySourceTypes.DrugLabel, LibrarySourceTypes.Guideline, LibrarySourceTypes.Uploaded, LibrarySourceTypes.PubMed }
    };

    /// <summary>The weight of one source kind at its place in <paramref name="ordered"/>: 1.0, 0.9, 0.8, …</summary>
    public static double WeightFor(string? sourceType, IReadOnlyList<string> ordered)
    {
        var index = sourceType is null ? -1 : ordered.ToList().IndexOf(sourceType);
        return index < 0 ? 0.6 : 1.0 - 0.1 * index;
    }

    /// <summary>
    /// PLAN.md 2.2 — the rank of a PubMed evidence line, best first: guideline, meta-analysis,
    /// systematic review, RCT, then anything else (or none at all).
    /// </summary>
    public static int EvidenceTier(string? evidence)
    {
        var e = (evidence ?? string.Empty).ToLowerInvariant();
        if (e.Contains("guideline")) return 0;
        if (e.Contains("meta-analysis") || e.Contains("meta analysis")) return 1;
        if (e.Contains("systematic")) return 2;
        if (e.Contains("randomized") || e.Contains("randomised") || e.Contains("rct")) return 3;
        return 4;
    }

    private static List<string> AllSources() => KnownSourceTypes.ToList();

    private static QueryKind ParseKind(string kind) => kind.ToLowerInvariant() switch
    {
        "interaction" => QueryKind.Interaction,
        "indication" => QueryKind.Indication,
        "clinical" => QueryKind.Clinical,
        "research" => QueryKind.Research,
        "admin" or "general" => QueryKind.General,
        _ => QueryKind.Drug
    };

    /// <summary>Arabic keyword fallback, used when the router model is off or unreachable.</summary>
    private static QueryPlan KeywordPlan(string question, int topK)
    {
        var q = question.ToLowerInvariant();
        var kind = InteractionHints.Any(q.Contains) ? QueryKind.Interaction
            : IndicationHints.Any(q.Contains) ? QueryKind.Indication
            : ClinicalHints.Any(q.Contains) ? QueryKind.Clinical
            : ResearchHints.Any(q.Contains) ? QueryKind.Research
            : QueryKind.Drug;

        return new QueryPlan(kind, question, question, AllSources(), Array.Empty<string>(), topK, false);
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";

    private static List<string> Strings(JsonElement root, string name)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(name, out var v)) return list;

        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    list.Add(s.Trim());
        }
        else if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } single)
        {
            list.AddRange(single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return list;
    }
}
