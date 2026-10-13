using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Infrastructure;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN.md 1.4 — per-account permission sets. The role matrix (PLAN_ADDENDUM.md 0.3) still proves the role
/// defaults; these tests prove the layer on top of them: the admin configuring a set from the settings, the set
/// taking effect without a new token, the self-protection and cross-clinic rules, and the login response carrying
/// the set the UI gates its pages on.
/// </summary>
[Collection(ApiCollection.Name)]
public class UserPermissionsTests : IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly string _username;
    private int _userId;
    private HttpClient _userClient;

    public UserPermissionsTests(ApiFactory factory)
    {
        _factory = factory;
        _username = $"permtest{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Creates a reception account through the real admin endpoint and signs it in once. Reception is the clean
    /// probe: its defaults cover the day-to-day pages but never the assistant.
    /// </summary>
    public async Task InitializeAsync()
    {
        UserSessionValidator.ClearCache();
        using var admin = await AdminClientAsync();
        using var created = await admin.PostAsJsonAsync("/api/users", new
        {
            fullName = "مستخدم اختبار الصلاحيات",
            username = _username,
            password = "PermTest@2026",
            role = "Reception"
        });
        Assert.True(created.StatusCode == HttpStatusCode.OK,
            $"user creation failed: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");
        _userId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        using var loginClient = _factory.CreateClient();
        using var login = await loginClient.PostAsJsonAsync("/api/auth/login",
            new { username = _username, password = "PermTest@2026" });
        Assert.True(login.IsSuccessStatusCode, $"user login failed: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        _userClient = _factory.CreateAuthenticatedClient(token);
    }

    /// <summary>Removes the throwaway account and forgets its cached state so no other test sees either.</summary>
    public async Task DisposeAsync()
    {
        _userClient?.Dispose();
        await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == _userId);
            if (user != null) db.Users.Remove(user);
            await db.SaveChangesAsync();
            return 0;
        });
        UserSessionValidator.ClearCache();
    }

    [Fact]
    public async Task The_login_response_carries_the_role_default_permissions()
    {
        var admin = await LoginBodyAsync(_factory, TestEnvironment.ClinicASlug, TestData.AdminRole);
        Assert.Equal(
            ["dashboard", "queue", "appointments", "patients", "invoices", "assistant", "admin"],
            PermissionsOf(admin));

        var reception = await LoginBodyAsync(_factory, TestEnvironment.ClinicASlug, TestData.ReceptionRole);
        var receptionKeys = PermissionsOf(reception);
        Assert.Equal(5, receptionKeys.Length);
        Assert.DoesNotContain("assistant", receptionKeys);
        Assert.DoesNotContain("admin", receptionKeys);
    }

    [Fact]
    public async Task An_admin_reads_a_users_permission_set()
    {
        using var admin = await AdminClientAsync();
        using var response = await admin.GetAsync($"/api/users/{_userId}/permissions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isExplicit").GetBoolean());
        Assert.Equal(5, body.GetProperty("effective").GetArrayLength());
        Assert.Equal(7, body.GetProperty("all").GetArrayLength());
        Assert.Equal(5, body.GetProperty("defaults").GetArrayLength());
    }

    [Fact]
    public async Task Permission_sets_are_managed_only_by_the_admin_permission()
    {
        foreach (var role in new[] { TestData.DoctorRole, TestData.ReceptionRole })
        {
            using var client = _factory.CreateAuthenticatedClient(await _factory.TokenAsync(TestEnvironment.ClinicASlug, role));
            using var get = await client.GetAsync($"/api/users/{_userId}/permissions");
            Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
            using var put = await client.PutAsJsonAsync($"/api/users/{_userId}/permissions",
                new { permissions = new[] { "queue" } });
            Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        }
    }

    [Fact]
    public async Task A_permission_set_takes_effect_without_a_new_token()
    {
        using var admin = await AdminClientAsync();

        // Grant the assistant: the very same token may now reach the AI, and loses the queue the role always had.
        using (var granted = await admin.PutAsJsonAsync($"/api/users/{_userId}/permissions",
                   new { permissions = new[] { "assistant" } }))
        {
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        }
        UserSessionValidator.ClearCache();
        using var assistant = await _userClient.GetAsync("/api/ai/status");
        Assert.Equal(HttpStatusCode.OK, assistant.StatusCode);
        using var queue = await _userClient.GetAsync("/api/queue");
        Assert.Equal(HttpStatusCode.Forbidden, queue.StatusCode);

        // An explicit empty set: even the role's own defaults close.
        using (var none = await admin.PutAsJsonAsync($"/api/users/{_userId}/permissions",
                   new { permissions = Array.Empty<string>() }))
        {
            Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        }
        UserSessionValidator.ClearCache();
        using var closed = await _userClient.GetAsync("/api/queue");
        Assert.Equal(HttpStatusCode.Forbidden, closed.StatusCode);

        // Back to the role's defaults: the queue opens again, the assistant stays shut.
        using (var reset = await admin.PutAsJsonAsync($"/api/users/{_userId}/permissions",
                   new { permissions = (string[])null }))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }
        UserSessionValidator.ClearCache();
        using var back = await _userClient.GetAsync("/api/queue");
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        using var stillNoAssistant = await _userClient.GetAsync("/api/ai/status");
        Assert.Equal(HttpStatusCode.Forbidden, stillNoAssistant.StatusCode);
    }

    [Fact]
    public async Task Unknown_permission_keys_are_refused()
    {
        using var admin = await AdminClientAsync();
        using var response = await admin.PutAsJsonAsync($"/api/users/{_userId}/permissions",
            new { permissions = new[] { "queue", "open-library" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_strip_its_own_admin_permission()
    {
        using var admin = await AdminClientAsync();
        using var list = await admin.GetAsync("/api/users");
        var users = await list.Content.ReadFromJsonAsync<JsonElement>();
        var adminId = users.EnumerateArray()
            .First(u => u.GetProperty("username").GetString() == "admin")
            .GetProperty("id").GetInt32();

        using var response = await admin.PutAsJsonAsync($"/api/users/{adminId}/permissions",
            new { permissions = new[] { "queue" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Changing_the_role_resets_the_explicit_permission_set()
    {
        using var admin = await AdminClientAsync();
        using (var granted = await admin.PutAsJsonAsync($"/api/users/{_userId}/permissions",
                   new { permissions = new[] { "queue" } }))
        {
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        }

        using var updated = await admin.PutAsJsonAsync($"/api/users/{_userId}",
            new { fullName = "مستخدم اختبار الصلاحيات", role = "Doctor", isActive = true });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);

        using var check = await admin.GetAsync($"/api/users/{_userId}/permissions");
        var body = await check.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isExplicit").GetBoolean());
        Assert.Equal("Doctor", body.GetProperty("role").GetString());
        Assert.Equal(6, body.GetProperty("effective").GetArrayLength());
    }

    [Fact]
    public async Task Another_clinics_admin_cannot_touch_this_users_set()
    {
        await TestData.EnsureClinicBAsync(_factory);
        using var bAdmin = _factory.CreateAuthenticatedClient(
            await _factory.TokenAsync(TestEnvironment.ClinicBSlug, TestData.AdminRole));

        // The tenant filter hides clinic A's accounts from clinic B's scope entirely: the id is a stranger, so it
        // reads as "no such account" — not a 403 that would reveal the account exists.
        using var get = await bAdmin.GetAsync($"/api/users/{_userId}/permissions");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var put = await bAdmin.PutAsJsonAsync($"/api/users/{_userId}/permissions",
            new { permissions = new[] { "queue" } });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    private Task<HttpClient> AdminClientAsync() =>
        _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole)
            .ContinueWith(t => _factory.CreateAuthenticatedClient(t.Result));

    private static async Task<JsonElement> LoginBodyAsync(ApiFactory factory, string clinicSlug, string role)
    {
        var (username, password) = TestData.Credentials(clinicSlug, role);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.True(response.IsSuccessStatusCode,
            $"login failed for {role}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] PermissionsOf(JsonElement body) =>
        body.GetProperty("user").GetProperty("permissions")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
}
