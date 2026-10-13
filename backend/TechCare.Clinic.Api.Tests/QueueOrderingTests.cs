using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Queue;
using TechCare.Clinic.Api.Infrastructure;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_FULL.md phase 4 — the queue order respects the appointment time (4.1): urgent first, then the patients
/// whose time has come (on time before late, so a latecomer goes to the back), then early arrivals waiting for
/// a future slot in slot order; the walk-in takes the place the current congestion gives. The expected wait is
/// learned from the doctor's real consultation durations (4.2), and the visit's invoice carries both the
/// appointment and the visit, one invoice per visit (4.3).
/// </summary>
[Collection(ApiCollection.Name)]
public class QueueOrderingTests
{
    private static int _seq;

    private readonly ApiFactory _factory;

    public QueueOrderingTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_due_walk_in_goes_before_an_early_arrival_waiting_for_a_future_slot()
    {
        var (_, now) = await ClinicNowAsync();

        // Checked in an hour early for a slot two hours from now.
        var early = await AddWaitingAsync(now.AddHours(2), now.AddHours(-1));
        // A walk-in arriving right now.
        var walkIn = await AddWaitingAsync(now, now);

        var waiting = await SnapshotWaitingAsync();

        var earlyPos = IndexOf(waiting, early.Id);
        var walkInPos = IndexOf(waiting, walkIn.Id);
        Assert.True(walkInPos >= 0 && earlyPos >= 0 && walkInPos < earlyPos,
            $"the walk-in ({walkInPos}) must wait ahead of the early arrival for a future slot ({earlyPos})");

        // And the early arrival is never estimated to go in before its slot.
        Assert.True(waiting[earlyPos].GetProperty("estimatedAt").GetDateTime() >= early.ScheduledAt,
            "the early arrival's estimate must not beat its slot time");
    }

    [Fact]
    public async Task A_patient_late_for_their_slot_goes_behind_the_on_time_line()
    {
        var (_, now) = await ClinicNowAsync();

        // Their slot was an hour ago and they checked in five minutes ago: late, and checked in first today.
        var late = await AddWaitingAsync(now.AddHours(-1), now.AddMinutes(-5));
        // This walk-in arrives after them, on time.
        var walkIn = await AddWaitingAsync(now, now);

        var waiting = await SnapshotWaitingAsync();

        var latePos = IndexOf(waiting, late.Id);
        var walkInPos = IndexOf(waiting, walkIn.Id);
        Assert.True(walkInPos >= 0 && latePos >= 0 && walkInPos < latePos,
            "the latecomer must wait behind the on-time patients already in the line");
    }

    [Fact]
    public async Task An_urgent_patient_is_called_before_the_due_patients_who_arrived_earlier()
    {
        var (_, now) = await ClinicNowAsync();

        var onTime = await AddWaitingAsync(now.AddHours(-1), now.AddMinutes(-10));
        var urgent = await AddWaitingAsync(now.AddMinutes(-2), now.AddMinutes(-1), urgent: true);

        var waiting = await SnapshotWaitingAsync();

        var onTimePos = IndexOf(waiting, onTime.Id);
        var urgentPos = IndexOf(waiting, urgent.Id);
        Assert.True(urgentPos >= 0 && onTimePos >= 0 && urgentPos < onTimePos,
            "the urgent patient must be called first");
    }

    [Fact]
    public async Task Call_next_follows_the_same_ordering_as_the_waiting_list()
    {
        var (clinic, now) = await ClinicNowAsync();

        var late = await AddWaitingAsync(now.AddHours(-1), now.AddMinutes(-5));
        var walkIn = await AddWaitingAsync(now, now);

        // The shared fixture queue may hold more rows; "next" must be the first one in the live order.
        var expected = await _factory.WithClinicAsync(clinic.Id, db =>
        {
            var t = ClinicClock.Now(clinic);
            return db.Appointments
                .Where(a => a.ScheduledAt >= t.Date && a.ScheduledAt < t.Date.AddDays(1) && a.Status == AppointmentStatus.Waiting)
                .OrderByDescending(a => a.IsUrgent)
                .ThenBy(a => a.ScheduledAt > t)
                .ThenBy(a => a.ScheduledAt > t ? a.ScheduledAt : DateTime.MinValue)
                .ThenBy(a => a.CheckedInAt > a.ScheduledAt)
                .ThenBy(a => a.QueueNumber)
                .Select(a => a.Id)
                .FirstAsync();
        });

        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.ReceptionRole);
        using var client = _factory.CreateAuthenticatedClient(token);
        using var response = await client.PostAsync("/api/queue/next", new StringContent(""));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var calledId = body.GetProperty("id").GetInt32();
        Assert.Equal(expected, calledId);

