using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;

namespace TechCare.Clinic.Api.Features.Ai;

public record AskResult(string Answer, List<LibraryHit> Sources);
public record DifferentialCandidate(string Diagnosis, int Probability, string Evidence, IReadOnlyList<string> Warnings);
public record DifferentialResult(IReadOnlyList<DifferentialCandidate> Candidates, string? Note, List<LibraryHit> Sources);

public class ClinicalAssistantService
{
    private const string SystemPrompt = """
        أنت مساعد طبي ذكي يعمل داخل نظام إدارة عيادة، وتخاطب الطبيب المعالج فقط (وليس المريض).
        قواعد إلزامية:
        - اكتب بالعربية الفصحى المبسطة، واكتب أسماء الأمراض والأدوية والتحاليل بالإنجليزية بين قوسين عند الحاجة.
        - أنت أداة دعم قرار: قدّم اقتراحات ونقاط للمراجعة، ولا تقدّم تشخيصاً نهائياً أو قراراً علاجياً ملزماً.
        - لا تخترع جرعات أو معلومات. إذا لم تكن المعلومة في السياق فقل ذلك صراحة، ونبّه الطبيب إلى مراجعة مصدر دوائي معتمد للجرعات والتداخلات.
        - ممنوع الحشو: بلا مقدمة، بلا خاتمة، بلا تكرار للسؤال، وبلا شرح عام لا يغيّر القرار.
        - كل نقطة سطر أو سطرين فقط.
        """;

    private readonly ClinicDbContext _db;
    private readonly LmStudioClient _ai;
    private readonly MedicalLibraryService _library;
    private readonly QueryRouter _router;
    private readonly PharmacyBrandMap _brands;
    private readonly ILogger<ClinicalAssistantService> _logger;

    public ClinicalAssistantService(ClinicDbContext db, LmStudioClient ai, MedicalLibraryService library,
        QueryRouter router, PharmacyBrandMap brands, ILogger<ClinicalAssistantService> logger)
    {
        _db = db;
        _ai = ai;
        _library = library;
        _router = router;
        _brands = brands;
        _logger = logger;
    }

    public async Task<string?> PatientSummaryAsync(int patientId, CancellationToken ct)
    {
        var context = await BuildPatientContextAsync(patientId, maxVisits: 10, ct);
        if (context == null) return null;

        var prompt = $"""
            هذا ملف المريض من سجلات العيادة:

            {context}

            اكتب ملخصاً سريعاً للطبيب قبل الكشف، بالعناوين التالية:
            1. الصورة العامة (العمر، الأمراض المزمنة، الحساسية).
            2. آخر التشخيصات وتطورها.
            3. الأدوية الموصوفة مؤخراً.
            4. تطور العلامات الحيوية إن وجدت (ضغط، سكر، وزن).
            5. نقاط تستحق الانتباه (متابعات فائتة، حساسية مع أدوية موصوفة، تداخلات دوائية محتملة يجب التحقق منها).
            اعتمد فقط على البيانات المذكورة.
            """;

        return await _ai.ChatAsync(new[] { new ChatMessage("system", SystemPrompt), new ChatMessage("user", prompt) }, 0.2, ct);
    }

