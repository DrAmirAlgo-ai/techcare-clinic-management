using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TechCare.Clinic.Api.Features.Ai;

public class AiOptions
{
    /// <summary>Any OpenAI-compatible endpoint: LM Studio locally, or vLLM on a GPU server later.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234/v1";
    public string? ApiKey { get; set; }
    public string ChatModel { get; set; } = "qwen3.5-9b";
    public string EmbeddingModel { get; set; } = "text-embedding-nomic-embed-text-v1.5";
    /// <summary>nomic-embed expects task prefixes; set both to empty for models that don't.</summary>
    public string EmbeddingDocumentPrefix { get; set; } = "search_document: ";
    public string EmbeddingQueryPrefix { get; set; } = "search_query: ";
    public string LibraryPath { get; set; } = "MedicalLibrary";
    public int TimeoutSeconds { get; set; } = 300;
    /// <summary>
    /// PLAN_ADDENDUM.md 9.4: the interactive budget for the chat calls the doctor waits on — far shorter than the
    /// 300 s the embedding/reindex path is allowed, configurable through appsettings or the AI options section.
    /// </summary>
    public int InteractiveTimeoutSeconds { get; set; } = 60;
    /// <summary>PLAN_ADDENDUM.md 9.2: whether the AI interaction log stores the question/answer text; off by default.</summary>
    public bool LogQuestionText { get; set; }
    public int TopK { get; set; } = 4;
    /// <summary>Cap on library excerpts per question (~4k tokens) so they fit the chat model's context with the answer.</summary>
    public int MaxLibraryContextChars { get; set; } = 16000;
    public int MaxAnswerTokens { get; set; } = 1200;
    /// <summary>Model for the short translate/classify call; empty means <see cref="ChatModel"/>.</summary>
    public string RouterModel { get; set; } = "";
    /// <summary>
    /// Minimum cosine similarity a library chunk must reach to count as evidence. The question is
    /// translated to English first, so a matching English chunk scores ≈0.7+ while an unrelated one sits
    /// around 0.6; a kept chunk has to clear this higher bar so a loose neighbour is not treated as evidence.
    /// Stays the floor for pubmed and egypt_catalog; the three core kinds below carry their own, lower bars.
    /// </summary>
    public double MinScore { get; set; } = 0.75;
    /// <summary>PLAN.md 2.3 — a dose line inside a label is evidence even at a looser match than a PubMed abstract is.</summary>
    public double DrugLabelMinScore { get; set; } = 0.70;
    /// <summary>PLAN.md 2.3 — guidelines answer clinical questions; the same looser bar applies to them.</summary>
    public double GuidelineMinScore { get; set; } = 0.70;
    /// <summary>PLAN.md 2.3 — the clinic's own uploads carry local authority and get the same looser bar.</summary>
    public double UploadedMinScore { get; set; } = 0.70;
    /// <summary>Translate the Arabic question into English medical terms before embedding it.</summary>
    public bool TranslateQueries { get; set; } = true;
    /// <summary>Optional cloud model for questions that carry no patient data. Off by default: nothing leaves the clinic.</summary>
    public ExternalAiOptions External { get; set; } = new();

    /// <summary>A copy the per-tenant resolver may override without touching the appsettings instance (PLAN.md 1.2).</summary>
    public AiOptions Clone() => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        ChatModel = ChatModel,
        EmbeddingModel = EmbeddingModel,
        EmbeddingDocumentPrefix = EmbeddingDocumentPrefix,
        EmbeddingQueryPrefix = EmbeddingQueryPrefix,
        LibraryPath = LibraryPath,
        TimeoutSeconds = TimeoutSeconds,
        InteractiveTimeoutSeconds = InteractiveTimeoutSeconds,
        LogQuestionText = LogQuestionText,
        TopK = TopK,
        MaxLibraryContextChars = MaxLibraryContextChars,
        MaxAnswerTokens = MaxAnswerTokens,
        RouterModel = RouterModel,
        MinScore = MinScore,
        DrugLabelMinScore = DrugLabelMinScore,
        GuidelineMinScore = GuidelineMinScore,
        UploadedMinScore = UploadedMinScore,
        TranslateQueries = TranslateQueries,
        External = External
    };

    /// <summary>PLAN.md 2.3 — the similarity floor of one source kind.</summary>
    public double ThresholdFor(string? sourceType) => sourceType switch
    {
        LibrarySourceTypes.DrugLabel => DrugLabelMinScore,
        LibrarySourceTypes.Guideline => GuidelineMinScore,
        LibrarySourceTypes.Uploaded => UploadedMinScore,
        _ => MinScore
    };
}

