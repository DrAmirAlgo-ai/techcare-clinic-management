using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Queue;

public class QueueEntry
{
    public int AppointmentId { get; init; }
    public int? PatientId { get; init; }
    public string PatientName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public int? QueueNumber { get; init; }
    public AppointmentType Type { get; init; }
    public AppointmentSource Source { get; init; }
    public bool IsUrgent { get; init; }
    public DateTime ScheduledAt { get; init; }
    public DateTime? CheckedInAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public int Position { get; set; }
    public int EstimatedWaitMinutes { get; set; }
    /// <summary>Expected time this patient goes in, projected from the live queue.</summary>
    public DateTime? EstimatedAt { get; set; }

    public int? InvoiceId { get; set; }
    public decimal AmountDue { get; set; }
    public decimal AmountPaid { get; set; }
    public PaymentState Payment { get; set; } = PaymentState.Unpaid;
}

public enum PaymentState
{
    Unpaid,
    Partial,
    Paid
}

public class QueueSnapshot
{
    public QueueEntry? Current { get; init; }
    public List<QueueEntry> Waiting { get; init; } = new();
    public List<QueueEntry> BookedNotArrived { get; init; } = new();
    public List<QueueEntry> Done { get; init; } = new();
    public int WaitingCount => Waiting.Count;
    public int DoneToday { get; init; }
    /// <summary>Patients who arrived today and haven't fully paid for their visit.</summary>
    public int UnpaidCount { get; init; }
    public decimal ConsultationFee { get; init; }
    public decimal FollowUpFee { get; init; }
    public int AvgMinutesPerPatient { get; init; }
    /// <summary>True when the average comes from this clinic's real consultation durations rather than the configured default.</summary>
    public bool AvgIsLearned { get; init; }
    /// <summary>When the doctor is expected to finish everyone already in the clinic (inside + waiting).</summary>
    public DateTime DoctorFreeAt { get; init; }
}

public class QueueException : Exception
{
    public QueueException(string message) : base(message) { }
}

/// <summary>
/// A queue action names an appointment or a patient that this clinic cannot see: another clinic's row, or a row
/// that no longer exists. Mapped to a 404 by <see cref="QueueExceptionFilter"/> so a cross-clinic attempt is
/// indistinguishable from a missing row and never reveals that the row exists somewhere else
/// (PLAN_ADDENDUM.md 0.2: "no read, no write, 404 or an empty list").
/// </summary>
public class QueueItemNotFoundException : QueueException
{
    public QueueItemNotFoundException(string message) : base(message) { }
}

/// <summary>One line of today's front-desk ↔ clinical team thread, as the queue endpoints return it.</summary>
public record QueueMessageDto(int Id, int? AppointmentId, string? PatientName, int SenderId, string SenderName, string Text, DateTime SentAt);

public class QueueService
{
    private const int MinSamplesToLearn = 5;
    private const int SampleSize = 30;
    private const int MaxMessageLength = 300;

    private readonly ClinicDbContext _db;
    private readonly TenantContext _tenant;

