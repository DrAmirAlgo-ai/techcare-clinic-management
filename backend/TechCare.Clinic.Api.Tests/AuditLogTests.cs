using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_ADDENDUM.md 7.4 — the audit trail: opening a patient writes one row (who, what, when), the clinic admin
/// reads only their own clinic's rows, and the owner reads all of them.
/// </summary>
[Collection(ApiCollection.Name)]
public class AuditLogTests
{
    private readonly ApiFactory _factory;

    public AuditLogTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Opening_a_patient_writes_one_audit_row_with_who_what_and_when()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync($"/api/patients/{a.PatientId}");
        response.EnsureSuccessStatusCode();

        var written = await _factory.WithClinicAsync(a.ClinicId, db =>
            db.AuditLogs.AsNoTracking().AnyAsync(x =>
                x.Entity == nameof(Patient)
                && x.EntityId == a.PatientId.ToString()
                && x.Action == "Viewed"
                && x.ClinicId == a.ClinicId
                && x.UserId != null
                && x.UserName != null));

        Assert.True(written, "opening a patient must leave one audit row naming the account, the clinic and the entity.");
    }

    [Fact]
    public async Task A_clinic_admin_reads_only_own_clinic_and_the_owner_reads_everything()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var b = await ClinicFixtures.ClinicBAsync(_factory);

        var adminA = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole);
        using (var clientA = _factory.CreateAuthenticatedClient(adminA))
        {
            var resA = await clientA.GetAsync("/api/audit-logs?take=1000");
            resA.EnsureSuccessStatusCode();
            var itemsA = (await resA.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray();

            // The fixture created a patient inside clinic A: it must appear in A's trail.
            Assert.Contains(itemsA, e =>
                e.GetProperty("entity").GetString() == nameof(Patient)
                && e.GetProperty("entityId").GetString() == a.PatientId.ToString());

            // Clinic B's invoice belongs to B: it must never leak into A's trail. The entity type joins the check,
            // because entity ids count from zero per clinic and A's own patients share numbers with B's invoices.
            Assert.DoesNotContain(itemsA, e =>
                e.GetProperty("entity").GetString() == nameof(Invoice)
                && e.GetProperty("entityId").GetString() == b.InvoiceId.ToString());
        }

        var owner = await _factory.TokenAsync(TestEnvironment.OwnerSlug, TestData.SuperAdminRole);
        using var clientO = _factory.CreateAuthenticatedClient(owner);
        var resO = await clientO.GetAsync("/api/owner/audit-logs?take=1000");
        resO.EnsureSuccessStatusCode();
        var itemsO = (await resO.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray();

        Assert.Contains(itemsO, e =>
            e.GetProperty("entity").GetString() == nameof(Patient)
            && e.GetProperty("entityId").GetString() == a.PatientId.ToString());
        Assert.Contains(itemsO, e =>
            e.GetProperty("entity").GetString() == nameof(Invoice)
            && e.GetProperty("entityId").GetString() == b.InvoiceId.ToString());
    }

    [Fact]
    public async Task The_user_filter_narrows_the_trail_to_one_account()
    {
        // The trail is an admin door (PLAN_ADDENDUM.md 7.4), so the filter is measured with the clinic's admin.
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole);

        int adminId;
        using (var meClient = _factory.CreateAuthenticatedClient(token))
        {
            var me = await meClient.GetAsync("/api/auth/me");
            me.EnsureSuccessStatusCode();
            adminId = (await me.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("user").GetProperty("id").GetInt32();
        }

        using var client = _factory.CreateAuthenticatedClient(token);
        var opened = await client.GetAsync($"/api/patients/{a.PatientId}");
        opened.EnsureSuccessStatusCode();

        var res = await client.GetAsync($"/api/audit-logs?userId={adminId}&take=1000");
        res.EnsureSuccessStatusCode();
        var items = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();

        Assert.NotEmpty(items);
        Assert.All(items, e => Assert.Equal(adminId, e.GetProperty("userId").GetInt32()));
        Assert.Contains(items, e =>
            e.GetProperty("action").GetString() == "Viewed"
            && e.GetProperty("entity").GetString() == nameof(Patient)
            && e.GetProperty("entityId").GetString() == a.PatientId.ToString());
    }
}