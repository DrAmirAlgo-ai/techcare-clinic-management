using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Infrastructure;
using UglyToad.PdfPig;
using System.IO;
using TechCare.Clinic.Api.Domain;



namespace TechCare.Clinic.Api.Features.ClinicSettings;

public record UpdateClinicRequest(
    string Name,
    string Slug,
    string DoctorName,
    string Specialty,
    string? About,
    string? Phone,
    string? Address,
    decimal ConsultationFee,
    decimal FollowUpFee,
    int AvgMinutesPerPatient,
    string WorkStart,
    string WorkEnd,
    string WorkDays,
    bool OnlineBookingEnabled,
    int DataRetentionDays,
    // PLAN.md 10.7: the clinic's display identity; null/empty falls back to Name everywhere.
    string? DisplayName,
    // PLAN.md 10.12: prescription header fields.
    string? Qualifications,
    string? WorkingHoursText);

[ApiController]
[Route("api/clinic")]
[Authorize(Policy = Permissions.Dashboard)]
public partial class ClinicController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly TenantContext _tenant;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public ClinicController(ClinicDbContext db, TenantContext tenant, IConfiguration config, IWebHostEnvironment env)
    {
        _db = db;
        _tenant = tenant;
        _config = config;
        _env = env;
    }

    [HttpGet]
        public async Task<IActionResult> Get()
        {
            var clinic = await _db.Clinics.FirstAsync(c => c.Id == _tenant.ClinicId);
            return Ok(new
            {
                clinic.Id, clinic.Name, clinic.Slug, clinic.DoctorName, clinic.Specialty, clinic.About,
                clinic.Phone, clinic.Address, clinic.ConsultationFee, clinic.FollowUpFee, clinic.AvgMinutesPerPatient,
                WorkStart = clinic.WorkStart.ToString("HH:mm"), WorkEnd = clinic.WorkEnd.ToString("HH:mm"),
                clinic.WorkDays, clinic.OnlineBookingEnabled, clinic.DataRetentionDays,
                // PLAN.md 10.2: the subscription numbers, so the UI (and any consumer) can show them.
                plan = clinic.Plan.ToString(),
                subscriptionStatus = clinic.SubscriptionStatus.ToString(),
                trialEndsAtUtc = clinic.TrialEndsAtUtc,
                subscriptionEndsAtUtc = clinic.SubscriptionEndsAtUtc,
                daysRemaining = SubscriptionInfo.DaysRemaining(clinic),
                aiEnabled = clinic.AiEnabled,
                trialDays = clinic.TrialDays,
                // PLAN.md 10.7: the branding fields; hasLogo tells the UI whether /api/public/clinics/{slug}/logo serves one.
                displayName = clinic.DisplayName ?? clinic.Name,
                hasLogo = clinic.LogoPath != null,
                // PLAN.md 10.12: prescription header fields.
                clinic.Qualifications,
                clinic.WorkingHoursText
            });
        }

    [HttpPut]
    [Authorize(Policy = Permissions.Admin)]
    public async Task<IActionResult> Update(UpdateClinicRequest r)
    {
        var slug = r.Slug.Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(slug))
            return BadRequest(new { message = "رابط الموقع يجب أن يتكون من حروف إنجليزية صغيرة وأرقام وشرطة (-) فقط" });
        if (!TimeOnly.TryParse(r.WorkStart, out var start) || !TimeOnly.TryParse(r.WorkEnd, out var end) || end <= start)
            return BadRequest(new { message = "مواعيد العمل غير صحيحة" });
        if (r.AvgMinutesPerPatient is < 3 or > 120)
            return BadRequest(new { message = "مدة الكشف يجب أن تكون بين 3 و120 دقيقة" });
        // PLAN_ADDENDUM.md 9.3: the retention window of a logically deleted patient file — at least a day,
        // at most ten years, so a typo cannot make the files vanish tonight or live forever.
        if (r.DataRetentionDays is < 1 or > 3650)
            return BadRequest(new { message = "مدة الاحتفاظ بالسجلات المحذوفة يجب أن تكون بين يوم واحد و10 سنوات" });
        // PLAN.md 10.7: the name that appears on the app, the public page and printouts.
        if ((r.DisplayName?.Trim().Length ?? 0) > 80)
            return BadRequest(new { message = "اسم العيادة المعروض طويل جداً (الحد 80 حرفاً)" });

        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _tenant.ClinicId);
        if (slug != clinic.Slug && await _db.Clinics.AnyAsync(c => c.Slug == slug))
            return Conflict(new { message = "هذا الرابط مستخدم لعيادة أخرى" });

        clinic.Name = r.Name.Trim();
        clinic.Slug = slug;
        clinic.DoctorName = r.DoctorName.Trim();
        clinic.Specialty = r.Specialty.Trim();
        clinic.About = r.About;
        clinic.Phone = r.Phone;
        clinic.Address = r.Address;
        clinic.ConsultationFee = r.ConsultationFee;
        clinic.FollowUpFee = r.FollowUpFee;
        clinic.AvgMinutesPerPatient = r.AvgMinutesPerPatient;
        clinic.WorkStart = start;
        clinic.WorkEnd = end;
        clinic.WorkDays = r.WorkDays;
        clinic.OnlineBookingEnabled = r.OnlineBookingEnabled;
        clinic.DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? null : r.DisplayName.Trim();
                // PLAN.md 10.12: prescription header fields.
                clinic.Qualifications = string.IsNullOrWhiteSpace(r.Qualifications) ? null : r.Qualifications.Trim();
                clinic.WorkingHoursText = string.IsNullOrWhiteSpace(r.WorkingHoursText) ? null : r.WorkingHoursText.Trim();
                await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── PLAN.md 10.7 — the clinic logo. Same rules as visit attachments (PLAN_ADDENDUM.md 7.8): real PNG/JPEG
    //    signature check, a size cap, and a random stored name inside the clinic's own folder outside wwwroot.
    //    The file is served to the world (login and public pages) via GET api/public/clinics/{slug}/logo.
    private const long MaxLogoBytes = 2 * 1024 * 1024;

    [HttpPost("logo")]
    [Authorize(Policy = Permissions.Admin)]
    [RequestSizeLimit(MaxLogoBytes)]
    public async Task<IActionResult> UploadLogo(IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { message = "يرجى اختيار صورة" });

        var ext = Path.GetExtension(file.FileName ?? string.Empty).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg"))
            return BadRequest(new { message = "الصور المدعومة: PNG و JPG" });
        if (file.Length > MaxLogoBytes)
            return BadRequest(new { message = "حجم اللوجو يتجاوز الحد المسموح (2 ميجابايت)" });

        await using (var check = file.OpenReadStream())
        {
            var reason = await Features.Ai.ContentSignature.ValidateAsync(ext, check);
            if (reason is not null) return BadRequest(new { message = reason });
        }

        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _tenant.ClinicId);
        var folder = Path.GetFullPath(Path.Combine(AttachmentRoot(), _tenant.ClinicId.ToString()));
        Directory.CreateDirectory(folder);

        var storedName = $"{Guid.NewGuid():N}{ext}";
        var target = Path.GetFullPath(Path.Combine(folder, storedName));
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "اسم ملف غير صالح" });

        var oldName = clinic.LogoPath;
        await using (var stream = System.IO.File.Create(target))
            await file.CopyToAsync(stream);

        clinic.LogoPath = storedName;
        _db.LogAction("Update", "ClinicLogo", clinic.Id);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch
        {
            try { System.IO.File.Delete(target); }
            catch { /* the random name keeps any orphan unreachable */ }
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "تعذّر حفظ اللوجو" });
        }

        // The previous logo file holds no value once replaced.
        if (oldName != null)
            try { System.IO.File.Delete(Path.Combine(folder, oldName)); }
            catch { /* an orphan with a random name is unreachable */ }

        return Ok(new { hasLogo = true, url = $"/api/public/clinics/{clinic.Slug}/logo" });
    }
    [HttpPost("catalog/upload-preview")]
    [Authorize(Policy = Permissions.Admin)]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB
    public async Task<IActionResult> UploadCatalogPreview(IFormFile file, [FromQuery] string type)
    {
        if (string.IsNullOrWhiteSpace(type) || !new[] { "Lab", "Radiology" }.Contains(type))
            return BadRequest(new { message = "نوع غير صالح" });

        if (file == null || file.Length == 0) return BadRequest(new { message = "يرجى اختيار ملف PDF" });

        var ext = Path.GetExtension(file.FileName ?? string.Empty).ToLowerInvariant();
        if (ext != ".pdf")
            return BadRequest(new { message = "الملف يجب أن يكون بصيغة PDF" });

        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _tenant.ClinicId);
        var folder = Path.GetFullPath(Path.Combine(AttachmentRoot(), _tenant.ClinicId.ToString()));
        Directory.CreateDirectory(folder);

        var storedName = $"{Guid.NewGuid():N}.pdf";
        var target = Path.Combine(folder, storedName);

        await using (var stream = System.IO.File.Create(target))
        {
            await file.CopyToAsync(stream);
        }

        var lines = new List<string>();
        try
        {
            using (var document = PdfDocument.Open(target))
            {
                foreach (var page in document.GetPages())
                {
                    var text = page.Text;
                    var pageLines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in pageLines)
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            lines.Add(trimmed);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            try { System.IO.File.Delete(target); } catch { }
            return BadRequest(new { message = "فشل قراءة ملف PDF" });
        }

        var cleaned = CleanCatalogLines(lines);

        return Ok(new { lines = cleaned });
    }

    private List<string> CleanCatalogLines(List<string> lines)
    {
        var cleaned = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            if (line.Length < 2) continue;

            var stripped = System.Text.RegularExpressions.Regex.Replace(line, @"[\d\+\-\.\,\:\;\$\£\€\%\s]", "");
            if (stripped.Length == 0) continue;

            var lower = line.ToLowerInvariant();
            if (seen.Contains(lower)) continue;
            seen.Add(lower);

            cleaned.Add(line);
        }

        return cleaned;
    }

    [HttpPost("catalog/save")]
    [Authorize(Policy = Permissions.Admin)]
    public async Task<IActionResult> SaveCatalog([FromBody] SaveCatalogRequest request, [FromQuery] string type)
    {
        if (string.IsNullOrWhiteSpace(type) || !new[] { "Lab", "Radiology" }.Contains(type))
            return BadRequest(new { message = "نوع غير صالح" });

        if (request == null || request.Names == null) return BadRequest(new { message = "قائمة الأسماء مطلوبة" });

        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _tenant.ClinicId);

        CatalogItemType itemType = type == "Lab" ? CatalogItemType.Lab : CatalogItemType.Radiology;

        if (request.Replace)
        {
            var existing = _db.CatalogItems.Where(ci => ci.ClinicId == clinic.Id && ci.Type == itemType);
            _db.CatalogItems.RemoveRange(existing);
        }

        var items = request.Names.Select(name => new CatalogItem
        {
            Name = name.Trim(),
            Type = itemType,
            ClinicId = clinic.Id
        }).Where(i => !string.IsNullOrWhiteSpace(i.Name));

        _db.CatalogItems.AddRange(items);

        await _db.SaveChangesAsync();

        return Ok(new { count = items.Count() });
    }

    public class SaveCatalogRequest
    {
        public List<string> Names { get; set; } = new();
        public bool Replace { get; set; }
    }


    /// <summary>The installation folder for per-clinic files (visit attachments, logos); outside wwwroot.</summary>
    private string AttachmentRoot() =>
        Path.GetFullPath(Path.Combine(_env.ContentRootPath, _config["Files:AttachmentsPath"] ?? "PatientFiles"));

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
