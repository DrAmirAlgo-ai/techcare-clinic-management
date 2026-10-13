using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;
using TechCare.Clinic.Api.Tests.Infrastructure;
using static TechCare.Clinic.Api.Tests.Infrastructure.TestHttp;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_ADDENDUM.md 0.2 — clinic A must not read or change clinic B's rows through any controller. Every test uses
/// one token (clinic A's admin, who is allowed on all of A's endpoints) and reaches for B's rows by their real ids,
/// the way a curious or buggy client would. The only accepted answers are "404" or "an empty list"; on top of that
/// each test re-reads the row from clinic B's point of view to prove nothing was written.
/// </summary>
[Collection(ApiCollection.Name)]
public class ClinicIsolationTests
{
    private readonly ApiFactory _factory;

    public ClinicIsolationTests(ApiFactory factory) => _factory = factory;

    private Task<ClinicFixture> ClinicA() => ClinicFixtures.ClinicAAsync(_factory);

    private Task<ClinicFixture> ClinicB() => ClinicFixtures.ClinicBAsync(_factory);

    private async Task<HttpClient> ClinicAAdminAsync()
    {
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole);
        return _factory.CreateAuthenticatedClient(token);
    }

    private static List<int> RowIds(JsonElement array) =>
        array.EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToList();

    /// <summary>Values of <paramref name="elementProperty"/> for every element of the named array property.</summary>
    private static List<int> Ids(JsonElement body, string arrayProperty, string elementProperty) =>
        body.TryGetProperty(arrayProperty, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(row => row.GetProperty(elementProperty).GetInt32()).ToList()
            : new List<int>();

    // ── Patients ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patients_list_shows_my_clinic_patients_only()
    {
        var a = await ClinicA();
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var response = await client.GetAsync("/api/patients");
        response.EnsureSuccessStatusCode();
        var ids = RowIds(await JsonAsync(response));

        Assert.Contains(a.PatientId, ids);
        Assert.DoesNotContain(b.PatientId, ids);
    }

    [Fact]
    public async Task Patients_search_finds_my_patient_and_never_the_other_clinic_patient()
    {
        var a = await ClinicA();
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var mine = await client.GetAsync($"/api/patients?q={Uri.EscapeDataString(a.PatientName)}");
        var theirs = await client.GetAsync($"/api/patients?q={Uri.EscapeDataString(b.PatientName)}");
        var bySharedCode = await client.GetAsync($"/api/patients?q={ClinicFixtures.SharedPatientCode}");

        mine.EnsureSuccessStatusCode();
        theirs.EnsureSuccessStatusCode();
        bySharedCode.EnsureSuccessStatusCode();
        Assert.Equal(new[] { a.PatientId }, RowIds(await JsonAsync(mine)).ToArray());
        Assert.Empty(RowIds(await JsonAsync(theirs)));

        // Both clinics own file number "10001", so this also proves file numbers are never shared.
        Assert.Equal(new[] { a.PatientId }, RowIds(await JsonAsync(bySharedCode)).ToArray());
    }

    [Fact]
    public async Task The_other_clinic_patient_cannot_be_read_edited_or_deleted()
    {
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var read = await client.GetAsync($"/api/patients/{b.PatientId}");
        var update = await client.PutAsJsonAsync($"/api/patients/{b.PatientId}", new
        {
            fullName = "اسم مسروق",
            birthDate = (string?)null,
            gender = (string?)null,
            phone = (string?)null,
            bloodType = (string?)null,
            chronicConditions = (string?)null,
            allergies = (string?)null,
            notes = (string?)null
        });
        var delete = await client.DeleteAsync($"/api/patients/{b.PatientId}");

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var after = await _factory.WithClinicAsync(b.ClinicId, db => db.Patients.FirstAsync(p => p.Id == b.PatientId));
        Assert.Equal(b.PatientName, after.FullName);
        Assert.Equal(ClinicFixtures.SharedPhone, after.Phone);
    }

    // ── Visits ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Visits_of_the_other_clinic_patient_are_not_returned()
    {
        var a = await ClinicA();
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var mine = await client.GetAsync($"/api/visits/by-patient/{a.PatientId}");
        var theirs = await client.GetAsync($"/api/visits/by-patient/{b.PatientId}");

        mine.EnsureSuccessStatusCode();
        theirs.EnsureSuccessStatusCode();
        Assert.Contains(a.VisitId, RowIds(await JsonAsync(mine)));
        Assert.Empty(RowIds(await JsonAsync(theirs)));
    }

    [Fact]
    public async Task The_other_clinic_visit_cannot_be_printed_or_edited()
    {
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var print = await client.GetAsync($"/api/visits/{b.VisitId}/print");
        var update = await client.PutAsJsonAsync($"/api/visits/{b.VisitId}", new
        {
            patientId = b.PatientId,
            diagnosis = "تشخيص مسروق",
            prescription = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.NotFound, print.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

        var after = await _factory.WithClinicAsync(b.ClinicId, db => db.Visits.FirstAsync(v => v.Id == b.VisitId));
        Assert.Equal(b.Diagnosis, after.Diagnosis);
    }

    [Fact]
    public async Task A_visit_cannot_be_recorded_for_the_other_clinic_patient()
    {
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();
        var before = await _factory.WithClinicAsync(b.ClinicId, db => db.Visits.CountAsync(v => v.PatientId == b.PatientId));

        var response = await client.PostAsJsonAsync("/api/visits", new { patientId = b.PatientId, chiefComplaint = "شكوى مسروقة" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var after = await _factory.WithClinicAsync(b.ClinicId, db => db.Visits.CountAsync(v => v.PatientId == b.PatientId));
        Assert.Equal(before, after);
    }

    // ── Appointments ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Todays_appointments_are_mine_only()
    {
        var a = await ClinicA();
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var response = await client.GetAsync("/api/appointments");
        response.EnsureSuccessStatusCode();
        var ids = RowIds(await JsonAsync(response));

        Assert.Contains(a.BookedAppointmentId, ids);
        Assert.Contains(a.WaitingAppointmentId, ids);
        Assert.DoesNotContain(b.BookedAppointmentId, ids);
        Assert.DoesNotContain(b.WaitingAppointmentId, ids);
    }

    [Fact]
    public async Task The_other_clinic_appointments_cannot_be_rescheduled_cancelled_or_linked()
    {
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();
        var bookedBefore = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.FirstAsync(x => x.Id == b.BookedAppointmentId));

        var reschedule = await client.PutAsJsonAsync($"/api/appointments/{b.BookedAppointmentId}", new
        {
            scheduledAt = bookedBefore.ScheduledAt.AddDays(3),
            type = "FollowUp",
            notes = "تعديل مسروق"
        });
        var cancel = await client.PostAsync($"/api/appointments/{b.WaitingAppointmentId}/cancel", null);
        var link = await client.PostAsJsonAsync($"/api/appointments/{b.WaitingAppointmentId}/link-patient", new { patientId = b.PatientId });

        Assert.Equal(HttpStatusCode.NotFound, reschedule.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, link.StatusCode);

        var bookedAfter = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.FirstAsync(x => x.Id == b.BookedAppointmentId));
        Assert.Equal(bookedBefore.ScheduledAt, bookedAfter.ScheduledAt);
        Assert.Equal(bookedBefore.Status, bookedAfter.Status);
        Assert.Equal(bookedBefore.Type, bookedAfter.Type);
        var waitingAfter = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.FirstAsync(x => x.Id == b.WaitingAppointmentId));
        Assert.Equal(AppointmentStatus.Waiting, waitingAfter.Status);
    }

    [Fact]
    public async Task A_slot_taken_by_the_other_clinic_is_still_free_for_me()
    {
        var a = await ClinicA();
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var (date, time) = await FirstFreeSlotAsync(client, date => $"/api/appointments/slots?date={date:yyyy-MM-dd}");
        var at = date.ToDateTime(TimeOnly.Parse(time));
        await ClinicFixtures.BookSlotAsync(_factory, b.ClinicId, b.PatientId, b.PatientName, at);

        var response = await client.GetAsync($"/api/appointments/slots?date={date:yyyy-MM-dd}");
        response.EnsureSuccessStatusCode();
        var slots = (await JsonAsync(response)).GetProperty("slots").EnumerateArray().Select(s => s.GetString()!).ToList();

        Assert.Contains(time, slots);
        Assert.NotEqual(a.ClinicId, b.ClinicId);

        var takenInTheOtherClinic = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.AnyAsync(x => x.ScheduledAt == at));
        Assert.True(takenInTheOtherClinic);   // the slot really is taken there, it just is not mine to see
    }

    // ── Queue ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_queue_snapshot_never_contains_the_other_clinic()
    {
        var a = await ClinicA();
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var response = await client.GetAsync("/api/queue");
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;

        var ids = Ids(body, "waiting", "appointmentId")
            .Concat(Ids(body, "bookedNotArrived", "appointmentId"))
            .Concat(Ids(body, "done", "appointmentId"))
            .ToList();
        if (body.TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.Object)
            ids.Add(current.GetProperty("appointmentId").GetInt32());

        Assert.Contains(a.WaitingAppointmentId, ids);
        Assert.DoesNotContain(b.WaitingAppointmentId, ids);
        Assert.DoesNotContain(b.BookedAppointmentId, ids);
        Assert.Contains(a.PatientFirstName, text);
        Assert.DoesNotContain(b.PatientFirstName, text);
    }

    [Fact]
    public async Task Calling_next_never_calls_the_other_clinic_patient()
    {
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var response = await client.PostAsync("/api/queue/next", null);
        response.EnsureSuccessStatusCode();
        var body = await JsonAsync(response);

        if (body.TryGetProperty("patientName", out var name))
            Assert.NotEqual(b.PatientName, name.GetString());

        var theirWaiting = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.FirstAsync(x => x.Id == b.WaitingAppointmentId));
        Assert.Equal(AppointmentStatus.Waiting, theirWaiting.Status);
    }

    [Theory]
    [InlineData("check-in")]
    [InlineData("start")]
    [InlineData("finish")]
    [InlineData("postpone")]
    [InlineData("no-show")]
    public async Task Queue_actions_on_the_other_clinic_appointment_are_not_found(string action)
    {
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();
        var before = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.FirstAsync(x => x.Id == b.WaitingAppointmentId));

        var response = await client.PostAsync($"/api/queue/{b.WaitingAppointmentId}/{action}", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var after = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.FirstAsync(x => x.Id == b.WaitingAppointmentId));
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.QueueNumber, after.QueueNumber);
        Assert.Equal(before.IsUrgent, after.IsUrgent);
        Assert.Equal(before.StartedAt, after.StartedAt);
    }

    [Fact]
    public async Task Marking_the_other_clinic_appointment_urgent_is_not_found()
    {
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();
        var before = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.FirstAsync(x => x.Id == b.WaitingAppointmentId));

        var response = await client.PostAsJsonAsync($"/api/queue/{b.WaitingAppointmentId}/urgent", new { isUrgent = !before.IsUrgent });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var after = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.FirstAsync(x => x.Id == b.WaitingAppointmentId));
        Assert.Equal(before.IsUrgent, after.IsUrgent);
    }

    [Fact]
    public async Task Walk_in_for_the_other_clinic_patient_is_not_found()
    {
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();
        var before = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.CountAsync(x => x.PatientId == b.PatientId));

        var response = await client.PostAsJsonAsync("/api/queue/walk-in", new { patientId = b.PatientId, type = "Consultation", isUrgent = true, notes = "زيارة مسروقة" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var after = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.CountAsync(x => x.PatientId == b.PatientId));
        Assert.Equal(before, after);
    }

    // ── Invoices ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Invoices_of_the_other_clinic_patient_are_an_empty_list()
    {
        var a = await ClinicA();
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var theirs = await client.GetAsync($"/api/invoices?patientId={b.PatientId}");
        var mine = await client.GetAsync($"/api/invoices?patientId={a.PatientId}");

        theirs.EnsureSuccessStatusCode();
        mine.EnsureSuccessStatusCode();
        var theirBody = await JsonAsync(theirs);
        Assert.Empty(theirBody.GetProperty("items").EnumerateArray());
        Assert.Equal(0m, theirBody.GetProperty("totals").GetProperty("paid").GetDecimal());

        var myBody = await JsonAsync(mine);
        Assert.Contains(a.InvoiceId, myBody.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task The_other_clinic_invoice_cannot_be_paid_or_added_to()
    {
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();
        var before = await _factory.WithClinicAsync(b.ClinicId, db => db.Invoices.FirstAsync(i => i.Id == b.InvoiceId));

        var create = await client.PostAsJsonAsync("/api/invoices", new
        {
            patientId = b.PatientId,
            description = "فاتورة مسروقة",
            amount = 100,
            discount = 0,
            paid = 0,
            method = "Cash"
        });
        var pay = await client.PostAsJsonAsync($"/api/invoices/{b.InvoiceId}/payments", new { amount = 50 });

        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, pay.StatusCode);

        var after = await _factory.WithClinicAsync(b.ClinicId, db => db.Invoices.FirstAsync(i => i.Id == b.InvoiceId));
        Assert.Equal(before.Paid, after.Paid);
    }

    // ── Users ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_users_list_never_shows_the_other_clinic_accounts()
    {
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var response = await client.GetAsync("/api/users");
        response.EnsureSuccessStatusCode();
        var users = await JsonAsync(response);
        var visibleIds = RowIds(users);
        var theirIds = await _factory.WithClinicAsync(b.ClinicId, db => db.Users.Select(u => u.Id).ToListAsync());

        Assert.Empty(visibleIds.Intersect(theirIds));
        // Usernames are unique across the whole installation, so clinic A cannot even create a user named like one
        // of B's accounts; the guarantee here is that B's actual row (its admin's id) never shows up at all.
        var theirAdminId = await _factory.WithClinicAsync(b.ClinicId, async db =>
            (await db.Users.FirstAsync(u => u.Username == TestData.Credentials(TestEnvironment.ClinicBSlug, TestData.AdminRole).Username)).Id);
        Assert.DoesNotContain(users.EnumerateArray(), u =>
            u.GetProperty("username").GetString() == TestData.Credentials(TestEnvironment.ClinicBSlug, TestData.AdminRole).Username
            && u.GetProperty("id").GetInt32() == theirAdminId);
    }

    [Fact]
    public async Task The_other_clinic_accounts_cannot_be_edited_or_reset()
    {
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();
        var theirUsername = TestData.Credentials(TestEnvironment.ClinicBSlug, TestData.AdminRole).Username;
        var theirAdmin = await _factory.WithClinicAsync(b.ClinicId, db => db.Users.FirstAsync(u => u.Username == theirUsername));

        var update = await client.PutAsJsonAsync($"/api/users/{theirAdmin.Id}", new { fullName = "اختراق", role = "Reception", isActive = false });
        var reset = await client.PostAsJsonAsync($"/api/users/{theirAdmin.Id}/reset-password", new { newPassword = "Hacked@2026" });

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);

        var after = await _factory.WithClinicAsync(b.ClinicId, db => db.Users.FirstAsync(u => u.Id == theirAdmin.Id));
        Assert.Equal(theirAdmin.FullName, after.FullName);
        Assert.Equal(theirAdmin.Role, after.Role);
        Assert.Equal(theirAdmin.PasswordHash, after.PasswordHash);
        Assert.True(after.IsActive);
    }

    [Fact]
    public async Task A_username_taken_in_the_other_clinic_is_refused()
    {
        // Usernames are unique across the whole installation: clinic A may not create "admin-b" while clinic B
        // already owns that name — no row is written.
        await ClinicB();
        using var client = await ClinicAAdminAsync();
        var theirUsername = TestData.Credentials(TestEnvironment.ClinicBSlug, TestData.AdminRole).Username;

        var body = new { fullName = "حساب مكرر", username = theirUsername, password = "Strong@2026", role = "Reception" };

        var response = await client.PostAsJsonAsync("/api/users", body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("اسم المستخدم", await response.Content.ReadAsStringAsync());
    }

    // ── Clinic settings ──────────────────────────────────────────────────────

    [Fact]
    public async Task Clinic_settings_are_always_mine()
    {
        await ClinicB();
        using var client = await ClinicAAdminAsync();

        var response = await client.GetAsync("/api/clinic");
        response.EnsureSuccessStatusCode();
        var body = await JsonAsync(response);

        Assert.Equal(TestEnvironment.ClinicASlug, body.GetProperty("slug").GetString());
        Assert.Equal(TestEnvironment.ClinicAName, body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task My_settings_cannot_take_over_the_other_clinic_slug()
    {
        var a = await ClinicA();
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();
        var before = await _factory.WithClinicAsync(b.ClinicId, db => db.Clinics.FirstAsync(c => c.Id == b.ClinicId));

        var response = await client.PutAsJsonAsync("/api/clinic", new
        {
            name = TestEnvironment.ClinicAName,
            slug = TestEnvironment.ClinicBSlug,          // the other clinic's public address
            doctorName = TestEnvironment.ClinicADoctorName,
            specialty = TestEnvironment.ClinicASpecialty,
            about = (string?)null,
            phone = (string?)null,
            address = (string?)null,
            consultationFee = 300m,
            followUpFee = 150m,
            avgMinutesPerPatient = 15,
            workStart = "17:00",
            workEnd = "22:00",
            workDays = "Sat,Sun,Mon,Tue,Wed,Thu",
            onlineBookingEnabled = true,
            dataRetentionDays = 30
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var theirClinic = await _factory.WithClinicAsync(b.ClinicId, db => db.Clinics.FirstAsync(c => c.Id == b.ClinicId));
        var myClinic = await _factory.WithClinicAsync(a.ClinicId, db => db.Clinics.FirstAsync(c => c.Id == a.ClinicId));
        Assert.Equal(before.Slug, theirClinic.Slug);
        Assert.Equal(before.Name, theirClinic.Name);
        Assert.Equal(TestEnvironment.ClinicASlug, myClinic.Slug);   // and nothing changed for me either
    }

    // ── Dashboard ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dashboard_numbers_leave_out_the_other_clinic()
    {
        var a = await ClinicA();
        var b = await ClinicB();
        using var client = await ClinicAAdminAsync();

        var response = await client.GetAsync("/api/dashboard");
        response.EnsureSuccessStatusCode();
        var body = await JsonAsync(response);
        var patients = body.GetProperty("totalPatients").GetInt32();
        var revenue = body.GetProperty("revenueToday").GetDecimal();

        Assert.NotEqual(b.InvoicePaid, revenue);      // clinic B's 777 never reaches clinic A's till
        Assert.Equal(await _factory.WithClinicAsync(a.ClinicId, db => db.Patients.CountAsync()), patients);

        // Counted the slow way, one clinic at a time, so the expectations stay honest however many rows exist.
        var everyClinicId = await _factory.WithClinicAsync(a.ClinicId, db => db.Clinics.Select(c => c.Id).ToListAsync());
        var everyPatient = 0;
        var everyPaidInvoice = 0m;
        foreach (var clinicId in everyClinicId)
        {
            var id = clinicId;
            everyPatient += await _factory.WithClinicAsync(id, db => db.Patients.CountAsync());
            everyPaidInvoice += await _factory.WithClinicAsync(id, db => TodaysPaidAsync(db, id));
        }

        Assert.True(patients < everyPatient);         // clinic B's patients are not counted for me
        Assert.True(revenue < everyPaidInvoice);      // nor is clinic B's money
    }

    private static async Task<decimal> TodaysPaidAsync(ClinicDbContext db, int clinicId)
    {
        var clinic = await db.Clinics.FirstAsync(c => c.Id == clinicId);
        var start = ClinicClock.Today(clinic).ToDateTime(TimeOnly.MinValue);
        return await db.Invoices.Where(i => i.CreatedAt >= start && i.CreatedAt < start.AddDays(1)).SumAsync(i => i.Paid);
    }
}