    public async Task<AskResult> AskAsync(string question, int? patientId, CancellationToken ct)
    {
        // The library is indexed in English, so the question is classified, its Egyptian brand names are
        // resolved to international names, and an English query is written — the Arabic text itself would
        // land next to unrelated chunks.
        var plan = await _router.PlanAsync(question, _ai.Options.TopK, ct);
        var brands = await _brands.ResolveAsync(_db, question, ct);
        if (brands.Count > 0)
            _logger.LogInformation("أسماء تجارية في السؤال: {Brands}",
                string.Join("، ", brands.Select(b => $"{b.Brand} ← {b.Inn}")));

        var hits = new List<LibraryHit>();
        var used = 0;
        foreach (var hit in await _library.SearchAsync(plan, brands, ct))
        {
            if (hits.Count > 0 && used + hit.Text.Length > _ai.Options.MaxLibraryContextChars) break;
            hits.Add(hit);
            used += hit.Text.Length;
        }
        var patientContext = patientId is int id ? await BuildPatientContextAsync(id, maxVisits: 5, ct) : null;

        var sb = new StringBuilder();
        if (hits.Count > 0)
        {
            sb.AppendLine("مقتطفات من المكتبة الطبية للعيادة:");
            for (var i = 0; i < hits.Count; i++)
                sb.AppendLine($"[{i + 1}] ({hits[i].DocumentTitle}{(hits[i].PageNumber is int p ? $"، صفحة {p}" : "")}{(hits[i].SourceType is string t ? $"، {t}" : "")})\n{hits[i].Text}\n");
        }
        else
        {
            sb.AppendLine("(المكتبة الطبية فارغة أو لم يتم فهرستها بعد.)");
        }

        if (patientContext != null)
        {
            sb.AppendLine($"\nبيانات المريض المعني:\n{patientContext}");
            sb.AppendLine(await BuildPatientConstraintsAsync(patientId!.Value, ct));
        }

        sb.AppendLine($"\nسؤال الطبيب: {question}");
        sb.AppendLine("""

            قواعد الإجابة:
            - اعتمد أولاً على مقتطفات المكتبة، واذكر رقم المصدر بين أقواس مربعة مثل [1] بعد كل معلومة مأخوذة منه.
            - أجب مباشرة على سؤال الطبيب بصيغة تناسب طبيعة السؤال، بدون عناوين ثابتة وبلا حشو.
            - اكتب الجرعة فقط إذا وردت في مقتطف مصدره drug_label أو guideline، واذكر رقم المصدر بعدها. وإلا فاكتب «الجرعة غير موثقة في المقتطفات — راجع مصدراً دوائياً معتمداً».
            - إذا لم تجد الإجابة في المقتطفات فقل ذلك بوضوح، ثم أضف سطراً واحداً فقط يبدأ بـ «من المعرفة العامة (غير موثق من المكتبة):».
            """);

        if (plan.Weak)
            sb.AppendLine("- السؤال خارج نطاق المكتبة مباشرة: اذكر ذلك صراحة في بداية الإجابة.");

        // PLAN.md 2.5: the answer no longer carries its own 700-token cap; the option's 1200 applies.
        var answer = await _ai.ChatAsync(
            new[] { new ChatMessage("system", SystemPrompt), new ChatMessage("user", sb.ToString()) },
            0.2, ct);
        return new AskResult(answer, hits);
    }

