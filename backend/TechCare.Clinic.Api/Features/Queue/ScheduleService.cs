using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Queue;

/// <summary>
/// Single source of truth for bookable times, shared by the online booking page and reception.
/// A slot is free when no pending booking overlaps it and, for today, the patients already in the clinic
/// (inside + waiting) are expected to be done before it starts.
/// </summary>
public class ScheduleService
{
    public const int MaxDaysAhead = 30;

    private readonly ClinicDbContext _db;
    private readonly QueueService _queue;

    public ScheduleService(ClinicDbContext db, QueueService queue)
    {
        _db = db;
        _queue = queue;
    }

    public static int SlotMinutes(Domain.Clinic clinic) => Math.Max(5, clinic.AvgMinutesPerPatient);

    public async Task<List<string>> AvailableSlotsAsync(Domain.Clinic clinic, DateOnly date, int? ignoreAppointmentId = null)
    {
        var now = ClinicClock.Now(clinic);
        var today = DateOnly.FromDateTime(now);
        if (date < today || date > today.AddDays(MaxDaysAhead) || !IsWorkDay(clinic, date)) return new();

        var step = SlotMinutes(clinic);
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var ignoreId = ignoreAppointmentId ?? 0;
        var booked = await _db.Appointments
            .Where(a => a.ScheduledAt >= dayStart && a.ScheduledAt < dayStart.AddDays(1)
                        && a.Status == AppointmentStatus.Booked && a.Id != ignoreId)
            .Select(a => a.ScheduledAt)
            .ToListAsync();

        var earliest = now;
        if (date == today)
        {
            var snapshot = await _queue.GetSnapshotAsync(clinic);
            if (snapshot.DoctorFreeAt > earliest) earliest = snapshot.DoctorFreeAt;
        }

        var slots = new List<string>();
        for (var t = clinic.WorkStart; t.AddMinutes(step) <= clinic.WorkEnd && t >= clinic.WorkStart; t = t.AddMinutes(step))
        {
            var start = date.ToDateTime(t);
            if (start < earliest) continue;
            var end = start.AddMinutes(step);
            if (booked.Any(b => b < end && start < b.AddMinutes(step))) continue;
            slots.Add(t.ToString("HH:mm"));
        }
        return slots;
    }

    public async Task<bool> IsAvailableAsync(Domain.Clinic clinic, DateTime at, int? ignoreAppointmentId = null)
    {
        if (at.Second != 0 || at.Millisecond != 0) return false;
        var slots = await AvailableSlotsAsync(clinic, DateOnly.FromDateTime(at), ignoreAppointmentId);
        return slots.Contains(TimeOnly.FromDateTime(at).ToString("HH:mm"));
    }

    private static bool IsWorkDay(Domain.Clinic clinic, DateOnly date) =>
        clinic.WorkDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(date.DayOfWeek.ToString()[..3], StringComparer.OrdinalIgnoreCase);
}
