using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Queue;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Public;

public record PublicBookingRequest(string Name, string Phone, DateOnly Date, string Time, AppointmentType Type);

/// <summary>
/// Anonymous endpoints behind the doctor's website, the waiting-room screen and the patient tracking page.
/// Never returns another patient's full name or phone.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
[EnableRateLimiting("public")]
public partial class PublicController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly TenantContext _tenant;
    private readonly QueueService _queue;
    private readonly ScheduleService _schedule;
    private readonly QueueNotifier _notifier;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public PublicController(ClinicDbContext db, TenantContext tenant, QueueService queue, ScheduleService schedule,
        QueueNotifier notifier, IConfiguration config, IWebHostEnvironment env)
    {
        _db = db;
        _tenant = tenant;
        _queue = queue;
        _schedule = schedule;
        _notifier = notifier;
        _config = config;
        _env = env;
    }

    [HttpGet("clinics/{slug}")]
    public async Task<IActionResult> Profile(string slug)
    {
        var clinic = await FindClinicAsync(slug);
        if (clinic == null) return NotFound();
        return Ok(new
        {
            clinic.Name, clinic.Slug, clinic.DoctorName, clinic.Specialty, clinic.About, clinic.Phone, clinic.Address,
            clinic.ConsultationFee, clinic.FollowUpFee, clinic.OnlineBookingEnabled,
            // PLAN.md 10.7: the clinic's own display name; hasLogo tells the page whether the logo URL serves one.
            displayName = clinic.DisplayName ?? clinic.Name,
            hasLogo = clinic.LogoPath != null,
            WorkStart = clinic.WorkStart.ToString("HH:mm"), WorkEnd = clinic.WorkEnd.ToString("HH:mm"),
            WorkDays = clinic.WorkDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        });
    }

    [HttpGet("clinics/{slug}/slots")]
    public async Task<IActionResult> Slots(string slug, [FromQuery] DateOnly date)
    {
        var clinic = await FindClinicAsync(slug);
        if (clinic == null) return NotFound();
        return Ok(new { date, slots = await _schedule.AvailableSlotsAsync(clinic, date) });
    }

    [HttpPost("clinics/{slug}/bookings")]
    [EnableRateLimiting("booking")]
    public async Task<IActionResult> Book(string slug, PublicBookingRequest r)
    {
        var clinic = await FindClinicAsync(slug);
        if (clinic == null) return NotFound();
        if (!clinic.OnlineBookingEnabled)
            return BadRequest(new { message = "الحجز الإلكتروني غير متاح حالياً" });

        var name = r.Name?.Trim() ?? "";
        var phone = Regex.Replace(r.Phone ?? "", @"[\s\-]", "");
        if (name.Length < 3 || name.Length > 80)
            return BadRequest(new { message = "يرجى إدخال الاسم الكامل" });
        if (!PhonePattern().IsMatch(phone))
            return BadRequest(new { message = "رقم الهاتف غير صحيح" });
        if (!TimeOnly.TryParse(r.Time, out var time))
            return BadRequest(new { message = "الموعد غير صحيح" });

        var at = r.Date.ToDateTime(new TimeOnly(time.Hour, time.Minute));
        if (!await _schedule.IsAvailableAsync(clinic, at))
            return Conflict(new { message = "هذا الموعد غير متاح، يرجى اختيار موعد آخر" });

        var dayStart = r.Date.ToDateTime(TimeOnly.MinValue);
        var alreadyBooked = await _db.Appointments.AnyAsync(a =>
            a.Phone == phone && a.ScheduledAt >= dayStart && a.ScheduledAt < dayStart.AddDays(1)
            && a.Status != AppointmentStatus.Cancelled);
        if (alreadyBooked)
            return Conflict(new { message = "يوجد حجز مسجل بهذا الرقم في نفس اليوم" });

        var existingPatient = await _db.Patients.FirstOrDefaultAsync(p => p.Phone == phone);
        var appt = new Appointment
        {
            PatientId = existingPatient?.Id,
            PatientName = name,
            Phone = phone,
            ScheduledAt = at,
            Type = r.Type,
            Source = AppointmentSource.Online,
            TrackingToken = QueueService.NewTrackingToken()
        };
        _db.Appointments.Add(appt);
        await _db.SaveChangesAsync();
        await _notifier.QueueChangedAsync(clinic.Id);

        var snapshot = await _queue.GetSnapshotAsync(clinic);
        var estimatedAt = snapshot.BookedNotArrived.FirstOrDefault(b => b.AppointmentId == appt.Id)?.EstimatedAt ?? appt.ScheduledAt;
        return Ok(new { appt.TrackingToken, appt.ScheduledAt, estimatedAt });
    }

    [HttpGet("track/{token}")]
    public async Task<IActionResult> Track(string token)
    {
        var appt = await FindByTokenAsync(token);
        if (appt == null) return NotFound();

        var clinic = await _db.Clinics.FirstAsync(c => c.Id == appt.ClinicId);
        var snapshot = await _queue.GetSnapshotAsync(clinic);
        var mine = snapshot.Waiting.FirstOrDefault(w => w.AppointmentId == appt.Id);
        var estimatedAt = appt.Status switch
        {
            AppointmentStatus.Waiting => mine?.EstimatedAt,
            AppointmentStatus.Booked => snapshot.BookedNotArrived.FirstOrDefault(b => b.AppointmentId == appt.Id)?.EstimatedAt ?? appt.ScheduledAt,
            _ => null
        };

        return Ok(new
        {
            clinic = new { clinic.Name, clinic.Slug, clinic.DoctorName, clinic.Address, clinic.Phone },
            patientName = FirstName(appt.PatientName),
            appt.ScheduledAt,
            appt.Status,
            appt.Type,
            appt.QueueNumber,
            currentNumber = snapshot.Current?.QueueNumber,
            peopleAhead = mine != null ? mine.Position - 1 : (int?)null,
            estimatedWaitMinutes = mine?.EstimatedWaitMinutes,
            estimatedAt,
            avgMinutesPerPatient = snapshot.AvgMinutesPerPatient,
            canCancel = appt.Status == AppointmentStatus.Booked
        });
    }

    [HttpPost("track/{token}/cancel")]
    [EnableRateLimiting("booking")]
    public async Task<IActionResult> CancelByPatient(string token)
    {
        var appt = await FindByTokenAsync(token);
        if (appt == null) return NotFound();
        if (appt.Status != AppointmentStatus.Booked)
            return BadRequest(new { message = "لا يمكن إلغاء الحجز في حالته الحالية" });

        appt.Status = AppointmentStatus.Cancelled;
        await _db.SaveChangesAsync();
        await _notifier.QueueChangedAsync(appt.ClinicId);
        return NoContent();
    }

    [HttpGet("clinics/{slug}/queue")]
    public async Task<IActionResult> Display(string slug)
    {
        var clinic = await FindClinicAsync(slug);
        if (clinic == null) return NotFound();

        var s = await _queue.GetSnapshotAsync(clinic);
        return Ok(new
        {
            clinic = new { clinic.Name, clinic.DoctorName, clinic.Specialty },
            current = s.Current == null ? null : new { s.Current.QueueNumber, Name = FirstName(s.Current.PatientName) },
            next = s.Waiting.Take(6).Select(w => new { w.QueueNumber, Name = FirstName(w.PatientName), w.EstimatedWaitMinutes, w.IsUrgent }),
            waitingCount = s.WaitingCount,
            avgMinutesPerPatient = s.AvgMinutesPerPatient
        });
    }

    // PLAN.md 10.7: the clinic's logo, public by definition — it decorates the clinic's public page, the login
    // screen and printouts. The file lives outside wwwroot under a random name; the row name is what opens it.
    [HttpGet("clinics/{slug}/logo")]
    public async Task<IActionResult> Logo(string slug)
    {
        var clinic = await FindClinicAsync(slug);
        if (clinic?.LogoPath == null) return NotFound();

        var folder = Path.GetFullPath(Path.Combine(_env.ContentRootPath,
            _config["Files:AttachmentsPath"] ?? "PatientFiles", clinic.Id.ToString()));
        var path = Path.GetFullPath(Path.Combine(folder, clinic.LogoPath));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !System.IO.File.Exists(path))
            return NotFound();

        Response.Headers.CacheControl = "public, max-age=3600";
        var contentType = clinic.LogoPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        return File(await System.IO.File.ReadAllBytesAsync(path), contentType);
    }

    private async Task<Domain.Clinic?> FindClinicAsync(string slug)
    {
        var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.Slug == slug.ToLower());
        if (clinic != null) _tenant.SetClinic(clinic.Id);
        return clinic;
    }

    private async Task<Appointment?> FindByTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        // Justified IgnoreQueryFilters: the tracking token is all an anonymous patient has, and the clinic is
        // unknown until the row is found — the token itself decides which clinic the request belongs to. The clinic
        // is set on the tenant context right after, so every other query in this request stays scoped.
        var appt = await _db.Appointments.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.TrackingToken == token);
        if (appt != null) _tenant.SetClinic(appt.ClinicId);
        return appt;
    }

    private static string FirstName(string fullName) =>
        fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    [GeneratedRegex(@"^(\+?\d{8,15})$")]
    private static partial Regex PhonePattern();
}