    /// <summary>
    /// PLAN_FULL.md 3.2 — the second opinion: the differential probabilities for the diagnosis the doctor is
    /// writing, each grounded on the library retrieval of 2.1/2.2 and checked against the patient's
    /// constraints of 2.6 (allergy, chronic conditions, current prescription).
    /// </summary>
    public async Task<DifferentialResult> DifferentialAsync(int patientId, string diagnosis, string? chiefComplaint,
        string? examination, CancellationToken ct)
    {
        // The diagnosis travels through the same router + retrieval pipeline as a clinical question: it is
        // reworded in English and matched against the whole library under the per-source thresholds.
        var plan = await _router.PlanAsync(diagnosis, _ai.Options.TopK, ct);
        var brands = await _brands.ResolveAsync(_db, diagnosis, ct);

        var hits = new List<LibraryHit>();
        var used = 0;
        foreach (var hit in await _library.SearchAsync(plan, brands, ct))
        {
            if (hits.Count > 0 && used + hit.Text.Length > _ai.Options.MaxLibraryContextChars) break;
            hits.Add(hit);
            used += hit.Text.Length;
        }

        var context = await BuildPatientContextAsync(patientId, maxVisits: 5, ct);
        if (context == null) return new DifferentialResult(Array.Empty<DifferentialCandidate>(), null, hits);

        var sb = new StringBuilder();
        if (hits.Count > 0)
        {
            sb.AppendLine("مقتطفات من المكتبة الطبية للعيادة:");
            for (var i = 0; i < hits.Count; i++)
                sb.AppendLine($"[{i + 1}] ({hits[i].DocumentTitle}{(hits[i].PageNumber is int p ? $"، صفحة {p}" : "")}{(hits[i].SourceType is string t ? $"، {t}" : "")})\n{hits[i].Text}\n");
        }
        else
        {
            sb.AppendLine("(المكتبة الطبية فارغة أو لم يُعثر على دليل لهذا التشخيص.)");
        }

        sb.AppendLine($"\nبيانات المريض المعني:\n{context}");
        sb.AppendLine(await BuildPatientConstraintsAsync(patientId, ct));
        if (!string.IsNullOrWhiteSpace(chiefComplaint)) sb.AppendLine($"\nالشكوى الحالية: {chiefComplaint}");
        if (!string.IsNullOrWhiteSpace(examination)) sb.AppendLine($"نتيجة الفحص الحالية: {examination}");

        sb.AppendLine($"\nالتشخيص الذي يكتبه الطبيب الآن: {diagnosis}");
        sb.AppendLine("""

            مهمتك: قيّم هذا التشخيص واقترح الاحتمالات التفريقية. أجب فقط بكائن JSON واحد دون أي نص قبله أو بعده بهذا الشكل:
            {
              "candidates": [
                {
                  "diagnosis": "اسم التشخيص بالعربية أو الإنجليزية",
                  "probability": احتمال صحيح من 0 إلى 100,
                  "evidence": "لماذا هذا الاحتمال بسطرين كحد أقصى؛ إن كان الدليل من المكتبة أذكر رقم المصدر بين قوسين مثل [1]",
                  "warnings": ["تحذير قصير واحد لكل خطر حقيقي من قيود المريض (حساسية، مرض مزمن، تداخل مع الروشتة الحالية)، ومصفوفة فارغة إن لم يوجد"]
                }
              ],
              "note": "سطر واحد إن كان التشخيص ناقص الدليل أو تحتاج لتحاليل أو لتشخيص يجب استبعاده، وإلا نص فارغ"
            }
            قواعد:
            - من 3 إلى 5 احتمالات مرتبة تنازلياً بالاحتمال؛ أولها التشخيص المكتوب نفسه.
            - لا تخترع مصادر؛ إن لم تجد دليلاً في المقتطفات فاكتفِ بالتفسير بلا رقم مصدر.
            - كل تحذير يجب أن يناسب بيانات هذا المريض تحديداً، ولا تتجاوز تحذيرين في الاحتمال الواحد.
            """);

        var raw = await _ai.ChatAsync(
            new[] { new ChatMessage("system", SystemPrompt), new ChatMessage("user", sb.ToString()) },
            0.2, ct, maxTokens: 900);
        return ParseDifferential(raw, hits);
    }

    /// <summary>The model may wrap the JSON in fences or stray words; only the outermost {...} block counts.</summary>
    private static DifferentialResult ParseDifferential(string raw, List<LibraryHit> sources)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return new DifferentialResult(Array.Empty<DifferentialCandidate>(), null, sources);

