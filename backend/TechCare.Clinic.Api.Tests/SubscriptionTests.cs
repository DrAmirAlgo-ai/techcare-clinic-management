using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN.md 10.2–10.5 — the subscription: a registered clinic starts on Basic in the free trial; the owner tunes
/// the plan, the state and the trial length, and books payments by hand; a suspended or expired clinic becomes
/// read-only while keeping its reads; and a Basic clinic (or a clinic with the AI switch off) is locked out of
/// the assistant. The full role-denial side of the two new owner endpoints is the role matrix's job.
/// </summary>
[Collection(ApiCollection.Name)]
public class SubscriptionTests
{
    /// <summary>Usernames and slugs this file creates must not collide with the seeder's or the matrix's.</summary>
    private static int _sequence = 81000;

    private readonly ApiFactory _factory;

    public SubscriptionTests(ApiFactory factory) => _factory = factory;

    /// <summary>Registers a clinic through the owner panel and answers its id, slug and admin username.</summary>
    private async Task<(int ClinicId, string Slug, string AdminUsername)> RegisterClinicAsync(string tag, int? trialDays = null)
    {
        var slug = $"subc{Interlocked.Increment(ref _sequence)}";
        var username = $"subad{Interlocked.Increment(ref _sequence)}";

        using var owner = await OwnerClientAsync();
        using var response = await owner.PostAsJsonAsync("/api/owner/clinics", new
        {
            name = $"عيادة اشتراك {tag}",
            slug,
            doctorName = "د. الاشتراك",
            specialty = "عام",
            adminUsername = username,
            adminPassword = "SubTest@2026",
            trialDays
        });
        Assert.True(response.IsSuccessStatusCode, $"registering the clinic answered {(int)response.StatusCode}");

        var id = await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            var clinic = await db.Clinics.SingleAsync(c => c.Slug == slug);
            return clinic.Id;
        });
        return (id, slug, username);
    }

    /// <summary>Writes the subscription row directly (no endpoint) and clears the guard's 30 second cache.</summary>
    private async Task SetSubscriptionAsync(int clinicId, ClinicPlan plan, SubscriptionStatus status,
        DateTime? trialEnds, DateTime? ends)
    {
        await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            var clinic = await db.Clinics.FirstAsync(c => c.Id == clinicId);
            clinic.Plan = plan;
            clinic.SubscriptionStatus = status;
            clinic.TrialEndsAtUtc = trialEnds;
            clinic.SubscriptionEndsAtUtc = ends;
            await db.SaveChangesAsync();
            return true;
        });
        SubscriptionGuard.ClearCache();
    }

    private async Task<HttpClient> OwnerClientAsync()
    {
        var token = await _factory.TokenAsync(TestEnvironment.OwnerSlug, TestData.SuperAdminRole);
        return _factory.CreateAuthenticatedClient(token);
    }

    /// <summary>
    /// A registered clinic has no entry in <see cref="TestData"/>, so its staff token comes from logging in
    /// with the generated admin username instead of the seeded-credentials table.
    /// </summary>
    private async Task<HttpClient> StaffClientAsync(string username)
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "SubTest@2026" });
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login for {username} answered {(int)response.StatusCode}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()
            ?? throw new InvalidOperationException($"Login for {username} returned no token.");
        return _factory.CreateAuthenticatedClient(token);
    }

    [Fact]
    public async Task A_registered_clinic_starts_on_basic_in_the_default_trial()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        var (id, _, _) = await RegisterClinicAsync("افتراضي");

        var clinic = await _factory.WithServicesAsync(services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            return db.Clinics.SingleAsync(c => c.Id == id);
        });

        Assert.Equal(ClinicPlan.Basic, clinic.Plan);
        Assert.Equal(SubscriptionStatus.Trial, clinic.SubscriptionStatus);
        Assert.NotNull(clinic.TrialEndsAtUtc);
        // The installation default is 14 days: the end date must sit just past now+13 and just before now+15.
        Assert.True(clinic.TrialEndsAtUtc > DateTime.UtcNow.AddDays(13));
        Assert.True(clinic.TrialEndsAtUtc < DateTime.UtcNow.AddDays(15));
        Assert.True(clinic.AiEnabled);
    }

    [Fact]
    public async Task A_registered_clinic_respects_its_own_trial_length_and_shows_it_to_its_staff()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        var (_, slug, username) = await RegisterClinicAsync("مخصص", trialDays: 5);

        using var client = _factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "SubTest@2026" });
        Assert.True(login.IsSuccessStatusCode, $"login answered {(int)login.StatusCode}");
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var clinic = body.GetProperty("clinic");
        Assert.Equal("Basic", clinic.GetProperty("plan").GetString());
        Assert.Equal("Trial", clinic.GetProperty("subscriptionStatus").GetString());
        Assert.Equal(5, clinic.GetProperty("daysRemaining").GetInt32());
        Assert.True(clinic.GetProperty("aiEnabled").GetBoolean());

        // The clinic's own settings endpoint carries the same numbers (PLAN.md 10.2).
        using var admin = await StaffClientAsync(username);
        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/clinic");
        Assert.Equal("Basic", settings.GetProperty("plan").GetString());
        Assert.Equal("Trial", settings.GetProperty("subscriptionStatus").GetString());
        Assert.Equal(5, settings.GetProperty("trialDays").GetInt32());
    }

    [Fact]
    public async Task The_owner_can_change_the_plan_and_renew_the_clinic()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        var (id, _, _) = await RegisterClinicAsync("تجديد");

        using var owner = await OwnerClientAsync();
        using var put = await owner.PutAsJsonAsync($"/api/owner/clinics/{id}/subscription",
            new { plan = "Pro", status = "Active" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using var extended = await owner.PutAsJsonAsync($"/api/owner/clinics/{id}/subscription", new { extendDays = 30 });
        Assert.Equal(HttpStatusCode.OK, extended.StatusCode);

        // Bad answers stay bad: an unknown plan or an unreasonable extension is refused before anything is written.
        using var badPlan = await owner.PutAsJsonAsync($"/api/owner/clinics/{id}/subscription", new { plan = "Gold" });
        Assert.Equal(HttpStatusCode.BadRequest, badPlan.StatusCode);
        using var badExtend = await owner.PutAsJsonAsync($"/api/owner/clinics/{id}/subscription", new { extendDays = 99999 });
        Assert.Equal(HttpStatusCode.BadRequest, badExtend.StatusCode);

        var clinic = await _factory.WithServicesAsync(services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            return db.Clinics.SingleAsync(c => c.Id == id);
        });
        Assert.Equal(ClinicPlan.Pro, clinic.Plan);
        Assert.Equal(SubscriptionStatus.Active, clinic.SubscriptionStatus);
        Assert.Null(clinic.TrialEndsAtUtc);
        // Active started one year out and was extended thirty days: the end sits 395 days ahead.
        Assert.NotNull(clinic.SubscriptionEndsAtUtc);
        Assert.InRange((clinic.SubscriptionEndsAtUtc.Value - DateTime.UtcNow).TotalDays, 394, 396);
    }

    [Fact]
    public async Task The_owner_books_a_payment_and_sees_it_in_the_detail_and_the_trail()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        var (id, _, _) = await RegisterClinicAsync("دفعات");

        using var owner = await OwnerClientAsync();
        // A payment dated in the last month must not count into this month's total.
        var lastMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-1);
        using var oldPayment = await owner.PostAsJsonAsync($"/api/owner/clinics/{id}/payments",
            new { amount = 111m, paidAtUtc = lastMonth, note = "دفعة الشهر الماضي" });
        Assert.Equal(HttpStatusCode.Created, oldPayment.StatusCode);
        using var current = await owner.PostAsJsonAsync($"/api/owner/clinics/{id}/payments",
            new { amount = 250.5m, paidAtUtc = DateTime.UtcNow, note = "دفعة التجديد" });
        Assert.Equal(HttpStatusCode.Created, current.StatusCode);
        using var weak = await owner.PostAsJsonAsync($"/api/owner/clinics/{id}/payments",
            new { amount = 0m, paidAtUtc = DateTime.UtcNow });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        // The detail lists the clinic's payments, newest first.
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/owner/clinics/{id}");
        var payments = detail.GetProperty("payments").EnumerateArray().ToList();
        Assert.Equal(2, payments.Count);
        Assert.Equal("دفعة التجديد", payments[0].GetProperty("note").GetString());

        // This month's total carries the current payment, not last month's. The role matrix may have booked its
        // own 500 against the fixture clinic in the same month, so the bound leaves it room.
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/owner/clinics");
        Assert.InRange(list.GetProperty("monthRevenue").GetDecimal(), 250.5m, 250.5m + 500m);

        // The payment row joins the event trail (PLAN_ADDENDUM.md 7.4) with the clinic stamped on it.
        var audited = await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            return await db.AuditLogs.IgnoreQueryFilters()
                .AnyAsync(a => a.Entity == "SubscriptionPayment" && a.ClinicId == id && a.Action == "Added");
        });
        Assert.True(audited, "the booked payment left no row in the event trail.");
    }

    [Fact]
    public async Task The_expiring_filter_lists_only_the_clinics_ending_soon()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        var (nearId, _, _) = await RegisterClinicAsync("قريب", trialDays: 3);
        var (farId, _, _) = await RegisterClinicAsync("بعيد", trialDays: 30);

        using var owner = await OwnerClientAsync();
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/owner/clinics?expiringDays=7");
        var ids = list.GetProperty("items").EnumerateArray()
            .Select(c => c.GetProperty("id").GetInt32()).ToList();

        Assert.Contains(nearId, ids);
        Assert.DoesNotContain(farId, ids);

        // Every row now carries the subscription shape the panel renders.
        var near = list.GetProperty("items").EnumerateArray().First(c => c.GetProperty("id").GetInt32() == nearId);
        Assert.Equal("Trial", near.GetProperty("subscriptionStatus").GetString());
        Assert.True(near.TryGetProperty("endsAtUtc", out var ends) && ends.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task A_suspended_or_lapsed_clinic_is_read_only_until_reactivated()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        using var admin = _factory.CreateAuthenticatedClient(
            await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole));

        try
        {
            // Suspended: writes are refused with the Arabic explanation, reads still pass.
            await SetSubscriptionAsync(a.ClinicId, ClinicPlan.Pro, SubscriptionStatus.Suspended, null, null);
            using var refusedWrite = await admin.PostAsJsonAsync("/api/patients",
                new { fullName = "مريض على عيادة موقوفة", phone = "01100000101" });
            Assert.Equal(HttpStatusCode.Forbidden, refusedWrite.StatusCode);
            Assert.Contains("القراءة فقط", await refusedWrite.Content.ReadAsStringAsync());

            using var stillReadable = await admin.GetAsync("/api/patients");
            Assert.Equal(HttpStatusCode.OK, stillReadable.StatusCode);

            // A trial that ran out of days locks the same way.
            await SetSubscriptionAsync(a.ClinicId, ClinicPlan.Basic, SubscriptionStatus.Trial,
                DateTime.UtcNow.AddDays(-1), null);
            using var lapsedWrite = await admin.PostAsJsonAsync("/api/patients",
                new { fullName = "مريض على عيادة منتهية", phone = "01100000102" });
            Assert.Equal(HttpStatusCode.Forbidden, lapsedWrite.StatusCode);
        }
        finally
        {
            // Clinic A is shared with the rest of the suite: hand it back to full service no matter what.
            await SetSubscriptionAsync(a.ClinicId, ClinicPlan.Pro, SubscriptionStatus.Active, null, null);
        }
    }

    [Fact]
    public async Task A_basic_clinic_is_locked_out_of_the_assistant_until_promoted()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        var (id, slug, username) = await RegisterClinicAsync("بسيط", trialDays: 30);
        using var admin = await StaffClientAsync(username);

        // The lock answers before a single token is spent — no patient, no library, no model call.
        using var ask = await admin.PostAsJsonAsync("/api/ai/ask", new
        {
            question = "متى يُعطى إسيتامينوفين لطفل عمره سنة؟",
            patientId = (int?)null
        });
        Assert.Equal(HttpStatusCode.Forbidden, ask.StatusCode);
        // The body is JSON, so the Arabic message arrives unicode-escaped: read it through the parser, not as raw text.
        var lockedBody = await ask.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("الاحترافية", lockedBody.GetProperty("message").GetString());

        // The owner promotes the clinic to Pro: the same request gets through the plan gate (whether the model
        // itself answers is the assistant's own concern, not the gate's).
        using var owner = await OwnerClientAsync();
        using var promoted = await owner.PutAsJsonAsync($"/api/owner/clinics/{id}/subscription", new { plan = "Pro" });
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);

        using var askAfter = await admin.PostAsJsonAsync("/api/ai/ask", new
        {
            question = "متى يُعطى إسيتامينوفين لطفل عمره سنة؟",
            patientId = (int?)null
        });
        Assert.NotEqual(HttpStatusCode.Forbidden, askAfter.StatusCode);
    }
}
