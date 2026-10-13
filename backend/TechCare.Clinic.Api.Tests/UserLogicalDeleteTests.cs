using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Tests.Infrastructure;
using Xunit;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN.md 10.6: logical delete of a staff account from the clinic admin's settings. A deleted account
/// disappears from every list, can never sign in again, and its username is freed for a new account, while its
/// row stays in place so visits and audit entries keep their data. An admin may not delete its own account, and
/// the delete joins the audit trail. No seeded fixture account is ever deleted — every test works on accounts
/// it creates itself.
/// </summary>
[Collection(ApiCollection.Name)]
public class UserLogicalDeleteTests : IAsyncLifetime
{
    private readonly ApiFactory _factory;
    // Assigned in InitializeAsync; empty until then so the compiler sees a non-null value.
    private string _adminToken = string.Empty;

    public UserLogicalDeleteTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync() =>
        _adminToken = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole);

    // Everything the tests create stays as deleted/renamed rows that affect no other test.
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A fresh account in clinic A, created through the API like the UI does.</summary>
    private async Task<(int Id, string Username)> CreateUserAsync(HttpClient admin, string role = "Reception")
    {
        var username = "del" + Guid.NewGuid().ToString("N")[..10];
        using var create = await admin.PostAsJsonAsync("/api/users",
            new { FullName = "مستخدم للحذف", Username = username, Password = "DelUser@2026", Role = role });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement;
        return (body.GetProperty("id").GetInt32(), username);
    }

    [Fact]
    public async Task deleted_user_disappears_from_lists_and_cannot_sign_in()
    {
        using var admin = _factory.CreateAuthenticatedClient(_adminToken);
        var (id, username) = await CreateUserAsync(admin);
        const string password = "DelUser@2026";

        // Sanity: before the delete the account signs in fine.
        using (var before = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { username, password }))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using (var del = await admin.DeleteAsync($"/api/users/{id}"))
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // The staff list no longer shows the account.
        using var list = await admin.GetAsync("/api/users");
        list.EnsureSuccessStatusCode();
        var users = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        Assert.DoesNotContain(users.EnumerateArray(), u => u.GetProperty("username").GetString() == username);

        // And the same credentials no longer open a session (the cross-clinic lookup skips the deleted row).
        using var after = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        // The row itself survived — softly deleted, deactivated, token-voiding, renamed — so nothing that
        // points at it (visits, audit entries) breaks.
        var row = await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            var u = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == id);
            return (u.IsDeleted, u.IsActive, Name: u.Username, HadDate: u.DeletedAtUtc != null);
        });
        Assert.True(row.IsDeleted);
        Assert.False(row.IsActive);
        Assert.True(row.HadDate);
        Assert.Equal($"{username}-del-{id}", row.Name);
    }

    [Fact]
    public async Task delete_releases_the_username_for_reuse()
    {
        using var admin = _factory.CreateAuthenticatedClient(_adminToken);
        var (id, username) = await CreateUserAsync(admin);
        using (var del = await admin.DeleteAsync($"/api/users/{id}"))
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // The same globally unique name is accepted again for a new account straight away.
        using var recreate = await admin.PostAsJsonAsync("/api/users",
            new { FullName = "مستخدم بنفس الاسم", Username = username, Password = "DelUser@2027", Role = "Reception" });
        Assert.True(recreate.IsSuccessStatusCode, await recreate.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task admin_cannot_delete_own_account()
    {
        using var admin = _factory.CreateAuthenticatedClient(_adminToken);

        // The caller's own id: the seeded "admin" account of clinic A.
        using var list = await admin.GetAsync("/api/users");
        var users = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        var selfId = users.EnumerateArray().First(u => u.GetProperty("username").GetString() == "admin")
            .GetProperty("id").GetInt32();

        using var del = await admin.DeleteAsync($"/api/users/{selfId}");
        Assert.Equal(HttpStatusCode.BadRequest, del.StatusCode);
        Assert.Contains("حسابك", await del.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task an_admin_account_can_be_deleted_while_another_active_admin_remains()
    {
        using var admin = _factory.CreateAuthenticatedClient(_adminToken);
        var (id, _) = await CreateUserAsync(admin, role: "Admin");

        // The seeded admin of clinic A remains as an active admin, so deleting this one is allowed.
        using var del = await admin.DeleteAsync($"/api/users/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // And the delete joined the audit trail beside the automatic "Modified" row.
        var found = await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            return await db.AuditLogs.IgnoreQueryFilters()
                .AnyAsync(a => a.Action == "Deleted" && a.Entity == "User" && a.EntityId == id.ToString());
        });
        Assert.True(found);
    }

    [Fact]
    public async Task deleting_a_user_of_another_clinic_or_a_missing_user_returns_404()
    {
        var bId = await TestData.EnsureClinicBAsync(_factory);
        using var admin = _factory.CreateAuthenticatedClient(_adminToken);

        // A user that exists but belongs to clinic B is invisible behind the tenant filter.
        var theirId = await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            return await db.Users.IgnoreQueryFilters().Where(u => u.ClinicId == bId).Select(u => u.Id).FirstAsync();
        });
        using (var del = await admin.DeleteAsync($"/api/users/{theirId}"))
            Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);

        using var missing = await admin.DeleteAsync("/api/users/99999999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
