using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Queue;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Invoices;

public record CreateInvoiceRequest(int PatientId, int? VisitId, string Description, decimal Amount, decimal Discount, decimal Paid, PaymentMethod Method, int? AppointmentId = null);
public record AddPaymentRequest(decimal Amount);

[ApiController]
[Route("api/invoices")]
[Authorize(Policy = Permissions.Invoices)]
public class InvoicesController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly TenantContext _tenant;
    private readonly QueueNotifier _notifier;

    public InvoicesController(ClinicDbContext db, TenantContext tenant, QueueNotifier notifier)
    {
        _db = db;
        _tenant = tenant;
        _notifier = notifier;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? patientId)
    {
        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _tenant.ClinicId);
        var today = ClinicClock.Today(clinic);
        var start = (from ?? today).ToDateTime(TimeOnly.MinValue);
        var end = (to ?? today).AddDays(1).ToDateTime(TimeOnly.MinValue);

        var query = _db.Invoices.Where(i => i.CreatedAt >= start && i.CreatedAt < end);
        if (patientId != null) query = _db.Invoices.Where(i => i.PatientId == patientId);

        var rows = await query
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new
            {
                i.Id, i.CreatedAt, i.Description, i.Amount, i.Discount, i.Paid, i.Method, i.PatientId, i.VisitId,
                PatientName = i.Patient!.FullName, PatientCode = i.Patient.Code
            })
            .ToListAsync();

        var result = rows.Select(i => new
        {
            i.Id, i.CreatedAt, i.Description, i.Amount, i.Discount, i.Paid, i.Method, i.PatientId, i.VisitId,
            i.PatientName, i.PatientCode,
            Net = i.Amount - i.Discount,
            Remaining = i.Amount - i.Discount - i.Paid
        }).ToList();

        return Ok(new
        {
            items = result,
            totals = new
            {
                Net = result.Sum(i => i.Net),
                Paid = result.Sum(i => i.Paid),
                Remaining = result.Sum(i => i.Remaining),
                Count = result.Count
            }
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateInvoiceRequest r)
    {
        if (r.Amount <= 0 || r.Discount < 0 || r.Paid < 0 || r.Discount > r.Amount || r.Paid > r.Amount - r.Discount)
            return BadRequest(new { message = "قيم الفاتورة غير صحيحة" });
        if (!await _db.Patients.AnyAsync(p => p.Id == r.PatientId))
            return NotFound(new { message = "المريض غير موجود" });
        if (r.VisitId != null && !await _db.Visits.AnyAsync(v => v.Id == r.VisitId && v.PatientId == r.PatientId))
            return BadRequest(new { message = "الزيارة غير مرتبطة بهذا المريض" });

        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _tenant.ClinicId);
        var visitId = r.VisitId;
        var linkedAppointmentId = r.AppointmentId ?? await TodaysUnbilledAppointmentAsync(r.PatientId, ClinicClock.Now(clinic));
        if (linkedAppointmentId is int appointmentId)
        {
            var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId);
            if (appt == null || appt.PatientId != r.PatientId)
                return BadRequest(new { message = "الحجز غير مرتبط بهذا المريض" });
            if (await _db.Invoices.AnyAsync(i => i.AppointmentId == appointmentId))
                return Conflict(new { message = "تم تسجيل فاتورة هذه الزيارة من قبل" });
            visitId ??= await _db.Visits.Where(v => v.AppointmentId == appointmentId).Select(v => (int?)v.Id).FirstOrDefaultAsync();
        }

        var invoice = new Invoice
        {
            PatientId = r.PatientId,
            VisitId = visitId,
            AppointmentId = linkedAppointmentId,
            Description = string.IsNullOrWhiteSpace(r.Description) ? "كشف" : r.Description.Trim(),
            Amount = r.Amount,
            Discount = r.Discount,
            Paid = r.Paid,
            Method = r.Method,
            CreatedAt = ClinicClock.Now(clinic),
            CreatedByUserId = _tenant.RequireUserId()
        };
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync();
        if (invoice.AppointmentId != null) await _notifier.QueueChangedAsync();
        return Ok(new { invoice.Id });
    }

    /// <summary>The patient's visit today that has no invoice yet, so the first invoice of the day pays for it.</summary>
    private async Task<int?> TodaysUnbilledAppointmentAsync(int patientId, DateTime now)
    {
        var start = now.Date;
        var end = start.AddDays(1);
        return await _db.Appointments
            .Where(a => a.PatientId == patientId && a.ScheduledAt >= start && a.ScheduledAt < end
                        && (a.Status == AppointmentStatus.Waiting || a.Status == AppointmentStatus.InConsultation || a.Status == AppointmentStatus.Done)
                        && !_db.Invoices.Any(i => i.AppointmentId == a.Id))
            .OrderBy(a => a.ScheduledAt)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync();
    }

    [HttpPost("{id:int}/payments")]
    public async Task<IActionResult> AddPayment(int id, AddPaymentRequest r)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == id);
        if (invoice == null) return NotFound();
        if (r.Amount <= 0 || r.Amount > invoice.Remaining)
            return BadRequest(new { message = "المبلغ أكبر من المتبقي على الفاتورة" });

        invoice.Paid += r.Amount;
        await _db.SaveChangesAsync();
        if (invoice.AppointmentId != null) await _notifier.QueueChangedAsync();
        return NoContent();
    }
}