        DifferentialCandidate[] candidates = [];
        string? note = null;
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            var list = new List<DifferentialCandidate>();
            if (root.TryGetProperty("candidates", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = JsonStr(item, "diagnosis");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var probability = item.TryGetProperty("probability", out var p) && p.ValueKind == JsonValueKind.Number
                        ? Math.Clamp(p.GetInt32(), 0, 100)
                        : 0;
                    var warnings = new List<string>();
                    if (item.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array)
                        foreach (var x in w.EnumerateArray())
                            if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s)
                                warnings.Add(s.Trim());
                    list.Add(new DifferentialCandidate(name, probability, JsonStr(item, "evidence"), warnings));
                }
            }

            note = JsonStr(root, "note");
            candidates = list.Take(5).ToArray();
        }
        catch (JsonException)
        {
            // A malformed answer surfaces as an empty list — the UI then says the model returned nothing usable.
        }

        return new DifferentialResult(candidates, string.IsNullOrWhiteSpace(note) ? null : note, sources);
    }

    private static string JsonStr(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";

    public async Task<string?> DraftDocumentAsync(int patientId, string kind, string? referTo, string? notes, CancellationToken ct)
    {
        var context = await BuildPatientContextAsync(patientId, maxVisits: 5, ct);
        if (context == null) return null;

        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _db.CurrentClinicId, ct);

        // PLAN.md 2.7: the draft is grounded on the library — the patient's latest diagnosis is searched once, and
        // a real hit becomes a reference the document cites instead of an invented one.
        var diagnosis = await _db.Visits.AsNoTracking()
            .Where(v => v.PatientId == patientId && v.Diagnosis != null && v.Diagnosis != "")
            .OrderByDescending(v => v.VisitDate)
            .Select(v => v.Diagnosis)
            .FirstOrDefaultAsync(ct);
        string? reference = null;
        if (diagnosis != null)
        {
            var hits = (await _library.SearchAsync(diagnosis, 2, ct))
                .Where(h => h.Score >= _ai.Options.ThresholdFor(h.SourceType))
                .ToList();
            if (hits.Count > 0)
                reference = string.Join('\n', hits.Select(h =>
                    $"- {h.DocumentTitle} ({h.SourceType}{(h.Year is int y ? $"، {y}" : "")})"));
        }

        var what = kind == "referral"
            ? $"خطاب تحويل رسمي{(string.IsNullOrWhiteSpace(referTo) ? "" : $" إلى: {referTo}")}، يوضح سبب التحويل والتاريخ المرضي المختصر والعلاج الحالي والمطلوب من الجهة المحول إليها."
            : "تقرير طبي رسمي عن حالة المريض يتضمن التاريخ المرضي والتشخيص والعلاج والتوصيات.";

        var prompt = $"""
            اكتب مسودة {what}
            الطبيب: {clinic.DoctorName} - {clinic.Specialty} - {clinic.Name}.
            {(string.IsNullOrWhiteSpace(notes) ? "" : $"ملاحظات إضافية من الطبيب: {notes}")}

            بيانات المريض:
            {context}

            {(reference is null ? "" :
                $"مرجع من مكتبة العيادة على تشخيص المريض:\n{reference}\nاذكر هذا المرجع داخل المستند في موضعه المناسب بصيغة [1].\n\n")}
            اكتب المسودة جاهزة للطباعة بدون أي تعليقات جانبية، واترك [ ] مكان أي معلومة ناقصة.
            """;

        return await _ai.ChatAsync(new[] { new ChatMessage("system", SystemPrompt), new ChatMessage("user", prompt) }, 0.3, ct);
    }

    /// <summary>
    /// PLAN.md 2.6 — the patient file enters the answer prompt as explicit constraints the answer must apply,
    /// not only as descriptive text: the allergy is a prohibition, the chronic conditions a compatibility check,
    /// and the current prescription an interaction check.
    /// </summary>
    private async Task<string> BuildPatientConstraintsAsync(int patientId, CancellationToken ct)
    {
        var p = await _db.Patients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == patientId, ct);
        if (p == null) return string.Empty;

        var latest = await _db.Visits.AsNoTracking()
            .Where(v => v.PatientId == patientId && v.Prescription.Count > 0)
            .OrderByDescending(v => v.VisitDate)
            .Include(v => v.Prescription)
            .FirstOrDefaultAsync(ct);
        var prescription = latest?.Prescription.Select(i => $"{i.DrugName} {i.Dose} {i.Frequency}".Trim()).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("\nقيود ملف المريض (تُطبَّق على الإجابة):");
        sb.AppendLine($"- حساسية: {Or(p.Allergies)} — لا تقترح دواءً يحتمل أن يستثير هذه الحساسية، وأشر إلى الخطر إن اهتم السؤال بأدوية لها صلة بها.");
        sb.AppendLine($"- أمراض مزمنة: {Or(p.ChronicConditions)} — تحقق أن أي علاج تقترحه متوافق مع هذه الحالات.");
        sb.AppendLine(prescription is { Count: > 0 }
            ? $"- روشتة حالية: {string.Join("؛ ", prescription)} — افحص التداخلات بينها وبين أي دواء جديد تقترحه."
            : "- روشتة حالية: لا توجد روشتة مسجلة.");
        return sb.ToString();
    }

    private async Task<string?> BuildPatientContextAsync(int patientId, int maxVisits, CancellationToken ct)
    {
        var p = await _db.Patients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == patientId, ct);
        if (p == null) return null;

        var visits = await _db.Visits.AsNoTracking()
            .Where(v => v.PatientId == patientId)
            .OrderByDescending(v => v.VisitDate)
            .Take(maxVisits)
            .Include(v => v.Prescription)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        var age = p.BirthDate is DateOnly bd ? DateTime.Today.Year - bd.Year : (int?)null;
        sb.AppendLine($"- النوع: {p.Gender ?? "غير محدد"}، العمر: {(age?.ToString() ?? "غير محدد")}، فصيلة الدم: {p.BloodType ?? "غير محددة"}");
        sb.AppendLine($"- أمراض مزمنة: {Or(p.ChronicConditions)}");
        sb.AppendLine($"- حساسية: {Or(p.Allergies)}");
        if (!string.IsNullOrWhiteSpace(p.Notes)) sb.AppendLine($"- ملاحظات: {p.Notes}");

        if (visits.Count == 0)
        {
            sb.AppendLine("- لا توجد زيارات سابقة مسجلة.");
            return sb.ToString();
        }

        sb.AppendLine($"\nآخر {visits.Count} زيارات (الأحدث أولاً):");
        foreach (var v in visits)
        {
            sb.AppendLine($"* {v.VisitDate:yyyy-MM-dd}:");
            if (!string.IsNullOrWhiteSpace(v.ChiefComplaint)) sb.AppendLine($"  الشكوى: {v.ChiefComplaint}");
            if (!string.IsNullOrWhiteSpace(v.Examination)) sb.AppendLine($"  الفحص: {v.Examination}");
            if (!string.IsNullOrWhiteSpace(v.Diagnosis)) sb.AppendLine($"  التشخيص: {v.Diagnosis}");
            var vitals = new List<string>();
            if (!string.IsNullOrWhiteSpace(v.BloodPressure)) vitals.Add($"ضغط {v.BloodPressure}");
            if (v.Pulse != null) vitals.Add($"نبض {v.Pulse}");
            if (v.Temperature != null) vitals.Add($"حرارة {v.Temperature}");
            if (v.BloodSugar != null) vitals.Add($"سكر {v.BloodSugar}");
            if (v.WeightKg != null) vitals.Add($"وزن {v.WeightKg} كجم");
            if (vitals.Count > 0) sb.AppendLine($"  العلامات الحيوية: {string.Join("، ", vitals)}");
            if (v.Prescription.Count > 0)
                sb.AppendLine($"  الروشتة: {string.Join("؛ ", v.Prescription.Select(i => $"{i.DrugName} {i.Dose} {i.Frequency} {i.Duration}".Trim()))}");
            if (!string.IsNullOrWhiteSpace(v.LabRequests)) sb.AppendLine($"  تحاليل مطلوبة: {v.LabRequests}");
            if (v.FollowUpDate != null) sb.AppendLine($"  متابعة: {v.FollowUpDate:yyyy-MM-dd}");
        }

        return sb.ToString();
    }

    private static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "لا يوجد" : value;
}