/// <summary>
/// Optional OpenAI-compatible cloud provider (Gemini, DeepSeek, …). It is only ever used for general
/// questions that have no patient context attached, and it stays disabled until it is configured.
/// </summary>
public class ExternalAiOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "";
    /// <summary>Route only questions that have no patient record attached to them.</summary>
    public bool GeneralQuestionsOnly { get; set; } = true;
}

public record ChatMessage(string Role, string Content);

public class AiUnavailableException : Exception
{
    /// <summary>
    /// PLAN_ADDENDUM.md 9.4: the provider's HTTP status when the failure came from a real response (not a
    /// dropped connection), so the caller can tell a transient 5xx apart from everything else.
    /// </summary>
    public int? StatusCode { get; }

    public AiUnavailableException(string message, Exception? inner = null) : base(message, inner) { }

    public AiUnavailableException(string message, int statusCode, Exception? inner = null) : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

public partial class LmStudioClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly AiSettingsService? _settings;
    private AiOptions? _active;

    public LmStudioClient(HttpClient http, IOptions<AiOptions> options, AiSettingsService? settings = null)
    {
        _options = options.Value;
        _settings = settings;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        // The base address and the key are applied per call in ActiveAsync: the owner may keep a different
        // address, key and model per clinic in the database (PLAN.md 1.2), and the shared HttpClient must not
        // keep whatever the first request used.
    }

    /// <summary>
    /// The options in force for the current request (PLAN.md 1.2): the appsettings defaults with the owner's
    /// database rows applied. Until the first async call has resolved them, the defaults are what is returned.
    /// </summary>
    public AiOptions Options => _active ?? _options;

    private async Task<AiOptions> ActiveAsync(CancellationToken ct)
    {
        if (_active == null)
        {
            _active = _settings == null ? _options : await _settings.ResolveForCurrentTenantAsync(ct);
            _http.BaseAddress = new Uri(_active.BaseUrl.TrimEnd('/') + "/");
            if (!string.IsNullOrWhiteSpace(_active.ApiKey))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _active.ApiKey);
            else
                _http.DefaultRequestHeaders.Remove("Authorization");
        }

