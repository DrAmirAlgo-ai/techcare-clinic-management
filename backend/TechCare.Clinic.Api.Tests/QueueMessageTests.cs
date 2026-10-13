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
/// PLAN.md 6.1 — the front desk's notes to the doctor (and the doctor's back to the front desk) live on
/// today's thread of the queue page: short text, optionally pointed at one of the day's queue entries, readable
/// by everyone who may see the queue, and never readable across clinics.
/// </summary>
[Collection(ApiCollection.Name)]
public class QueueMessageTests
{
    private static int _seq;

    private readonly ApiFactory _factory;

    public QueueMessageTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_note_sent_by_reception_reaches_the_doctor_with_sender_and_patient_names()
    {
        var (clinic, now) = await ClinicNowAsync();
        var appt = await AddAppointmentAsync(now.AddMinutes(-10));

        using var client = await ClientAsync(TestData.ReceptionRole);
        using var created = await client.PostAsJsonAsync("/api/queue/messages", new
        {
            text = "المريض في الاستقبال وجاهز",
            appointmentId = appt.Id
        });
        created.EnsureSuccessStatusCode();
        var sent = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("المريض في الاستقبال وجاهز", sent.GetProperty("text").GetString());
        Assert.Equal(appt.Id, sent.GetProperty("appointmentId").GetInt32());

        // The doctor opens the thread: the note is there, wearing the sender's and the patient's names.
        using var doctor = await ClientAsync(TestData.DoctorRole);
        using var list = await doctor.GetAsync("/api/queue/messages");
        list.EnsureSuccessStatusCode();
        var body = await list.Content.ReadFromJsonAsync<JsonElement>();
        var line = body.EnumerateArray()
            .First(e => e.GetProperty("id").GetInt32() == sent.GetProperty("id").GetInt32());
        Assert.Equal("الاستقبال", line.GetProperty("senderName").GetString());
        Assert.Equal(appt.PatientName, line.GetProperty("patientName").GetString());

        // The row itself belongs to this clinic and carries the sender snapshot.
        var row = await _factory.WithClinicAsync(clinic.Id,
            db => db.QueueMessages.FirstAsync(m => m.Text == "المريض في الاستقبال وجاهز"));
        Assert.Equal(clinic.Id, row.ClinicId);
        Assert.Equal(appt.Id, row.AppointmentId);
        Assert.Equal("الاستقبال", row.SenderName);
    }

    [Fact]
    public async Task An_empty_or_overlong_note_is_refused()
    {
        using var client = await ClientAsync(TestData.ReceptionRole);

        using var empty = await client.PostAsJsonAsync("/api/queue/messages", new { text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var overlong = await client.PostAsJsonAsync("/api/queue/messages", new { text = new string('م', 301) });
        Assert.Equal(HttpStatusCode.BadRequest, overlong.StatusCode);
    }

    [Fact]
    public async Task A_note_cannot_point_at_another_clinics_appointment()
    {
        var clinicBId = await TestData.EnsureClinicBAsync(_factory);
        var foreignId = await _factory.WithClinicAsync(clinicBId, async db =>
        {
            var clinic = await db.Clinics.FirstAsync(c => c.Id == clinicBId);
            db.Appointments.Add(new Appointment
            {
                PatientName = "مريض عيادة ب",
                ScheduledAt = ClinicClock.Now(clinic),
                Status = AppointmentStatus.Waiting,
                Source = AppointmentSource.Reception,
                TrackingToken = QueueService.NewTrackingToken()
            });
            await db.SaveChangesAsync();
            return db.Appointments.OrderByDescending(a => a.Id).First().Id;
        });

        using var client = await ClientAsync(TestData.ReceptionRole);
        using var response = await client.PostAsJsonAsync("/api/queue/messages", new
        {
            text = "رسالة مرتبطة بحجز آخر",
            appointmentId = foreignId
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Only_todays_notes_are_listed()
    {
        var (clinic, now) = await ClinicNowAsync();

        // A yesterday's note, planted directly: the thread must keep it out.
        await _factory.WithClinicAsync(clinic.Id, db =>
        {
            db.QueueMessages.Add(new QueueMessage
            {
                SenderId = 1,
                SenderName = "امس",
                Text = "رسالة من أمس",
                SentAt = now.Date.AddDays(-1).AddHours(10)
            });
            return db.SaveChangesAsync();
        });

        using var client = await ClientAsync(TestData.ReceptionRole);
        using var created = await client.PostAsJsonAsync("/api/queue/messages", new { text = "رسالة من اليوم" });
        created.EnsureSuccessStatusCode();

        using var list = await client.GetAsync("/api/queue/messages");
        list.EnsureSuccessStatusCode();
        var body = await list.Content.ReadFromJsonAsync<JsonElement>();
        var texts = body.EnumerateArray().Select(e => e.GetProperty("text").GetString()!).ToList();
        Assert.Contains("رسالة من اليوم", texts);
        Assert.DoesNotContain("رسالة من أمس", texts);
    }

    [Fact]
    public async Task Another_clinic_cannot_read_this_thread()
    {
        await TestData.EnsureClinicBAsync(_factory);

        using var client = await ClientAsync(TestData.ReceptionRole);
        using var created = await client.PostAsJsonAsync("/api/queue/messages", new { text = "بلاغ داخلي للعيادة أ" });
        created.EnsureSuccessStatusCode();

        // The other clinic's own reception: its thread must not carry this note.
        var token = await _factory.TokenAsync(TestEnvironment.ClinicBSlug, TestData.ReceptionRole);
        using var other = _factory.CreateAuthenticatedClient(token);
        using var list = await other.GetAsync("/api/queue/messages");
        list.EnsureSuccessStatusCode();
        var body = await list.Content.ReadFromJsonAsync<JsonElement>();
        var texts = body.EnumerateArray().Select(e => e.GetProperty("text").GetString()!).ToList();
        Assert.DoesNotContain("بلاغ داخلي للعيادة أ", texts);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private async Task<HttpClient> ClientAsync(string role)
    {
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, role);
        return _factory.CreateAuthenticatedClient(token);
    }

    private async Task<(Domain.Clinic Clinic, DateTime Now)> ClinicNowAsync() =>
        await _factory.WithServicesAsync(async s =>
        {
            var db = s.GetRequiredService<ClinicDbContext>();
            var clinic = await db.Clinics.FirstAsync(c => c.Slug == TestEnvironment.ClinicASlug);
            return (clinic, ClinicClock.Now(clinic));
        });

    /// <summary>A waiting queue entry of its own, so a note may point at it without touching a fixture row.</summary>
    private async Task<Appointment> AddAppointmentAsync(DateTime scheduledAt)
    {
        var seq = Interlocked.Increment(ref _seq);
        return await _factory.WithServicesAsync(async s =>
        {
            var db = s.GetRequiredService<ClinicDbContext>();
            var tenant = s.GetRequiredService<TenantContext>();
            var clinic = await db.Clinics.FirstAsync(c => c.Slug == TestEnvironment.ClinicASlug);
            tenant.SetClinic(clinic.Id);

            var appointment = new Appointment
            {
                PatientName = $"مريض رسائل {seq}",
                ScheduledAt = scheduledAt,
                Status = AppointmentStatus.Waiting,
                Source = AppointmentSource.Reception,
                TrackingToken = QueueService.NewTrackingToken()
            };
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
            return appointment;
        });
    }
}
