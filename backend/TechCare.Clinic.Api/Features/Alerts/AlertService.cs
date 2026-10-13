using System.Text;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Ai;

namespace TechCare.Clinic.Api.Features.Alerts;

/// <summary>
/// PLAN_FULL.md 3.1 — the alerts shown while the doctor writes a visit: a registered allergy met in the new
/// prescription, an interaction between the written drugs (or with the patient's current prescription), and a
/// lab the diagnosis should carry. Built on 2.4 (Egyptian brand names are resolved to their international
/// names before any matching) and on 2.6 (the patient file's constraints are hard facts, not prose). The
/// checks are rule-based on purpose: this endpoint runs while the draft changes, never inside a long AI
/// round-trip.
/// </summary>
public enum AlertType { Serious, Moderate, Minor }

public record Alert(AlertType Type, string Message, string Details = "");

public record VisitContext
{
    public required Patient Patient { get; init; }
    public string? ChiefComplaint { get; init; }
    public string? Diagnosis { get; init; }
    public string? LabRequests { get; init; }
    public decimal? Temperature { get; init; }
    public List<PrescriptionItem>? Prescription { get; init; }
}

public class AlertService
{
    private readonly ClinicDbContext _db;
    private readonly PharmacyBrandMap _brands;

    /// <summary>International-name pairs that are unsafe together, with the severity the doctor must see.</summary>
    private static readonly (string A, string B, AlertType Type, string Message)[] Interactions =
    {
        ("Ibuprofen", "Warfarin", AlertType.Serious, "تداخل خطر نزيف: مضاد التهاب غير ستيرويدي مع مميع الدم"),
        ("Naproxen", "Warfarin", AlertType.Serious, "تداخل خطر نزيف: مضاد التهاب غير ستيرويدي مع مميع الدم"),
        ("Aspirin", "Warfarin", AlertType.Moderate, "تداخل مع مميع الدم — زيادة خطر النزيف، راجع الجرعة"),
        ("Enalapril", "Spironolactone", AlertType.Serious, "خطر ارتفاع البوتاسيوم: مثبط ACE مع مدر لفاقد البوتاسيوم"),
        ("Lisinopril", "Amiloride", AlertType.Moderate, "مراقبة مستويات البوتاسيوم: مثبط ACE مع مدر لفاقد البوتاسيوم"),
        ("Sertraline", "Ibuprofen", AlertType.Minor, "زيادة طفيفة في خطر النزيف مع مضادات الالتهاب"),
        ("Fluoxetine", "Acetylsalicylic Acid", AlertType.Minor, "مراقبة علامات النزيف مع الأسبرين"),
        ("Phenelzine", "Pethidine", AlertType.Serious, "تداخل خطير: ارتفاع حاد في الضغط — يُمنع الجمع بينهما"),
        ("Metoprolol", "Sildenafil", AlertType.Moderate, "خطر انخفاض حاد في الضغط مع حاصرات بيتا"),
    };

    /// <summary>Allergy keywords as they sit on the patient file (Arabic or English) and the drug keywords a registered allergy forbids.</summary>
    private static readonly (string[] AllergyKeywords, string[] DrugKeywords, string Label)[] AllergyChecks =
    {
        (new[] { "بنسلين", "penicillin", "اموكسيسيلين", "amoxicillin", "امبيسيلين", "ampicillin" },
            new[] { "penicillin", "amoxicillin", "ampicillin", "augmentin", "كلافيوكس", "اموكسي" }, "بنسلين"),
        (new[] { "أسبرين", "aspirin", "إيبوبروفين", "ibuprofen", "بروفين", "مضاد التهاب" },
            new[] { "aspirin", "ibuprofen", "naproxen", "أسبرين", "إيبوبروفين", "بروفين" }, "مضادات الالتهاب"),
        (new[] { "سلفا", "sulfamethoxazole", "sulfonamide", "كومبولين" },
            new[] { "sulfamethoxazole", "cotrimoxazole", "سلفا", "كومبولين" }, "سلفا"),
    };

    /// <summary>Diagnosis keywords and the lab each one should be accompanied by.</summary>
    private static readonly (string[] Keywords, string Lab, string Note)[] ExpectedLabs =
    {
        (new[] { "أنيميا", "فقر دم", "anemia" }, "CBC", "تحليل صورة دم CBC"),
        (new[] { "سكري", "diabetes" }, "HbA1c", "تحليل HbA1c للسكر التراكمي"),
        (new[] { "التهاب", "إلتهاب", "عدوى", "infection" }, "CBC", "تحاليل دم CBC مع CRP"),
        (new[] { "كلى", "كبدي", "kidney", "liver" }, "وظائف", "تحاليل وظائف (كلوية وكبدية)"),
    };

    public AlertService(ClinicDbContext db, PharmacyBrandMap brands)
    {
        _db = db;
        _brands = brands;
    }