    public QueueService(ClinicDbContext db, TenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public static string NewTrackingToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<Domain.Clinic> CurrentClinicAsync() =>
        await _db.Clinics.FirstAsync(c => c.Id == _db.CurrentClinicId);

    public async Task<QueueSnapshot> GetSnapshotAsync(Domain.Clinic clinic)
    {
        var now = ClinicClock.Now(clinic);
        var (start, end) = DayRange(now);

        var today = await _db.Appointments
            .Where(a => a.ScheduledAt >= start && a.ScheduledAt < end)
            .ToListAsync();

        var (avg, learned) = await AverageConsultationMinutesAsync(clinic, now);

        var currentAppt = today.Where(a => a.Status == AppointmentStatus.InConsultation).MinBy(a => a.StartedAt);
        var waiting = QueuePosition(today.Where(a => a.Status == AppointmentStatus.Waiting), now)
            .Select(ToEntry).ToList();

        // Time left for the patient currently inside, then one average slot per person ahead.
        // An early arrival with a future slot does not go in before that slot (PLAN_FULL.md 4.1).
        var remainingCurrent = currentAppt?.StartedAt is DateTime startedAt
            ? Math.Max(1, avg - (int)(now - startedAt).TotalMinutes)
            : 0;
        var lastEntryAt = now;
        for (var i = 0; i < waiting.Count; i++)
        {
            waiting[i].Position = i + 1;
            var projected = remainingCurrent + i * avg;
            var slotFloor = waiting[i].ScheduledAt > now
                ? (int)Math.Ceiling((waiting[i].ScheduledAt - now).TotalMinutes)
                : 0;
            waiting[i].EstimatedWaitMinutes = Math.Max(projected, slotFloor);
            waiting[i].EstimatedAt = now.AddMinutes(waiting[i].EstimatedWaitMinutes);
            lastEntryAt = now.AddMinutes(waiting[i].EstimatedWaitMinutes);
        }

        // Pending bookings go in at their slot time, or later if the people already present run over.
        var doctorFreeAt = waiting.Count == 0 ? now.AddMinutes(remainingCurrent) : lastEntryAt.AddMinutes(avg);
        var cursor = doctorFreeAt;
        var booked = today.Where(a => a.Status == AppointmentStatus.Booked).OrderBy(a => a.ScheduledAt).Select(ToEntry).ToList();
        foreach (var b in booked)
        {
            var estimate = b.ScheduledAt > cursor ? b.ScheduledAt : cursor;
            b.EstimatedAt = estimate;
            b.EstimatedWaitMinutes = Math.Max(0, (int)Math.Ceiling((estimate - now).TotalMinutes));
            cursor = estimate.AddMinutes(avg);
        }

        var current = currentAppt == null ? null : ToEntry(currentAppt);
        var done = today.Where(a => a.Status == AppointmentStatus.Done)
            .OrderByDescending(a => a.FinishedAt).Select(ToEntry).ToList();

        var arrived = waiting.Concat(done).ToList();
        if (current != null) arrived.Add(current);
        await AttachPaymentsAsync(arrived);

        return new QueueSnapshot
        {
            Current = current,
            Waiting = waiting,
            BookedNotArrived = booked,
            Done = done,
            DoneToday = done.Count,
            UnpaidCount = arrived.Count(e => e.Payment != PaymentState.Paid),
            ConsultationFee = clinic.ConsultationFee,
            FollowUpFee = clinic.FollowUpFee,
            AvgMinutesPerPatient = avg,
            AvgIsLearned = learned,
            DoctorFreeAt = doctorFreeAt
        };
    }

    private async Task AttachPaymentsAsync(List<QueueEntry> entries)
    {
        if (entries.Count == 0) return;
        var ids = entries.Select(e => e.AppointmentId).ToList();
        var invoices = await _db.Invoices
            .Where(i => i.AppointmentId != null && ids.Contains(i.AppointmentId.Value))
            .Select(i => new { i.Id, AppointmentId = i.AppointmentId!.Value, i.Amount, i.Discount, i.Paid })
            .ToListAsync();

        foreach (var e in entries)
        {
            var inv = invoices.FirstOrDefault(i => i.AppointmentId == e.AppointmentId);
            if (inv == null) continue;
            e.InvoiceId = inv.Id;
            e.AmountDue = inv.Amount - inv.Discount;
            e.AmountPaid = inv.Paid;
            e.Payment = inv.Paid >= e.AmountDue ? PaymentState.Paid : inv.Paid > 0 ? PaymentState.Partial : PaymentState.Unpaid;
        }
    }

    public async Task<Appointment> WalkInAsync(int patientId, AppointmentType type, bool isUrgent, string? notes)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId)
                      ?? throw new QueueItemNotFoundException("المريض غير موجود");
        var clinic = await CurrentClinicAsync();
        var now = ClinicClock.Now(clinic);
        var (start, end) = DayRange(now);

