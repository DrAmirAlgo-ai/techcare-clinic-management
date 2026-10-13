using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Queue;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Dashboard;

[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = Permissions.Dashboard)]
public class DashboardController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly TenantContext _tenant;
    private readonly QueueService _queue;

    public DashboardController(ClinicDbContext db, TenantContext tenant, QueueService queue)
    {
        _db = db;
        _tenant = tenant;
        _queue = queue;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _tenant.ClinicId);
        var today = ClinicClock.Today(clinic);
        var start = today.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);

        var appointments = await _db.Appointments
            .Where(a => a.ScheduledAt >= start && a.ScheduledAt < end)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();
        int Count(AppointmentStatus s) => appointments.FirstOrDefault(a => a.Status == s)?.Count ?? 0;

        var invoices = await _db.Invoices
            .Where(i => i.CreatedAt >= start && i.CreatedAt < end)
            .Select(i => new { i.Paid })
            .ToListAsync();

        var queue = await _queue.GetSnapshotAsync(clinic);

        return Ok(new
        {
            date = today,
            totalPatients = await _db.Patients.CountAsync(),
            newPatientsToday = await _db.Patients.CountAsync(p => p.CreatedAt >= start && p.CreatedAt < end),
            appointmentsToday = appointments.Sum(a => a.Count),
            booked = Count(AppointmentStatus.Booked),
            waiting = Count(AppointmentStatus.Waiting),
            done = Count(AppointmentStatus.Done),
            noShow = Count(AppointmentStatus.NoShow),
            revenueToday = invoices.Sum(i => i.Paid),
            unpaidToday = queue.UnpaidCount,
            queue = new { queue.Current, queue.WaitingCount, queue.AvgMinutesPerPatient, queue.AvgIsLearned }
        });
    }
}
