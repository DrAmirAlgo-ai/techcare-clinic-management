using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Patients;
using TechCare.Clinic.Api.Infrastructure;
using TechCare.Clinic.Api.Tests.Infrastructure;
using static TechCare.Clinic.Api.Tests.Infrastructure.TestHttp;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_ADDENDUM.md 9.3 — the patient data rights: the complete file export, the one-way identity
/// anonymisation, and the logical deletion behind the clinic's own retention period, every one of them
/// joining the audit trail. The files that outlive their retention go physically through
/// <see cref="PatientRetentionService"/>.
/// </summary>
[Collection(ApiCollection.Name)]
public class PatientDataRightsTests
{
    private readonly ApiFactory _factory;

    public PatientDataRightsTests(ApiFactory factory) => _factory = factory;

    private Task<ClinicFixture> ClinicA() => ClinicFixtures.ClinicAAsync(_factory);

    private async Task<HttpClient> ClinicAAdminAsync()
    {
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole);
        return _factory.CreateAuthenticatedClient(token);
    }

    private async Task<HttpClient> ClinicBAdminAsync()
    {
        await TestData.EnsureClinicBAsync(_factory);
        var token = await _factory.TokenAsync(TestEnvironment.ClinicBSlug, TestData.AdminRole);
        return _factory.CreateAuthenticatedClient(token);
    }

    /// <summary>A file of its own, created through the real endpoint so the code, audit and filter all run.</summary>
    private async Task<int> CreatePatientAsync(HttpClient admin, string fullName, string phone)
    {
        using var response = await admin.PostAsJsonAsync("/api/patients", new { fullName, phone });
        response.EnsureSuccessStatusCode();
        return (await JsonAsync(response)).GetProperty("id").GetInt32();
    }

    /// <summary>A recorded consultation under a file, so a test can prove what survives deletion.</summary>
    private Task AddVisitAsync(int clinicId, int patientId, string? diagnosis = null) =>
        _factory.WithClinicAsync(clinicId, async db =>
        {
            var doctor = await db.Users.FirstAsync(u => u.Role == UserRole.Doctor);
            var clinic = await db.Clinics.FirstAsync(c => c.Id == db.CurrentClinicId);
            db.Visits.Add(new Visit
            {
                PatientId = patientId,
                DoctorId = doctor.Id,
                VisitDate = ClinicClock.Now(clinic),
                Diagnosis = diagnosis
            });
            await db.SaveChangesAsync();
            return 0;
        });

    // ── The export ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_returns_the_whole_file_and_joins_the_audit_trail()
    {
        var a = await ClinicA();
        using var admin = await ClinicAAdminAsync();

        using var response = await admin.GetAsync($"/api/patients/{a.PatientId}/export");
        response.EnsureSuccessStatusCode();
        var body = await JsonAsync(response);

        Assert.Equal(a.PatientName, body.GetProperty("patient").GetProperty("fullName").GetString());
        var visits = body.GetProperty("visits");
        Assert.Equal(a.Diagnosis, visits[0].GetProperty("diagnosis").GetString());
        Assert.Equal(1, visits[0].GetProperty("prescription").GetArrayLength());
        Assert.Equal("كشف", body.GetProperty("invoices")[0].GetProperty("description").GetString());
        // Clinic A's fixture owns a booked appointment plus a waiting one: both ride along in the file.
        Assert.True(body.GetProperty("appointments").GetArrayLength() >= 2);

        // The event sits in the trail, stamped for exactly this file by this admin's account.
        var audited = await _factory.WithClinicAsync(a.ClinicId, db =>
            db.AuditLogs.AnyAsync(l => l.Action == "Exported" && l.Entity == "Patient" && l.EntityId == a.PatientId.ToString()));
        Assert.True(audited);
    }

    [Fact]
    public async Task Export_stays_inside_one_clinic_and_is_admin_only()
    {
        var a = await ClinicA();
        await TestData.EnsureClinicBAsync(_factory);

        // Another clinic reaching the same id by number must see "no such patient", not a foreign file.
        using var otherAdmin = await ClinicBAdminAsync();
        using var cross = await otherAdmin.GetAsync($"/api/patients/{a.PatientId}/export");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        // Inside the clinic the export is the admin's door, not the doctor's.
        var doctorToken = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var doctorClient = _factory.CreateAuthenticatedClient(doctorToken);
        using var denied = await doctorClient.GetAsync($"/api/patients/{a.PatientId}/export");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    // ── The anonymisation ─────────────────────────────────────────────────────

    [Fact]
    public async Task Anonymize_hides_the_identity_and_is_one_way()
    {
        using var admin = await ClinicAAdminAsync();

        using var create = await admin.PostAsJsonAsync("/api/patients", new
        {
            fullName = "صاحب هوية",
            phone = "01234567890",
            birthDate = "1990-01-15",
            gender = "ذكر"
        });
        create.EnsureSuccessStatusCode();
        var patientId = (await JsonAsync(create)).GetProperty("id").GetInt32();

        using var anonymized = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/patients/{patientId}/anonymize"));
        Assert.Equal(HttpStatusCode.OK, anonymized.StatusCode);
        var result = await JsonAsync(anonymized);
        Assert.Equal("مريض مجهول", result.GetProperty("fullName").GetString());
        Assert.True(result.GetProperty("isAnonymized").GetBoolean());

        // The file still opens — for its medical history — but the identity fields are gone from it.
        using var file = await admin.GetAsync($"/api/patients/{patientId}");
        file.EnsureSuccessStatusCode();
        var body = await JsonAsync(file);
        Assert.Equal("مريض مجهول", body.GetProperty("fullName").GetString());
        Assert.True(body.GetProperty("isAnonymized").GetBoolean());
        // The API omits nulls, so the stripped fields are simply absent.
        Assert.False(body.TryGetProperty("phone", out _));
        Assert.False(body.TryGetProperty("birthDate", out _));
        Assert.False(body.TryGetProperty("gender", out _));

        // One-way: a second call is refused.
        using var again = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/patients/{patientId}/anonymize"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var audited = await _factory.WithClinicAsync((await ClinicA()).ClinicId, db =>
            db.AuditLogs.AnyAsync(l => l.Action == "Anonymized" && l.Entity == "Patient" && l.EntityId == patientId.ToString()));
        Assert.True(audited);
    }

    // ── The logical delete and the retention window ──────────────────────────

    [Fact]
    public async Task Delete_is_logical_and_the_file_goes_away_from_every_door()
    {
        var a = await ClinicA();
        using var admin = await ClinicAAdminAsync();

        var patientId = await CreatePatientAsync(admin, "حذف منطقي", "01250000001");
        await AddVisitAsync(a.ClinicId, patientId, "تشخيص ملف الحقوق");

        using var del = await admin.DeleteAsync($"/api/patients/{patientId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // From every door the UI has, the file looks gone.
        using var file = await admin.GetAsync($"/api/patients/{patientId}");
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
        using var export = await admin.GetAsync($"/api/patients/{patientId}/export");
        Assert.Equal(HttpStatusCode.NotFound, export.StatusCode);
        using var search = await admin.GetAsync("/api/patients?q=" + Uri.EscapeDataString("حذف منطقي"));
        search.EnsureSuccessStatusCode();
        Assert.Equal(0, (await JsonAsync(search)).GetArrayLength());

        // A second delete is just another "no such patient" — the row is already behind the filter.
        using var delAgain = await admin.DeleteAsync($"/api/patients/{patientId}");
        Assert.Equal(HttpStatusCode.NotFound, delAgain.StatusCode);

        // ...yet the file and its history are intact on disk, stamped as deleted.
        var (row, historyStillThere) = await _factory.WithServicesAsync(services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            return Task.FromResult((
                db.Patients.IgnoreQueryFilters().FirstOrDefault(p => p.Id == patientId),
                db.Visits.IgnoreQueryFilters().Any(v => v.PatientId == patientId)));
        });
        Assert.NotNull(row);
        Assert.True(row!.IsDeleted);
        Assert.NotNull(row.DeletedAtUtc);
        Assert.True(historyStillThere);

        var audited = await _factory.WithClinicAsync(a.ClinicId, db =>
            db.AuditLogs.AnyAsync(l => l.Action == "SoftDeleted" && l.Entity == "Patient" && l.EntityId == patientId.ToString()));
        Assert.True(audited);
    }

    [Fact]
    public async Task Purge_removes_only_files_past_their_clinics_retention()
    {
        var a = await ClinicA();
        using var admin = await ClinicAAdminAsync();

        var longAgo = await CreatePatientAsync(admin, "حذف قديم", "01260000001");
        var today = await CreatePatientAsync(admin, "حذف اليوم", "01260000002");
        await AddVisitAsync(a.ClinicId, longAgo);

        using (var delOld = await admin.DeleteAsync($"/api/patients/{longAgo}"))
            Assert.Equal(HttpStatusCode.NoContent, delOld.StatusCode);
        using (var delNew = await admin.DeleteAsync($"/api/patients/{today}"))
            Assert.Equal(HttpStatusCode.NoContent, delNew.StatusCode);

        // The old file is stamped two days back, and this clinic keeps deleted files for a day only.
        await _factory.WithClinicAsync(a.ClinicId, async db =>
        {
            var clinic = await db.Clinics.FirstAsync(c => c.Id == db.CurrentClinicId);
            clinic.DataRetentionDays = 1;
            db.Patients.IgnoreQueryFilters().First(p => p.Id == longAgo).DeletedAtUtc = DateTime.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
            return 0;
        });

        var purged = await _factory.WithServicesAsync(services =>
            services.GetRequiredService<PatientRetentionService>().PurgeExpiredAsync());
        Assert.Equal(1, purged);

        var state = await _factory.WithServicesAsync(services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            return Task.FromResult((
                oldGone: db.Patients.IgnoreQueryFilters().Any(p => p.Id == longAgo),
                oldHistoryGone: db.Visits.IgnoreQueryFilters().Any(v => v.PatientId == longAgo),
                youngStillLogical: db.Patients.IgnoreQueryFilters().FirstOrDefault(p => p.Id == today)!.IsDeleted));
        });
        Assert.False(state.oldGone);
        Assert.False(state.oldHistoryGone);
        Assert.True(state.youngStillLogical);

        // The removal event is in the trail under the system's name — no user account performs this step.
        var audited = await _factory.WithClinicAsync(a.ClinicId, db =>
            db.AuditLogs.AnyAsync(l => l.Action == "Purged" && l.EntityId == longAgo.ToString() && l.UserName == "النظام"));
        Assert.True(audited);

        // The default window is back, so no other test sees a one-day clinic.
        await _factory.WithClinicAsync(a.ClinicId, async db =>
        {
            (await db.Clinics.FirstAsync(c => c.Id == db.CurrentClinicId)).DataRetentionDays = 30;
            await db.SaveChangesAsync();
            return 0;
        });
    }

    [Fact]
    public async Task The_clinic_controls_its_own_retention_window()
    {
        using var admin = await ClinicAAdminAsync();

        using (var get = await admin.GetAsync("/api/clinic"))
        {
            get.EnsureSuccessStatusCode();
            Assert.Equal(30, (await JsonAsync(get)).GetProperty("dataRetentionDays").GetInt32());
        }

        // Zero days would remove the files the moment they are marked deleted: refused.
        using var rejected = await admin.PutAsJsonAsync("/api/clinic", await ClinicSettingsBodyAsync(admin, 0));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // Ten years is the outer edge: accepted.
        using var accepted = await admin.PutAsJsonAsync("/api/clinic", await ClinicSettingsBodyAsync(admin, 3650));
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);

        // ...and the default is back, so the suite keeps running against a sane window.
        using var restored = await admin.PutAsJsonAsync("/api/clinic", await ClinicSettingsBodyAsync(admin, 30));
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
    }

    /// <summary>The current clinic settings with only the retention window changed — the update endpoint takes the whole form.</summary>
    private static async Task<object> ClinicSettingsBodyAsync(HttpClient admin, int dataRetentionDays)
    {
        using var get = await admin.GetAsync("/api/clinic");
        get.EnsureSuccessStatusCode();
        var b = await JsonAsync(get);
        return new
        {
            name = b.GetProperty("name").GetString()!,
            slug = b.GetProperty("slug").GetString()!,
            doctorName = b.GetProperty("doctorName").GetString()!,
            specialty = b.GetProperty("specialty").GetString()!,
            about = OptString(b, "about"),
            phone = OptString(b, "phone"),
            address = OptString(b, "address"),
            consultationFee = b.GetProperty("consultationFee").GetDecimal(),
            followUpFee = b.GetProperty("followUpFee").GetDecimal(),
            avgMinutesPerPatient = b.GetProperty("avgMinutesPerPatient").GetInt32(),
            workStart = b.GetProperty("workStart").GetString()!,
            workEnd = b.GetProperty("workEnd").GetString()!,
            workDays = b.GetProperty("workDays").GetString()!,
            onlineBookingEnabled = b.GetProperty("onlineBookingEnabled").GetBoolean(),
            dataRetentionDays
        };
    }

    /// <summary>The API omits null properties, so a missing settings value reads as null.</summary>
    private static string? OptString(JsonElement b, string name) =>
        b.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
}

