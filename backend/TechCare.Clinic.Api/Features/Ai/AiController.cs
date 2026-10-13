using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Ai;

public record AskRequest(string Question, int? PatientId);
public record DraftRequest(int PatientId, string Kind, string? ReferTo, string? Notes);
public record DifferentialRequest(int PatientId, string Diagnosis, string? ChiefComplaint, string? Examination);

public class AiUnavailableFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not AiUnavailableException ex) return;
        context.Result = new ObjectResult(new { message = ex.Message }) { StatusCode = StatusCodes.Status503ServiceUnavailable };
        context.ExceptionHandled = true;
    }
}

/// <summary>
/// PLAN.md 10.3 — the plan gate: a clinic on Basic, or a clinic whose own AI switch is off, is refused the
/// assistant before a single token is spent, with the Arabic explanation the UI shows. The status endpoint stays
/// open on purpose: it spends nothing and lets the panel see why the calls are refused.
/// </summary>
public class AiPlanFilter : IAsyncActionFilter
{
    private readonly ClinicDbContext _db;

    public AiPlanFilter(ClinicDbContext db) => _db = db;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (int.TryParse(context.HttpContext.User.FindFirstValue(ClaimNames.ClinicId), out var clinicId))
        {
            var clinic = await _db.Clinics.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clinicId);
            if (clinic != null && (clinic.Plan == ClinicPlan.Basic || !clinic.AiEnabled))
            {
                context.Result = new JsonResult(new
                {
                    message = "الذكاء الاصطناعي متاح في باقة الاحترافية فقط — راجع مالك المنصة لترقية الاشتراك"
                }) { StatusCode = StatusCodes.Status403Forbidden };
                return;
            }
        }

        await next();
    }
}

[ApiController]
[Route("api/ai")]
[Authorize(Policy = Permissions.Assistant)]
[TypeFilter(typeof(AiUnavailableFilter))]
public class AiController : ControllerBase
{
    /// <summary>PLAN_ADDENDUM.md 9.1: the line every generated summary, report or referral carries.</summary>
    private const string Disclaimer = "\n\n— هذه مسودة أعدها مساعد ذكاء اصطناعي لدعم القرار السريري، ولا تغني عن تقدير الطبيب المعالج.";

    private readonly ClinicalAssistantService _assistant;
    private readonly LmStudioClient _client;
    private readonly ClinicDbContext _db;
    private readonly VectorStore _vectors;
    private readonly AiSettingsService _aiSettings;

    public AiController(ClinicalAssistantService assistant, LmStudioClient client, ClinicDbContext db, VectorStore vectors,
        AiSettingsService aiSettings)
    {
        _assistant = assistant;
        _client = client;
        _db = db;
        _vectors = vectors;
        _aiSettings = aiSettings;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var (reachable, models) = await _client.StatusAsync(ct);
        var o = _client.Options;
        return Ok(new
        {
            reachable,
            baseUrl = o.BaseUrl,
            chatModel = o.ChatModel,
            embeddingModel = o.EmbeddingModel,
            chatModelLoaded = models.Contains(o.ChatModel),
            embeddingModelLoaded = models.Contains(o.EmbeddingModel),
            models,
            vectorStore = new { enabled = _vectors.Enabled, ready = _vectors.Ready }
        });
    }

    [TypeFilter(typeof(AiPlanFilter))]
    [HttpPost("patients/{patientId:int}/summary")]
    public async Task<IActionResult> Summary(int patientId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var summary = await _assistant.PatientSummaryAsync(patientId, ct);
        sw.Stop();
        if (summary == null) return NotFound();
        await AuditAsync("AiSummary", patientId);
        // PLAN_ADDENDUM.md 9.2: the request joins the interaction log — clinic, user, patient, kind, time.
        await LogInteractionAsync("summary", patientId, null, summary, Array.Empty<LibraryHit>(), sw.Elapsed);
        // PLAN_ADDENDUM.md 9.1: the generated summary carries the disclaimer line.
        return Ok(new { summary = summary.Trim() + Disclaimer });
    }

