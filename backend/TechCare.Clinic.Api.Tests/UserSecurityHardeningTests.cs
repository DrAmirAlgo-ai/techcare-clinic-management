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
/// Global username uniqueness + account security hardening: a username is unique across the whole installation,
/// so no clinic admin (and no clinic the owner registers) may create a name another clinic already uses — a bare
/// login can therefore never be ambiguous and needs no clinic code. Also covered: ten-character passwords with a
/// common-password blocklist, a temporary password that must be changed at the first login, and a 15 minute lock
/// after 5 failed attempts.
///
/// Clinic B comes from <see cref="TestData.EnsureClinicBAsync"/> with its own "-b" suffixed accounts, so every
/// username in the test database is globally unique. No seeded fixture is ever touched.
/// </summary>
[Collection(ApiCollection.Name)]
public class UserSecurityHardeningTests : IAsyncLifetime
{
    private readonly ApiFactory _factory;
    // Assigned in InitializeAsync; empty until then so the compiler sees a non-null value.
    private string _adminToken = string.Empty;

    public UserSecurityHardeningTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        _adminToken = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole);
        await TestData.EnsureClinicBAsync(_factory);
    }

    // Nothing to clean up: the global-uniqueness tests only write through the API and assert rows, never fixtures.
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task a_username_taken_by_any_clinic_is_refused_in_another()
    {
        // "admin" belongs to clinic A. Clinic B (whose own admin is "admin-b") cannot create it: 409, no new row.
        var bToken = await _factory.TokenAsync(TestEnvironment.ClinicBSlug, TestData.AdminRole);
        using var bClient = _factory.CreateAuthenticatedClient(bToken);
        using var dup = await bClient.PostAsJsonAsync("/api/users",
            new { FullName = "مكرر", Username = "admin", Password = "DupUsr@2026", Role = "Reception" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("اسم المستخدم", await dup.Content.ReadAsStringAsync());

        // And the reverse direction: clinic B's "admin-b" is refused inside clinic A the same way.
        using var aClient = _factory.CreateAuthenticatedClient(_adminToken);
        using var dupBack = await aClient.PostAsJsonAsync("/api/users",
            new { FullName = "مكرر عكسي", Username = "admin-b", Password = "DupUsr@2026", Role = "Reception" });
        Assert.Equal(HttpStatusCode.Conflict, dupBack.StatusCode);

        // Neither refusal wrote a row under the taken names: the two originals, and only those two.
        var rows = await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            return await db.Users.IgnoreQueryFilters().CountAsync(u => u.Username == "admin" || u.Username == "admin-b");
        });
        Assert.Equal(2, rows);

        // And a globally unique username still signs in without any clinic code.
        await LoginAsync("admin-b", TestEnvironment.ClinicBAdminPassword, null);
    }

    [Fact]
    public async Task five_failed_logins_lock_the_account_for_fifteen_minutes()
    {
        var create = await _factory.CreateAuthenticatedClient(_adminToken)
            .PostAsJsonAsync("/api/users", new { FullName = "قفل 7.5", Username = "usr75a", Password = "Usr75@2026", Role = "Reception" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());

        // Pre-load four of the five allowed failures so the test spends only one real attempt.
        await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Username == "usr75a");
            user.FailedLoginCount = 4;
            await db.SaveChangesAsync();
            return true;
        });

        using var anon = _factory.CreateClient();
        using var fifth = await anon.PostAsJsonAsync("/api/auth/login", new { username = "usr75a", password = "Wrong@2026" });
        Assert.Equal(HttpStatusCode.Unauthorized, fifth.StatusCode);

        var (count, until) = await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Username == "usr75a");
            return (user.FailedLoginCount, user.LockedUntilUtc);
        });
        Assert.Equal(0, count);
        Assert.NotNull(until);
        Assert.InRange(until!.Value, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));

        // While locked, even the correct password is refused — with the lockout's own message.
        using var lockedOut = await anon.PostAsJsonAsync("/api/auth/login", new { username = "usr75a", password = "Usr75@2026" });
        Assert.Equal(HttpStatusCode.Unauthorized, lockedOut.StatusCode);
        Assert.Contains("مقفل", await lockedOut.Content.ReadAsStringAsync());

        // Once the window has passed the account is usable again.
        await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Username == "usr75a");
            user.LockedUntilUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            return true;
        });
        await LoginAsync("usr75a", "Usr75@2026", null);
    }

    [Fact]
    public async Task created_account_is_forced_to_change_its_temporary_password()
    {
        var create = await _factory.CreateAuthenticatedClient(_adminToken)
            .PostAsJsonAsync("/api/users", new { FullName = "كلمة مؤقتة 7.5", Username = "usr75b", Password = "TempUsr75@2026", Role = "Doctor" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());

        // The first login carries the forced-change flag.
        var body = await LoginAsync("usr75b", "TempUsr75@2026", null);
        Assert.True(body.GetProperty("mustChangePassword").GetBoolean());
        var client = _factory.CreateAuthenticatedClient(body.GetProperty("token").GetString()!);

        // The wrong current password is refused, and so is a weak new one.
        using var wrongCurrent = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "Wrong@2026", newPassword = "NewUsr75@2026" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        using var weak = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "TempUsr75@2026", newPassword = "password123" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        // The real change clears the flag and voids the old password.
        var changed = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "TempUsr75@2026", newPassword = "NewUsr75@2026" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        using var anon = _factory.CreateClient();
        using var oldRefused = await anon.PostAsJsonAsync("/api/auth/login", new { username = "usr75b", password = "TempUsr75@2026" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefused.StatusCode);

        var second = await LoginAsync("usr75b", "NewUsr75@2026", null);
        Assert.False(second.GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task short_and_common_passwords_are_rejected()
    {
        var client = _factory.CreateAuthenticatedClient(_adminToken);

        // Too short, then too common (at a length that already passes the minimum).
        using var shortOne = await client.PostAsJsonAsync("/api/users",
            new { FullName = "سياسة 7.5", Username = "usr75c", Password = "Short@26", Role = "Reception" });
        Assert.Equal(HttpStatusCode.BadRequest, shortOne.StatusCode);
        using var common = await client.PostAsJsonAsync("/api/users",
            new { FullName = "سياسة 7.5", Username = "usr75c", Password = "password123", Role = "Reception" });
        Assert.Equal(HttpStatusCode.BadRequest, common.StatusCode);

        // ...and an admin reset to a common password is refused the same way.
        var created = await client.PostAsJsonAsync("/api/users",
            new { FullName = "سياسة 7.5", Username = "usr75d", Password = "ValidUsr75@26", Role = "Reception" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var userId = (await TestHttp.JsonAsync(created)).GetProperty("id").GetInt32();
        using var resetCommon = await client.PostAsJsonAsync($"/api/users/{userId}/reset-password", new { newPassword = "12345678901" });
        Assert.Equal(HttpStatusCode.BadRequest, resetCommon.StatusCode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<JsonElement> LoginAsync(string username, string password, string? clinicSlug)
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { username, password, clinicSlug = clinicSlug ?? "" });
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login '{username}' -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await TestHttp.JsonAsync(response);
    }
}