    public async Task<List<Alert>> CheckAsync(VisitContext context, CancellationToken ct)
    {
        var alerts = new List<Alert>();
        var patient = context.Patient;
        var written = (context.Prescription ?? new())
            .Where(i => !string.IsNullOrWhiteSpace(i.DrugName))
            .Select(i => i.DrugName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 2.4: what the doctor typed may be an Egyptian brand name; resolve every one of them to its
        // international name so the allergy and interaction checks match on a single vocabulary.
        var names = new List<string>(written);
        foreach (var name in written)
            foreach (var match in await _brands.ResolveAsync(_db, name, ct))
                names.Add(match.Inn);

        if (written.Count > 0)
        {
            AddAllergyAlerts(patient.Allergies, names, alerts);
            var currentRx = await CurrentPrescriptionNamesAsync(patient, ct);
            AddInteractionAlerts(names, currentRx, alerts);
        }
        AddMissingLabAlerts(context, alerts);
        return alerts;
    }

    private static void AddAllergyAlerts(string? allergies, List<string> drugNames, List<Alert> alerts)
    {
        if (string.IsNullOrWhiteSpace(allergies)) return;
        var allergyText = NormalizeArabic(allergies);

        foreach (var (allergyKeywords, drugKeywords, label) in AllergyChecks)
        {
            if (!allergyKeywords.Any(k => allergyText.Contains(NormalizeArabic(k)))) continue;
            var hits = drugNames
                .Where(name => drugKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (hits.Count > 0)
                alerts.Add(new Alert(AlertType.Serious, $"حساسية مسجلة ضد {label} — الدواء الجديد يستثيرها", string.Join("، ", hits)));
        }
    }

    private static void AddInteractionAlerts(List<string> draftNames, List<string> currentRx, List<Alert> alerts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (a, b, type, message) in Interactions)
        {
            var inDraft = ContainsAny(draftNames, a) && ContainsAny(draftNames, b);
            var withCurrent = currentRx.Count > 0 &&
                ((ContainsAny(draftNames, a) && ContainsAny(currentRx, b)) ||
                 (ContainsAny(draftNames, b) && ContainsAny(currentRx, a)));
            if (inDraft && seen.Add($"{a}|{b}"))
                alerts.Add(new Alert(type, message, $"{a} + {b}"));
            else if (withCurrent && seen.Add($"cur|{a}|{b}"))
                alerts.Add(new Alert(type, "تداخل محتمل مع دواء في الروشتة الحالية للمريض — راجع قبل التوقيع", $"{a} + {b}"));
        }
    }

    private static void AddMissingLabAlerts(VisitContext ctx, List<Alert> alerts)
    {
        if (!string.IsNullOrWhiteSpace(ctx.LabRequests)) return;

        var text = NormalizeArabic($"{ctx.ChiefComplaint ?? ""} {ctx.Diagnosis ?? ""}");
        foreach (var (keywords, lab, note) in ExpectedLabs)
        {
            if (!keywords.Any(k => text.Contains(NormalizeArabic(k)))) continue;
            alerts.Add(new Alert(AlertType.Minor, $"التشخيص بلا تحاليل مرفقة — يُنصح بطلب {note}", lab));
            return;
        }

        if (ctx.Temperature is > 38m)
            alerts.Add(new Alert(AlertType.Minor, "حرارة مرتفعة بلا تحاليل مطلوبة — يُنصح بطلب CBC مبدئي", "CBC"));
    }

    /// <summary>The patient's current prescription (their latest visit): names and their international names.</summary>
    private async Task<List<string>> CurrentPrescriptionNamesAsync(Patient patient, CancellationToken ct)
    {
        var visitId = await _db.Visits.AsNoTracking()
            .Where(v => v.PatientId == patient.Id)
            .OrderByDescending(v => v.VisitDate)
            .Select(v => (int?)v.Id)
            .FirstOrDefaultAsync(ct);
        if (visitId == null) return new List<string>();

        var names = await _db.PrescriptionItems.AsNoTracking()
            .Where(i => i.VisitId == visitId && i.DrugName != "")
            .Select(i => i.DrugName)
            .Distinct()
            .ToListAsync(ct);

        var all = new List<string>(names);
        foreach (var name in names)
            foreach (var match in await _brands.ResolveAsync(_db, name, ct))
                all.Add(match.Inn);
        return all;
    }

    /// <summary>One Arabic/Latin lookup form: letter shapes folded ("أموكسيل" = "اموكسيل") and lower-cased, so the patient file and the written drug names share a vocabulary.</summary>
    private static string NormalizeArabic(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            var c = ch switch
            {
                'أ' or 'إ' or 'آ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                'ؤ' or 'ئ' => 'ي',
                'ک' => 'ك',
                _ => char.ToLowerInvariant(ch)
            };
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool ContainsAny(List<string> names, string keyword) =>
        names.Any(name => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}