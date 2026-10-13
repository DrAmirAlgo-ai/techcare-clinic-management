using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Queue;

public record CreateAppointmentRequest(int? PatientId, string? PatientName, string? Phone, DateTime ScheduledAt, AppointmentType Type, string? Notes);
public record RescheduleRequest(DateTime ScheduledAt, AppointmentType Type, string? Notes);
public record LinkPatientRequest(int PatientId);

[ApiController]
[Route("api/appointments")]
[Authorize(Policy = Permissions.Appointments)]
[TypeFilter(typeof(QueueExceptionFilter))]
public class AppointmentsController : ControllerBase
{
    private const string SlotTakenMessage = "هذا الموعد غير متاح، يرجى اختيار موعد آخر";

    private readonly ClinicDbContext _db;
    private readonly QueueService _queue;
    private readonly ScheduleService _schedule;
    private readonly QueueNotifier _notifier;

    public AppointmentsController(ClinicDbContext db, QueueService queue, ScheduleService schedule, QueueNotifier notifier)
    {
        _db = db;
        _queue = queue;
        _schedule = schedule;
        _notifier = notifier;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] DateOnly? date)
    {
        var clinic = await _queue.CurrentClinicAsync();
        var today = ClinicClock.Today(clinic);
        var selected = date ?? today;
        var day = selected.ToDateTime(TimeOnly.MinValue);

        var list = await _db.Appointments
            .Where(a => a.ScheduledAt >= day && a.ScheduledAt < day.AddDays(1))
            .OrderBy(a => a.ScheduledAt)
            .Select(a => new
            {
                a.Id, a.PatientId, a.PatientName, a.Phone, a.ScheduledAt, a.Type, a.Status, a.Source,
                a.IsUrgent, a.QueueNumber, a.Notes, a.TrackingToken,
                PatientCode = a.Patient != null ? a.Patient.Code : null
            })
            .ToListAsync();

        var estimates = new Dictionary<int, DateTime?>();
        if (selected == today)
        {
            var snapshot = await _queue.GetSnapshotAsync(clinic);
            foreach (var e in snapshot.Waiting.Concat(snapshot.BookedNotArrived)) estimates[e.AppointmentId] = e.EstimatedAt;
        }

        return Ok(list.Select(a => new
        {
            a.Id, a.PatientId, a.PatientName, a.Phone, a.ScheduledAt, a.Type, a.Status, a.Source,
            a.IsUrgent, a.QueueNumber, a.Notes, a.TrackingToken, a.PatientCode,
            EstimatedAt = estimates.TryGetValue(a.Id, out var at) ? at
                : a.Status == AppointmentStatus.Booked && selected > today ? a.ScheduledAt : (DateTime?)null
        }));
    }

    [HttpGet("slots")]
    public async Task<IActionResult> Slots([FromQuery] DateOnly date, [FromQuery] int? ignore)
    {
        var clinic = await _queue.CurrentClinicAsync();
        return Ok(new { date, slots = await _schedule.AvailableSlotsAsync(clinic, date, ignore) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateAppointmentRequest r)
    {
        if (!await _schedule.IsAvailableAsync(await _queue.CurrentClinicAsync(), r.ScheduledAt))
            return Conflict(new { message = SlotTakenMessage });

        var appt = new Appointment
        {
            ScheduledAt = r.ScheduledAt,
            Type = r.Type,
            Notes = r.Notes,
            Source = AppointmentSource.Reception,
            TrackingToken = QueueService.NewTrackingToken()
        };

        if (r.PatientId is int patientId)
        {
            var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId);
            if (patient == null) return NotFound(new { message = "المريض غير موجود" });
            appt.PatientId = patient.Id;
            appt.PatientName = patient.FullName;
            appt.Phone = patient.Phone;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(r.PatientName))
                return BadRequest(new { message = "يرجى اختيار مريض أو إدخال الاسم" });
            appt.PatientName = r.PatientName.Trim();
            appt.Phone = r.Phone?.Trim();
        }

        _db.Appointments.Add(appt);
        await _db.SaveChangesAsync();
        await _notifier.QueueChangedAsync();
        return Ok(new { appt.Id, appt.TrackingToken });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Reschedule(int id, RescheduleRequest r)
    {
        var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == id);
        if (appt == null) return NotFound();
        if (appt.Status != AppointmentStatus.Booked)
            return BadRequest(new { message = "لا يمكن تعديل الموعد بعد تسجيل الحضور" });
        if (r.ScheduledAt != appt.ScheduledAt && !await _schedule.IsAvailableAsync(await _queue.CurrentClinicAsync(), r.ScheduledAt, appt.Id))
            return Conflict(new { message = SlotTakenMessage });

        appt.ScheduledAt = r.ScheduledAt;
        appt.Type = r.Type;
        appt.Notes = r.Notes;
        await _db.SaveChangesAsync();
        await _notifier.QueueChangedAsync();
        return NoContent();
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        await _queue.CancelAsync(id);
        await _notifier.QueueChangedAsync();
        return NoContent();
    }

    /// <summary>Online bookings arrive with a name and phone only; reception links them to a patient file on arrival.</summary>
    [HttpPost("{id:int}/link-patient")]
    public async Task<IActionResult> LinkPatient(int id, LinkPatientRequest r)
    {
        var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == id);
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == r.PatientId);
        if (appt == null || patient == null) return NotFound();

        appt.PatientId = patient.Id;
        appt.PatientName = patient.FullName;
        appt.Phone ??= patient.Phone;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