        return _active;
    }

    // The three model calls below are virtual so the integration tests can drop in a fake that never
    // opens a socket (PLAN_ADDENDUM.md 0.1). Production behaviour is unchanged.
    public virtual async Task<string> ChatAsync(IEnumerable<ChatMessage> messages, double temperature = 0.3, CancellationToken ct = default,
        int? maxTokens = null, string? model = null)
    {
        var options = await ActiveAsync(ct);
        var body = new
        {
            model = string.IsNullOrWhiteSpace(model) ? options.ChatModel : model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            temperature,
            max_tokens = maxTokens ?? options.MaxAnswerTokens,
            stream = false
        };

        using var doc = await WithInteractiveBudgetAsync(token => PostAsync("chat/completions", body, token), ct);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        return StripReasoning(content);
    }

    /// <summary>True when a cloud provider is configured for general (patient-free) questions.</summary>
    public bool ExternalReady =>
        _options.External.Enabled && !string.IsNullOrWhiteSpace(_options.External.BaseUrl) && !string.IsNullOrWhiteSpace(_options.External.Model);

    /// <summary>
    /// Asks the configured cloud provider. Callers must only use this for questions that carry no patient
    /// data — the local model stays in charge of anything clinical.
    /// </summary>
    public virtual async Task<string> ChatExternalAsync(IEnumerable<ChatMessage> messages, double temperature = 0.3,
        int? maxTokens = null, CancellationToken ct = default)
    {
        var ext = _options.External;
        if (!ExternalReady) throw new AiUnavailableException("المزوّد الخارجي غير مُفعّل");

        var body = new
        {
            model = ext.Model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            temperature,
            max_tokens = maxTokens ?? _options.MaxAnswerTokens,
            stream = false
        };

        // The same interactive budget as the local server (PLAN_ADDENDUM.md 9.4): a short cap and one retry.
        using var doc = await WithInteractiveBudgetAsync(token => ExternalPostAsync(ext, body, token, _options.TimeoutSeconds), ct);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return StripReasoning(content);
    }

    /// <summary>One round-trip to the configured cloud provider; cancellation propagates as-is for the budget layer to interpret.</summary>
    private static async Task<JsonDocument> ExternalPostAsync(ExternalAiOptions ext, object body, CancellationToken token, int backstopSeconds)
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri(ext.BaseUrl.TrimEnd('/') + "/"),
            // The linked budget token is the effective cap; the client's own timeout stays only as a backstop.
            Timeout = TimeSpan.FromSeconds(Math.Max(1, backstopSeconds))
        };
        if (!string.IsNullOrWhiteSpace(ext.ApiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ext.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("chat/completions", body, Json, token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (token.IsCancellationRequested)
                throw;
            throw ex is TaskCanceledException
                ? new AiUnavailableException("تعذّر الرد من المزوّد الخارجي خلال الوقت المسموح", ex)
                : new AiUnavailableException("تعذّر الاتصال بالمزوّد الخارجي", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new AiUnavailableException($"خطأ من المزوّد الخارجي ({(int)response.StatusCode})", (int)response.StatusCode);
            return JsonDocument.Parse(text);
        }
    }

    public virtual async Task<List<float[]>> EmbedAsync(IReadOnlyList<string> inputs, bool isQuery, CancellationToken ct = default)
    {
        var options = await ActiveAsync(ct);
        var prefix = isQuery ? options.EmbeddingQueryPrefix : options.EmbeddingDocumentPrefix;
        var body = new { model = options.EmbeddingModel, input = inputs.Select(i => prefix + i).ToArray() };

        using var doc = await PostAsync("embeddings", body, ct);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .OrderBy(d => d.GetProperty("index").GetInt32())
            .Select(d => d.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray())
            .ToList();
    }

    public virtual async Task<(bool Reachable, List<string> Models)> StatusAsync(CancellationToken ct = default)
    {
        await ActiveAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("models", cts.Token));
            var models = doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "").ToList();
            return (true, models);
        }
        catch (Exception)
        {
            return (false, new List<string>());
        }
    }

    /// <summary>
    /// PLAN_ADDENDUM.md 9.4: the interactive budget — the chat call the doctor waits on gets
    /// <see cref="AiOptions.InteractiveTimeoutSeconds"/> instead of the full <see cref="AiOptions.TimeoutSeconds"/>,
    /// and exactly one extra attempt when the first one fails in a transient way (a dropped connection or a
    /// server-side 5xx), so a single flaky packet does not cost the doctor the question. A hung server is not
    /// retried: its timeout is the budget's own, and the answer is the clear Arabic timeout message.
    /// </summary>
    private async Task<JsonDocument> WithInteractiveBudgetAsync(Func<CancellationToken, Task<JsonDocument>> post, CancellationToken ct)
    {
        var seconds = Math.Max(1, Options.InteractiveTimeoutSeconds);
        using var first = CancellationTokenSource.CreateLinkedTokenSource(ct);
        first.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await post(first.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // The budget (not the request) fired: the doctor gets the clear Arabic timeout, not a raw 500.
            throw new AiUnavailableException($"استغرق رد خدمة الذكاء الاصطناعي وقتاً أطول من الحد المسموح ({seconds} ثانية)");
        }
        catch (AiUnavailableException ex) when (IsTransient(ex))
        {
            using var second = CancellationTokenSource.CreateLinkedTokenSource(ct);
            second.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                return await post(second.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                throw new AiUnavailableException($"استغرق رد خدمة الذكاء الاصطناعي وقتاً أطول من الحد المسموح ({seconds} ثانية)");
            }
        }
    }

    /// <summary>A failure worth one retry: the provider answered with a 5xx, or the connection itself failed.</summary>
    private static bool IsTransient(AiUnavailableException ex) =>
        ex.StatusCode is 500 or 501 or 502 or 503 or 504 || ex.InnerException is HttpRequestException;

    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(path, body, Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw;   // the request or its budget was cancelled: the budget layer decides what it means
            // PLAN_ADDENDUM.md 9.4: a clear Arabic failure instead of a long silent wait, timeout told apart
            // from a plain connection error.
            throw ex is TaskCanceledException
                ? new AiUnavailableException("تعذّر الرد من خادم الذكاء الاصطناعي خلال الوقت المسموح", ex)
                : new AiUnavailableException("تعذّر الاتصال بخادم الذكاء الاصطناعي", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new AiUnavailableException($"خطأ من خادم الذكاء الاصطناعي ({(int)response.StatusCode})", (int)response.StatusCode);
            return JsonDocument.Parse(text);
        }
    }

    /// <summary>Qwen thinking models may inline their reasoning in &lt;think&gt; tags; the doctor only needs the answer.</summary>
    private static string StripReasoning(string content) => ThinkBlock().Replace(content, "").Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex(@"<think>[\s\S]*?(</think>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();
}