        var existing = await _db.Appointments
            .Where(a => a.PatientId == patient.Id && a.ScheduledAt >= start && a.ScheduledAt < end
                        && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Waiting || a.Status == AppointmentStatus.InConsultation))
            .OrderBy(a => a.ScheduledAt)
            .FirstOrDefaultAsync();
        if (existing is { Status: AppointmentStatus.Waiting or AppointmentStatus.InConsultation })
            throw new QueueException("المريض موجود بالفعل في قائمة اليوم");
        if (existing != null)
        {
            existing.Type = type;
            existing.IsUrgent = isUrgent;
            await CheckInCoreAsync(existing, now);
            await _db.SaveChangesAsync();
            return existing;
        }

        var appt = new Appointment
        {
            PatientId = patient.Id,
            PatientName = patient.FullName,
            Phone = patient.Phone,
            ScheduledAt = now,
            Type = type,
            IsUrgent = isUrgent,
            Notes = notes,
            Source = AppointmentSource.Reception,
            TrackingToken = NewTrackingToken()
        };
        _db.Appointments.Add(appt);
        await CheckInCoreAsync(appt, now);
        await _db.SaveChangesAsync();
        return appt;
    }

    public async Task CheckInAsync(int appointmentId)
    {
        var appt = await GetAsync(appointmentId);
        if (appt.Status != AppointmentStatus.Booked)
            throw new QueueException("الحجز ليس في حالة انتظار الوصول");

        var clinic = await CurrentClinicAsync();
        var now = ClinicClock.Now(clinic);
        if (DateOnly.FromDateTime(appt.ScheduledAt) != DateOnly.FromDateTime(now))
            throw new QueueException("تسجيل الحضور متاح لحجوزات اليوم فقط");

        await CheckInCoreAsync(appt, now);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Finishes whoever is inside and calls the next waiting patient, in <see cref="QueuePosition"/> order:
    /// urgent first, then the due patients (on time before late), then early arrivals waiting for a future slot.
    /// </summary>
    public async Task<Appointment?> CallNextAsync()
    {
        var clinic = await CurrentClinicAsync();
        var now = ClinicClock.Now(clinic);
        var (start, end) = DayRange(now);

        await FinishCurrentAsync(now, start, end);

        var next = await QueuePosition(
                _db.Appointments.Where(a => a.ScheduledAt >= start && a.ScheduledAt < end && a.Status == AppointmentStatus.Waiting),
                now)
            .FirstOrDefaultAsync();

        if (next != null)
        {
            next.Status = AppointmentStatus.InConsultation;
            next.StartedAt = now;
        }

        await _db.SaveChangesAsync();
        return next;
    }

    /// <summary>
    /// PLAN.md 6.1 — today's front-desk ↔ clinical team thread: the clinic's own notes sent since the day began,
    /// oldest first, each one wearing the name of the patient it points at when it points at one.
    /// </summary>
    public async Task<List<QueueMessageDto>> TodayMessagesAsync()
    {
        var clinic = await CurrentClinicAsync();
        var now = ClinicClock.Now(clinic);
        var (start, end) = DayRange(now);

        var rows = await _db.QueueMessages
            .Where(m => m.SentAt >= start && m.SentAt < end)
            .OrderBy(m => m.SentAt)
            .ThenBy(m => m.Id)
            .ToListAsync();
        if (rows.Count == 0) return [];

        var ids = rows.Where(r => r.AppointmentId != null).Select(r => r.AppointmentId!.Value).Distinct().ToList();
        var names = ids.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Appointments
                .Where(a => ids.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.PatientName);

        return rows.Select(r => new QueueMessageDto(
            r.Id,
            r.AppointmentId,
            r.AppointmentId is int id && names.TryGetValue(id, out var patientName) ? patientName : null,
            r.SenderId,
            r.SenderName,
            r.Text,
            r.SentAt)).ToList();
    }

    /// <summary>
    /// PLAN.md 6.1 — appends a short note to today's thread. The optional appointment link must name one of
    /// this clinic's queue entries for today; another clinic's id is refused exactly like a missing one.
    /// </summary>
    public async Task<QueueMessageDto> SendMessageAsync(string? text, int? appointmentId)
    {
        var body = text?.Trim();
        if (string.IsNullOrWhiteSpace(body))
            throw new QueueException("اكتب نص الرسالة");
        if (body.Length > MaxMessageLength)
            throw new QueueException("الرسالة أطول من 300 حرف");

        var clinic = await CurrentClinicAsync();
        var now = ClinicClock.Now(clinic);

        Appointment? appt = null;
        if (appointmentId is int id)
        {
            appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == id)
                   ?? throw new QueueItemNotFoundException("الحجز غير موجود");
            var (start, end) = DayRange(now);
            if (appt.ScheduledAt < start || appt.ScheduledAt >= end)
                throw new QueueException("تُربط الرسالة بحجز من اليوم فقط");
        }

        var message = new QueueMessage
        {
            AppointmentId = appt?.Id,
            SenderId = _tenant.RequireUserId(),
            SenderName = _tenant.UserName ?? string.Empty,
            Text = body,
            SentAt = now
        };
        _db.QueueMessages.Add(message);
        await _db.SaveChangesAsync();

        return new QueueMessageDto(message.Id, appt?.Id, appt?.PatientName, message.SenderId, message.SenderName, message.Text, message.SentAt);
    }

    public async Task StartAsync(int appointmentId)
    {
        var appt = await GetAsync(appointmentId);
        if (appt.Status != AppointmentStatus.Waiting)
            throw new QueueException("يجب أن يكون المريض في قائمة الانتظار");

        var clinic = await CurrentClinicAsync();
        var now = ClinicClock.Now(clinic);
        var (start, end) = DayRange(now);
        await FinishCurrentAsync(now, start, end);

        appt.Status = AppointmentStatus.InConsultation;
        appt.StartedAt = now;
        await _db.SaveChangesAsync();
    }

    public async Task FinishAsync(int appointmentId)
    {
        var appt = await GetAsync(appointmentId);
        if (appt.Status is AppointmentStatus.Done or AppointmentStatus.Cancelled or AppointmentStatus.NoShow)
            return;

        var clinic = await CurrentClinicAsync();
        var now = ClinicClock.Now(clinic);
        appt.StartedAt ??= now;
        appt.Status = AppointmentStatus.Done;
        appt.FinishedAt = now;
        await _db.SaveChangesAsync();
    }

    /// <summary>Patient stepped out or isn't ready: moves them to the end of the line with a new number.</summary>
    public async Task PostponeAsync(int appointmentId)
    {
        var appt = await GetAsync(appointmentId);
        if (appt.Status != AppointmentStatus.Waiting)
            throw new QueueException("التأجيل متاح للمرضى في قائمة الانتظار فقط");

        var clinic = await CurrentClinicAsync();
        var (start, end) = DayRange(ClinicClock.Now(clinic));
        appt.QueueNumber = await NextQueueNumberAsync(start, end);
        appt.IsUrgent = false;
        await _db.SaveChangesAsync();
    }

    public async Task SetUrgentAsync(int appointmentId, bool urgent)
    {
        var appt = await GetAsync(appointmentId);
        appt.IsUrgent = urgent;
        await _db.SaveChangesAsync();
    }

    public async Task MarkNoShowAsync(int appointmentId)
    {
        var appt = await GetAsync(appointmentId);
        if (appt.Status is not (AppointmentStatus.Booked or AppointmentStatus.Waiting))
            throw new QueueException("لا يمكن تسجيل هذا الحجز كغياب");
        appt.Status = AppointmentStatus.NoShow;
        await _db.SaveChangesAsync();
    }

    public async Task CancelAsync(int appointmentId)
    {
        var appt = await GetAsync(appointmentId);
        if (appt.Status is AppointmentStatus.Done or AppointmentStatus.InConsultation)
            throw new QueueException("لا يمكن إلغاء كشف بدأ أو انتهى");
        appt.Status = AppointmentStatus.Cancelled;
        await _db.SaveChangesAsync();
    }

    private async Task<Appointment> GetAsync(int id) =>
        await _db.Appointments.FirstOrDefaultAsync(a => a.Id == id)
        ?? throw new QueueItemNotFoundException("الحجز غير موجود");

    private async Task CheckInCoreAsync(Appointment appt, DateTime now)
    {
        var (start, end) = DayRange(now);
        appt.QueueNumber = await NextQueueNumberAsync(start, end);
        appt.Status = AppointmentStatus.Waiting;
        appt.CheckedInAt = now;
    }

    private async Task<int> NextQueueNumberAsync(DateTime start, DateTime end)
    {
        var max = await _db.Appointments
            .Where(a => a.ScheduledAt >= start && a.ScheduledAt < end && a.QueueNumber != null)
            .MaxAsync(a => a.QueueNumber);
        var pendingMax = _db.ChangeTracker.Entries<Appointment>()
            .Where(e => e.State == EntityState.Added && e.Entity.QueueNumber != null)
            .Select(e => e.Entity.QueueNumber).Max();
        return Math.Max(max ?? 0, pendingMax ?? 0) + 1;
    }

    private async Task FinishCurrentAsync(DateTime now, DateTime start, DateTime end)
    {
        var inside = await _db.Appointments
            .Where(a => a.ScheduledAt >= start && a.ScheduledAt < end && a.Status == AppointmentStatus.InConsultation)
            .ToListAsync();
        foreach (var a in inside)
        {
            a.Status = AppointmentStatus.Done;
            a.FinishedAt = now;
        }
    }

    private async Task<(int Minutes, bool Learned)> AverageConsultationMinutesAsync(Domain.Clinic clinic, DateTime now)
    {
        var since = now.AddDays(-30);
        var samples = await _db.Appointments
            .Where(a => a.Status == AppointmentStatus.Done && a.StartedAt != null && a.FinishedAt != null && a.FinishedAt >= since)
            .OrderByDescending(a => a.FinishedAt)
            .Take(SampleSize)
            .Select(a => new { a.StartedAt, a.FinishedAt })
            .ToListAsync();

        var durations = samples
            .Select(s => (s.FinishedAt!.Value - s.StartedAt!.Value).TotalMinutes)
            .Where(m => m is >= 2 and <= 90)
            .ToList();

        if (durations.Count < MinSamplesToLearn)
            return (clinic.AvgMinutesPerPatient, false);

        return ((int)Math.Round(durations.Average()), true);
    }

    private static (DateTime Start, DateTime End) DayRange(DateTime now)
    {
        var start = now.Date;
        return (start, start.AddDays(1));
    }

    /// <summary>
    /// PLAN_FULL.md 4.1 — the order the waiting list is called in:
    /// <list type="number">
    /// <item>urgent patients first;</item>
    /// <item>patients whose time has come, on time before late — a latecomer goes to the back;</item>
    /// <item>early arrivals waiting for a future slot, in slot order, so the appointment time is respected.</item>
    /// </list>
    /// Within a tier the queue number (arrival order) decides, so a walk-in takes exactly the place the current
    /// congestion gives.
    /// </summary>
    private static IOrderedQueryable<Appointment> QueuePosition(IQueryable<Appointment> q, DateTime now) =>
        q.OrderByDescending(a => a.IsUrgent)
          .ThenBy(a => a.ScheduledAt > now)
          .ThenBy(a => a.ScheduledAt > now ? a.ScheduledAt : DateTime.MinValue)
          .ThenBy(a => a.CheckedInAt > a.ScheduledAt)
          .ThenBy(a => a.QueueNumber);

    /// <summary>In-memory twin of <see cref="QueuePosition(IQueryable{Appointment}, DateTime)"/>.</summary>
    private static IOrderedEnumerable<Appointment> QueuePosition(IEnumerable<Appointment> q, DateTime now) =>
        q.OrderByDescending(a => a.IsUrgent)
          .ThenBy(a => a.ScheduledAt > now)
          .ThenBy(a => a.ScheduledAt > now ? a.ScheduledAt : DateTime.MinValue)
          .ThenBy(a => a.CheckedInAt > a.ScheduledAt)
          .ThenBy(a => a.QueueNumber);

    private static QueueEntry ToEntry(Appointment a) => new()
    {
        AppointmentId = a.Id,
        PatientId = a.PatientId,
        PatientName = a.PatientName,
        Phone = a.Phone,
        QueueNumber = a.QueueNumber,
        Type = a.Type,
        Source = a.Source,
        IsUrgent = a.IsUrgent,
        ScheduledAt = a.ScheduledAt,
        CheckedInAt = a.CheckedInAt,
        StartedAt = a.StartedAt,
        FinishedAt = a.FinishedAt
    };
}
