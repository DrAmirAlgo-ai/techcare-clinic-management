using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Ai;
using TechCare.Clinic.Api.Features.Alerts;
using TechCare.Clinic.Api.Features.Queue;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Visits;

public record PrescriptionItemDto(string DrugName, string? Dose, string? Frequency, string? Duration, string? Instructions);

public record VisitRequest(
    int PatientId,
    int? AppointmentId,
    string? ChiefComplaint,
    string? Examination,
    string? Diagnosis,
    string? Notes,
    string? LabRequests,
    DateOnly? FollowUpDate,
    string? BloodPressure,
    int? Pulse,
    decimal? Temperature,
    decimal? WeightKg,
    decimal? HeightCm,
    int? BloodSugar,
    List<PrescriptionItemDto>? Prescription);

public record DraftAlertsRequest(
    int PatientId,
    string? ChiefComplaint,
    string? Diagnosis,
    string? LabRequests,
    decimal? Temperature,
    List<PrescriptionItemDto>? Prescription);

[ApiController]
[Route("api/visits")]
[Authorize(Policy = Permissions.Assistant)]
public class VisitsController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly TenantContext _tenant;
    private readonly QueueService _queue;
    private readonly QueueNotifier _notifier;
    private readonly AlertService _alerts;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public VisitsController(ClinicDbContext db, TenantContext tenant, QueueService queue, QueueNotifier notifier,
        AlertService alerts, IConfiguration config, IWebHostEnvironment env)
    {
        _db = db;
        _tenant = tenant;
        _queue = queue;
        _notifier = notifier;
        _alerts = alerts;
        _config = config;
        _env = env;
    }

    [HttpGet("by-patient/{patientId:int}")]
    public async Task<IActionResult> ByPatient(int patientId)
    {
        var visits = await _db.Visits
            .Where(v => v.PatientId == patientId)
            .OrderByDescending(v => v.VisitDate)
            .Select(v => new
            {
                v.Id, v.VisitDate, v.ChiefComplaint, v.Examination, v.Diagnosis, v.Notes, v.LabRequests, v.FollowUpDate,
                v.BloodPressure, v.Pulse, v.Temperature, v.WeightKg, v.HeightCm, v.BloodSugar,
                DoctorName = v.Doctor != null ? v.Doctor.FullName : null,
                Prescription = v.Prescription.Select(i => new { i.DrugName, i.Dose, i.Frequency, i.Duration, i.Instructions })
            })
            .ToListAsync();
        return Ok(visits);
    }

    [HttpGet("{id:int}/print")]
    public async Task<IActionResult> PrintData(int id)
    {
        var visit = await _db.Visits
            .Include(v => v.Patient)
            .Include(v => v.Doctor)
            .Include(v => v.Prescription)
            .FirstOrDefaultAsync(v => v.Id == id);
        if (visit == null) return NotFound();

        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _tenant.ClinicId);
                return Ok(new
                {
                    clinic = new { clinic.Name, clinic.DoctorName, clinic.Specialty, clinic.Phone, clinic.Address,
                        // PLAN.md 10.7: the printout shows the clinic's own name and logo.
                        displayName = clinic.DisplayName ?? clinic.Name, hasLogo = clinic.LogoPath != null, clinic.Slug,
                        // PLAN.md 10.12: prescription header fields.
                        clinic.Qualifications, clinic.WorkingHoursText },
                    patient = new { visit.Patient!.FullName, visit.Patient.Code, visit.Patient.BirthDate, visit.Patient.Gender },
                    visit = new { visit.Id, visit.VisitDate, visit.Diagnosis, visit.LabRequests, visit.FollowUpDate, visit.Notes },
                    prescription = visit.Prescription.Select(i => new { i.DrugName, i.Dose, i.Frequency, i.Duration, i.Instructions })
                });
    }

    [HttpPost]
    public async Task<IActionResult> Create(VisitRequest r)
    {
        if (!await _db.Patients.AnyAsync(p => p.Id == r.PatientId))
            return NotFound(new { message = "المريض غير موجود" });

        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _tenant.ClinicId);
        var now = ClinicClock.Now(clinic);
        var appointmentId = r.AppointmentId ?? await TodaysOpenAppointmentAsync(r.PatientId, now);

        var visit = new Visit
        {
            PatientId = r.PatientId,
            DoctorId = _tenant.RequireUserId(),
            AppointmentId = appointmentId,
            VisitDate = now
        };
        Apply(visit, r);
        _db.Visits.Add(visit);

        await using var tx = await _db.Database.BeginTransactionAsync();
        await _db.SaveChangesAsync();

        if (appointmentId is int apptId)
        {
            await _queue.FinishAsync(apptId);
            var invoices = await _db.Invoices.Where(i => i.AppointmentId == apptId && i.VisitId == null).ToListAsync();
            foreach (var invoice in invoices) invoice.VisitId = visit.Id;
            await _db.SaveChangesAsync();
        }

        await tx.CommitAsync();
        if (appointmentId != null) await _notifier.QueueChangedAsync();

        return Ok(new { visit.Id });
    }

    /// <summary>The patient's queue entry today (inside first, then waiting), so a visit recorded from the file still closes it.</summary>
    private async Task<int?> TodaysOpenAppointmentAsync(int patientId, DateTime now)
    {
        var start = now.Date;
        var end = start.AddDays(1);
        return await _db.Appointments
            .Where(a => a.PatientId == patientId && a.ScheduledAt >= start && a.ScheduledAt < end
                        && (a.Status == AppointmentStatus.InConsultation || a.Status == AppointmentStatus.Waiting))
            .OrderBy(a => a.Status == AppointmentStatus.InConsultation ? 0 : 1)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync();
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, VisitRequest r)
    {
        var visit = await _db.Visits.Include(v => v.Prescription).FirstOrDefaultAsync(v => v.Id == id);
        if (visit == null) return NotFound();

        _db.PrescriptionItems.RemoveRange(visit.Prescription);
        visit.Prescription.Clear();
        Apply(visit, r);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── PLAN_FULL.md 5.1 — the lab scans and X-rays a clinic keeps with a saved visit ───────────────
    // The image lives on disk in the clinic's own folder, outside wwwroot, under the server's own random name;
    // the VisitAttachment row is the only door to it, so no caller-supplied path can reach a file
    // (PLAN_ADDENDUM.md 7.8), and the clinic filter keeps another clinic's attachments out of reach.
    private const long MaxAttachmentBytes = 10 * 1024 * 1024;

    [HttpPost("{id:int}/attachments")]
    [RequestSizeLimit(MaxAttachmentBytes)]
    public async Task<IActionResult> AddAttachment(int id, IFormFile file)
    {
        var visit = await _db.Visits.FirstOrDefaultAsync(v => v.Id == id);
        if (visit == null) return NotFound(new { message = "الزيارة غير موجودة" });
        if (file == null || file.Length == 0) return BadRequest(new { message = "يرجى اختيار ملف" });

        // PLAN.md 10.11: PDF lab reports sit beside the scans — same signature check, size limit, and isolation.
        var ext = Path.GetExtension(file.FileName ?? string.Empty).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".pdf"))
            return BadRequest(new { message = "المرفقات المدعومة: PDF و PNG و JPG" });
        if (file.Length > MaxAttachmentBytes)
            return BadRequest(new { message = "حجم الملف يتجاوز الحد المسموح (10 ميجابايت)" });

        // PLAN_ADDENDUM.md 7.8: the content is validated, not the extension — a .png that is not a PNG
        // must not reach a patient's file.
        await using (var check = file.OpenReadStream())
        {
            var reason = await ContentSignature.ValidateAsync(ext, check);
            if (reason is not null) return BadRequest(new { message = reason });
        }

        // 7.8: the file is stored under a random name, never the caller's — "..\..\evil" cannot leave the
        // clinic folder, and the extension is kept so the door that serves it knows the content type.
        var storedName = $"{Guid.NewGuid():N}{ext}";
        var attachment = new VisitAttachment
        {
            VisitId = visit.Id,
            StoredName = storedName,
            OriginalName = Path.GetFileName(file.FileName ?? storedName),
            ContentType = ext switch { ".png" => "image/png", ".pdf" => "application/pdf", _ => "image/jpeg" },
            SizeBytes = file.Length,
            UploadedAtUtc = DateTime.UtcNow
        };

        var folder = AttachmentFolder();
        var target = Path.GetFullPath(Path.Combine(folder, storedName));
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "اسم ملف غير صالح" });
        if (System.IO.File.Exists(target))
            return Conflict(new { message = "يوجد مرفق بنفس الاسم" });

        await using (var stream = System.IO.File.Create(target))
            await file.CopyToAsync(stream);

        _db.VisitAttachments.Add(attachment);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch
        {
            // The row never landed: take the file back so the disk never outruns the database.
            try { System.IO.File.Delete(target); }
            catch { /* the random name keeps any orphan unreachable */ }
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "تعذّر حفظ المرفق" });
        }

        return Ok(new { attachment.Id, attachment.OriginalName, attachment.ContentType, attachment.SizeBytes, attachment.UploadedAtUtc });
    }

    [HttpGet("{visitId:int}/attachments")]
    public async Task<IActionResult> Attachments(int visitId)
    {
        var visit = await _db.Visits.FirstOrDefaultAsync(v => v.Id == visitId);
        if (visit == null) return NotFound(new { message = "الزيارة غير موجودة" });

        var list = await _db.VisitAttachments.AsNoTracking()
            .Where(a => a.VisitId == visitId)
            .OrderBy(a => a.UploadedAtUtc)
            .Select(a => new { a.Id, a.OriginalName, a.ContentType, a.SizeBytes, a.UploadedAtUtc })
            .ToListAsync();
        return Ok(list);
    }

    [HttpGet("attachments/{id:int}")]
    public async Task<IActionResult> OpenAttachment(int id)
    {
        var attachment = await _db.VisitAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
        if (attachment == null) return NotFound(new { message = "المرفق غير موجود" });

        // The file is resolved through the row's own random name inside the caller's clinic folder — never
        // through a path the caller supplies.
        var folder = Path.GetFullPath(Path.Combine(AttachmentRoot(), _tenant.ClinicId.ToString()));
        var path = Path.GetFullPath(Path.Combine(folder, attachment.StoredName));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return NotFound();
        if (!System.IO.File.Exists(path)) return NotFound(new { message = "الملف غير موجود على الخادم" });

        return File(await System.IO.File.ReadAllBytesAsync(path), attachment.ContentType);
    }

    /// <summary>The installation folder for visit attachments; each clinic keeps its own subfolder.</summary>
    private string AttachmentRoot() =>
        Path.GetFullPath(Path.Combine(_env.ContentRootPath, _config["Files:AttachmentsPath"] ?? "PatientFiles"));

    /// <summary>The caller's own clinic folder, created on demand.</summary>
    private string AttachmentFolder()
    {
        var folder = Path.GetFullPath(Path.Combine(AttachmentRoot(), _tenant.ClinicId.ToString()));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>PLAN_FULL.md 3.1 — light alert check on the visit draft: allergy met in the new prescription, drug interaction, lab the diagnosis should carry.</summary>
    [HttpPost("drafts/alerts")]
    public async Task<IActionResult> DraftAlerts(DraftAlertsRequest r)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == r.PatientId);
        if (patient == null) return NotFound(new { message = "المريض غير موجود" });

        var alerts = await _alerts.CheckAsync(new VisitContext
        {
            Patient = patient,
            ChiefComplaint = r.ChiefComplaint,
            Diagnosis = r.Diagnosis,
            LabRequests = r.LabRequests,
            Temperature = r.Temperature,
            Prescription = (r.Prescription ?? new())
                .Where(i => !string.IsNullOrWhiteSpace(i.DrugName))
                .Select(i => new PrescriptionItem
                {
                    DrugName = i.DrugName.Trim(), Dose = i.Dose, Frequency = i.Frequency,
                    Duration = i.Duration, Instructions = i.Instructions
                })
                .ToList()
        }, HttpContext.RequestAborted);
        return Ok(new { alerts });
    }

    private static void Apply(Visit v, VisitRequest r)
    {
        v.ChiefComplaint = r.ChiefComplaint;
        v.Examination = r.Examination;
        v.Diagnosis = r.Diagnosis;
        v.Notes = r.Notes;
        v.LabRequests = r.LabRequests;
        v.FollowUpDate = r.FollowUpDate;
        v.BloodPressure = r.BloodPressure;
        v.Pulse = r.Pulse;
        v.Temperature = r.Temperature;
        v.WeightKg = r.WeightKg;
        v.HeightCm = r.HeightCm;
        v.BloodSugar = r.BloodSugar;
        v.Prescription.AddRange((r.Prescription ?? new())
            .Where(i => !string.IsNullOrWhiteSpace(i.DrugName))
            .Select(i => new PrescriptionItem
            {
                DrugName = i.DrugName.Trim(), Dose = i.Dose, Frequency = i.Frequency,
                Duration = i.Duration, Instructions = i.Instructions
            }));
    }
}