        var after = await _factory.WithClinicAsync(clinic.Id, db => db.Appointments.FirstAsync(a => a.Id == calledId));
        Assert.Equal(AppointmentStatus.InConsultation, after.Status);
        Assert.NotNull(after.StartedAt);
        _ = (late, walkIn);
    }

    [Fact]
    public async Task The_expected_wait_uses_the_doctors_real_average_consultation_time()
    {
        var (_, now) = await ClinicNowAsync();

        // Six finished consultations of 45 minutes: enough samples for the average to be learned.
        var morning = now.Date.AddDays(-1).AddHours(9);
        for (var i = 0; i < 6; i++)
            await AddDoneAsync(morning.AddHours(i + 1));

        var a = await AddWaitingAsync(now, now.AddMinutes(-20));
        var b = await AddWaitingAsync(now, now.AddMinutes(-10));

        var snapshot = await SnapshotAsync();
        Assert.Equal(45, snapshot.GetProperty("avgMinutesPerPatient").GetInt32());
        Assert.True(snapshot.GetProperty("avgIsLearned").GetBoolean(),
            "the average must come from this clinic's real consultation durations");

        var waiting = snapshot.GetProperty("waiting").EnumerateArray().ToList();
        var posA = IndexOf(waiting, a.Id);
        var posB = IndexOf(waiting, b.Id);
        Assert.True(posB == posA + 1, "same tier, consecutive arrivals: one average slot apart");
        Assert.Equal(45,
            waiting[posB].GetProperty("estimatedWaitMinutes").GetInt32() - waiting[posA].GetProperty("estimatedWaitMinutes").GetInt32());
    }

    [Fact]
    public async Task A_visit_invoice_written_from_the_file_carries_both_the_appointment_and_the_visit()
    {
        var (clinic, now) = await ClinicNowAsync();
        var walkIn = await AddWaitingAsync(now, now);

        var doctorToken = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(doctorToken);

        // From the patient file: the invoice names today's appointment; the visit does not exist yet.
        using var created = await client.PostAsJsonAsync("/api/invoices", new
        {
            patientId = walkIn.PatientId,
            appointmentId = walkIn.Id,
            description = "كشف",
            amount = 300m,
            discount = 0m,
            paid = 300m,
            method = "Cash"
        });
        created.EnsureSuccessStatusCode();
        var invoiceId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // A second invoice for the same visit is refused: one invoice per visit.
        using var duplicate = await client.PostAsJsonAsync("/api/invoices", new
        {
            patientId = walkIn.PatientId,
            appointmentId = walkIn.Id,
            description = "كشف",
            amount = 300m,
            discount = 0m,
            paid = 300m,
            method = "Cash"
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // The visit is recorded from the file; its id must land on the invoice that was created before it.
        using var visitResponse = await client.PostAsJsonAsync("/api/visits", new
        {
            patientId = walkIn.PatientId,
            appointmentId = walkIn.Id,
            chiefComplaint = "تألم بالبطن",
            diagnosis = "مغص حاد"
        });
        visitResponse.EnsureSuccessStatusCode();
        var visitId = (await visitResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var invoice = await _factory.WithClinicAsync(clinic.Id, db => db.Invoices.FirstAsync(i => i.Id == invoiceId));
        Assert.Equal(walkIn.Id, invoice.AppointmentId);
        Assert.Equal(visitId, invoice.VisitId);

        // The payment state is visible on the queue side of the same visit.
        var snapshot = await SnapshotAsync();
        var entry = snapshot.GetProperty("done").EnumerateArray()
            .First(e => e.GetProperty("appointmentId").GetInt32() == walkIn.Id);
        Assert.Equal("Paid", entry.GetProperty("payment").GetString());
        Assert.Equal(300m, entry.GetProperty("amountDue").GetDecimal());
        Assert.Equal(300m, entry.GetProperty("amountPaid").GetDecimal());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private async Task<(Domain.Clinic Clinic, DateTime Now)> ClinicNowAsync() =>
        await _factory.WithServicesAsync(async s =>
        {
            var db = s.GetRequiredService<ClinicDbContext>();
            var clinic = await db.Clinics.FirstAsync(c => c.Slug == TestEnvironment.ClinicASlug);
            return (clinic, ClinicClock.Now(clinic));
        });

    /// <summary>Adds a patient and a queue entry with the requested schedule; the queue number is the next free one.</summary>
    private async Task<Appointment> AddWaitingAsync(DateTime scheduledAt, DateTime? checkedInAt, bool urgent = false)
    {
        var seq = Interlocked.Increment(ref _seq);
        return await _factory.WithServicesAsync(async s =>
        {
            var db = s.GetRequiredService<ClinicDbContext>();
            var tenant = s.GetRequiredService<TenantContext>();
            var clinic = await db.Clinics.FirstAsync(c => c.Slug == TestEnvironment.ClinicASlug);
            tenant.SetClinic(clinic.Id);
            var now = ClinicClock.Now(clinic);

            var maxNumber = await db.Appointments
                .Where(a => a.ScheduledAt >= now.Date && a.ScheduledAt < now.Date.AddDays(1) && a.QueueNumber != null)
                .MaxAsync(a => (int?)a.QueueNumber) ?? 0;

            var patient = new Patient
            {
                Code = $"Q4{seq:D5}",
                FullName = $"مريض طابور {seq}",
                Phone = $"012000000{seq:D2}",
                CreatedAt = now
            };
            db.Patients.Add(patient);
            await db.SaveChangesAsync();

            var appointment = new Appointment
            {
                PatientId = patient.Id,
                PatientName = patient.FullName,
                Phone = patient.Phone,
                ScheduledAt = scheduledAt,
                Status = checkedInAt == null ? AppointmentStatus.Booked : AppointmentStatus.Waiting,
                QueueNumber = checkedInAt == null ? null : maxNumber + 1,
                CheckedInAt = checkedInAt,
                IsUrgent = urgent,
                Source = AppointmentSource.Reception,
                TrackingToken = QueueService.NewTrackingToken()
            };
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
            return appointment;
        });
    }

    private async Task AddDoneAsync(DateTime startedAt)
    {
        var seq = Interlocked.Increment(ref _seq);
        await _factory.WithServicesAsync(async s =>
        {
            var db = s.GetRequiredService<ClinicDbContext>();
            var tenant = s.GetRequiredService<TenantContext>();
            var clinic = await db.Clinics.FirstAsync(c => c.Slug == TestEnvironment.ClinicASlug);
            tenant.SetClinic(clinic.Id);

            var patient = new Patient
            {
                Code = $"Q4{seq:D5}",
                FullName = $"مريض منتهي {seq}",
                CreatedAt = startedAt
            };
            db.Patients.Add(patient);
            await db.SaveChangesAsync();

            db.Appointments.Add(new Appointment
            {
                PatientId = patient.Id,
                PatientName = patient.FullName,
                ScheduledAt = startedAt,
                Status = AppointmentStatus.Done,
                StartedAt = startedAt,
                FinishedAt = startedAt.AddMinutes(45),
                Source = AppointmentSource.Reception,
                TrackingToken = QueueService.NewTrackingToken()
            });
            await db.SaveChangesAsync();
            return true;
        });
    }

    private async Task<JsonElement> SnapshotAsync()
    {
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.ReceptionRole);
        using var client = _factory.CreateAuthenticatedClient(token);
        using var response = await client.GetAsync("/api/queue");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<List<JsonElement>> SnapshotWaitingAsync()
    {
        var snapshot = await SnapshotAsync();
        return snapshot.GetProperty("waiting").EnumerateArray().ToList();
    }

    private static int IndexOf(List<JsonElement> waiting, int appointmentId) =>
        waiting.FindIndex(w => w.GetProperty("appointmentId").GetInt32() == appointmentId);
}