    [TypeFilter(typeof(AiPlanFilter))]
    [HttpPost("ask")]
    public async Task<IActionResult> Ask(AskRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Question) || r.Question.Length > 2000)
            return BadRequest(new { message = "يرجى كتابة السؤال (بحد أقصى 2000 حرف)" });
        if (r.PatientId is int pid && !await _db.Patients.AnyAsync(p => p.Id == pid, ct))
            return NotFound(new { message = "المريض غير موجود" });

        var sw = Stopwatch.StartNew();
        var result = await _assistant.AskAsync(r.Question.Trim(), r.PatientId, ct);
        sw.Stop();
        if (r.PatientId is int id) await AuditAsync("AiAsk", id);
        // PLAN_ADDENDUM.md 9.2: the question, its cited sources and the time it took join the interaction log.
        await LogInteractionAsync("ask", r.PatientId, r.Question.Trim(), result.Answer, result.Sources, sw.Elapsed);

        // PLAN.md 1.3: a cited source is openable as its full file, so every hit carries the id of the document
        // that owns it — the only reference a clinic role ever gets to the library (PLAN_ADDENDUM.md 7.3).
        var chunkIds = result.Sources.Select(s => s.ChunkId).Distinct().ToList();
        var documentIds = chunkIds.Count == 0
            ? new Dictionary<int, int>()
            : await _db.LibraryChunks.AsNoTracking()
                .Where(c => chunkIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.DocumentId);

        return Ok(new
        {
            answer = result.Answer,
            foundInLibrary = result.Sources.Count > 0,
            sources = result.Sources.Select((s, i) => new
            {
                n = i + 1, s.DocumentTitle, s.FileName, s.PageNumber, s.Score, s.Section, s.Url, s.SourceType, s.Evidence, s.Year,
                documentId = documentIds.TryGetValue(s.ChunkId, out var docId) ? docId : (int?)null,
                snippet = s.Body.Length > 400 ? s.Body[..400] + "…" : s.Body
            })
        });
    }

    // PLAN_FULL.md 3.2 — the second opinion: differential probabilities for the diagnosis the doctor is
    // writing, each candidate grounded on the library and checked against the patient's constraints.
    [TypeFilter(typeof(AiPlanFilter))]
    [HttpPost("differential")]
    public async Task<IActionResult> Differential(DifferentialRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Diagnosis) || r.Diagnosis.Length > 300)
            return BadRequest(new { message = "اكتب التشخيص أولاً ثم اطلب الرأي الثاني" });
        if (!await _db.Patients.AnyAsync(p => p.Id == r.PatientId, ct))
            return NotFound(new { message = "المريض غير موجود" });

        var sw = Stopwatch.StartNew();
        var result = await _assistant.DifferentialAsync(r.PatientId, r.Diagnosis.Trim(), r.ChiefComplaint, r.Examination, ct);
        sw.Stop();
        await AuditAsync("AiDifferential", r.PatientId);
        // PLAN_ADDENDUM.md 9.2: the differential request is logged with the candidates it produced.
        var candidateText = result.Candidates.Count == 0
            ? "لم يُرجع النموذج مرشحين قابلين للاستخدام"
            : string.Join("؛ ", result.Candidates.Select(c => $"{c.Diagnosis} ({c.Probability}%)"));
        await LogInteractionAsync("differential", r.PatientId, r.Diagnosis.Trim(), candidateText, result.Sources, sw.Elapsed);

        // Same cited-file contract as Ask (PLAN.md 1.3): every hit carries the id of the document that owns it.
        var chunkIds = result.Sources.Select(s => s.ChunkId).Distinct().ToList();
        var documentIds = chunkIds.Count == 0
            ? new Dictionary<int, int>()
            : await _db.LibraryChunks.AsNoTracking()
                .Where(c => chunkIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.DocumentId);

        return Ok(new
        {
            candidates = result.Candidates.Select(c => new { c.Diagnosis, c.Probability, c.Evidence, c.Warnings }),
            note = result.Note,
            foundInLibrary = result.Sources.Count > 0,
            sources = result.Sources.Select((s, i) => new
            {
                n = i + 1, s.DocumentTitle, s.FileName, s.PageNumber, s.Score, s.Section, s.Url, s.SourceType, s.Evidence, s.Year,
                documentId = documentIds.TryGetValue(s.ChunkId, out var docId) ? docId : (int?)null,
                snippet = s.Body.Length > 400 ? s.Body[..400] + "…" : s.Body
            })
        });
    }

    [TypeFilter(typeof(AiPlanFilter))]
    [HttpPost("draft")]
    public async Task<IActionResult> Draft(DraftRequest r, CancellationToken ct)
    {
        if (r.Kind is not ("report" or "referral"))
            return BadRequest(new { message = "نوع المستند غير معروف" });

        var sw = Stopwatch.StartNew();
        var text = await _assistant.DraftDocumentAsync(r.PatientId, r.Kind, r.ReferTo, r.Notes, ct);
        sw.Stop();
        if (text == null) return NotFound();
        await AuditAsync("AiDraft", r.PatientId);
        // PLAN_ADDENDUM.md 9.2: drafts join the interaction log like every other AI request.
        await LogInteractionAsync($"draft-{r.Kind}", r.PatientId, r.Notes, text, Array.Empty<LibraryHit>(), sw.Elapsed);
        // PLAN_ADDENDUM.md 9.1: the generated report or referral carries the disclaimer line.
        return Ok(new { text = text.Trim() + Disclaimer });
    }

    /// <summary>
    /// PLAN_ADDENDUM.md 9.2: one row in <c>AiInteractions</c> per AI request. The question/answer text is stored
    /// only when the clinic enabled it in the AI settings; the sources are always just their titles, never content.
    /// </summary>
    private async Task LogInteractionAsync(string kind, int? patientId, string? questionText, string? answerText,
        IReadOnlyList<LibraryHit> sources, TimeSpan elapsed)
    {
        var options = await _aiSettings.ResolveForCurrentTenantAsync();
        var sourceNames = sources is { Count: > 0 }
            ? sources.Take(10).Select(s => s.SourceType is { Length: > 0 } type ? $"{s.DocumentTitle} ({type})" : s.DocumentTitle).ToList()
            : new List<string>();

        _db.AiInteractions.Add(new AiInteraction
        {
            ClinicId = _db.CurrentClinicId,
            UserId = HttpContext.RequestServices.GetRequiredService<TenantContext>().UserId,
            PatientId = patientId,
            Kind = kind,
            Sources = sourceNames.Count == 0 ? "[]" : JsonSerializer.Serialize(sourceNames),
            DurationMs = (int)elapsed.TotalMilliseconds,
            QuestionText = options.LogQuestionText ? Clip(questionText, 4000) : null,
            AnswerText = options.LogQuestionText ? Clip(answerText, 8000) : null
        });
        await _db.SaveChangesAsync();
    }

    private static string? Clip(string? value, int max) =>
        value is { } text && text.Length > max ? text[..max] + "…" : value;

    private async Task AuditAsync(string action, int patientId)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            ClinicId = _db.CurrentClinicId,
            UserId = HttpContext.RequestServices.GetRequiredService<TenantContext>().UserId,
            Action = action,
            Entity = nameof(Patient),
            EntityId = patientId.ToString()
        });
        await _db.SaveChangesAsync();
    }
}
