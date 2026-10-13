using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Infrastructure;
using TechCare.Clinic.Api.Tests.Infrastructure;
using Xunit;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_ADDENDUM.md 7.1: disabling a user, changing its role, or resetting its password must void every token
/// issued before the change, within the 30 second cache window; a fresh login keeps working.
///
/// One dedicated account ("usr71a") of clinic A is created through the real admin endpoint so no seeded fixture
/// is ever touched. Each admin action is followed by <see cref="UserSessionValidator.ClearCache"/> to stand in
/// for the cache window expiring — that is what "within 30 seconds" means, deterministically.
/// </summary>
[Collection(ApiCollection.Name)]
public class UserTokenInvalidationTests : IAsyncLifetime
{
    private const string UserName = "usr71a";
    private const string OriginalPassword = "Usr71@2026";
    private const string NewPassword = "NewUsr71@2026";

    private readonly ApiFactory _factory;
    // Assigned in InitializeAsync; empty until then so the compiler sees a non-null value.
    private string _adminToken = string.Empty;

    public UserTokenInvalidationTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync() => _adminToken = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task disable_role_change_and_password_reset_all_void_the_old_token()
    {
        // ── Arrange: a fresh doctor account of clinic A, through the real admin endpoint ──
        var create = await _factory.CreateAuthenticatedClient(_adminToken)
            .PostAsJsonAsync("/api/users", new { FullName = "بنو 7.1", Username = UserName, Password = OriginalPassword, Role = "Doctor" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var userId = (await TestHttp.JsonAsync(create)).GetProperty("id").GetInt32();

        // ── The account's first token works, and it carries the TokenVersion claim from the start ──
        var (token, _) = await LoginAsync(UserName, OriginalPassword);
        var client = _factory.CreateAuthenticatedClient(token);
        var ok = await client.GetAsync("/api/patients");
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        Assert.Contains("TokenVersion", DecodeClaims(token));

        // ── 1) Admin disables the account: the old token is refused from the next check onward ──
        var disable = await UpdateUserAsync(userId, "Doctor", isActive: false);
        Assert.True(disable.IsSuccessStatusCode, await disable.Content.ReadAsStringAsync());
        UserSessionValidator.ClearCache(); // stands in for the 30 second window expiring
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/patients")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        // ── 2) Re-enable with a demotion to reception: the old token is still dead (version bumped again) ──
        var demote = await UpdateUserAsync(userId, "Reception", isActive: true);
        Assert.True(demote.IsSuccessStatusCode, await demote.Content.ReadAsStringAsync());
        UserSessionValidator.ClearCache();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        // ── 3) A fresh login with the new role works ──
        var (receptionToken, _) = await LoginAsync(UserName, OriginalPassword);
        Assert.Contains("Reception", DecodeClaims(receptionToken));
        var me = await _factory.CreateAuthenticatedClient(receptionToken).GetAsync("/api/auth/me");
        Assert.True(me.IsSuccessStatusCode, await me.Content.ReadAsStringAsync());

        // ── 4) Admin resets the password: even the brand-new token is void, the new password signs in ──
        var reset = await _factory.CreateAuthenticatedClient(_adminToken)
            .PostAsJsonAsync($"/api/users/{userId}/reset-password", new { NewPassword });
        Assert.True(reset.IsSuccessStatusCode, await reset.Content.ReadAsStringAsync());
        UserSessionValidator.ClearCache();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateAuthenticatedClient(receptionToken).GetAsync("/api/auth/me")).StatusCode);

        var (finalToken, _) = await LoginAsync(UserName, NewPassword);
        var back = await _factory.CreateAuthenticatedClient(finalToken).GetAsync("/api/auth/me");
        Assert.True(back.IsSuccessStatusCode, await back.Content.ReadAsStringAsync());

        // ── And the old password is still refused while the account is active ──
        using var anon = _factory.CreateClient();
        using var wrong = await anon.PostAsJsonAsync("/api/auth/login", new { username = UserName, password = OriginalPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    private async Task<HttpResponseMessage> UpdateUserAsync(int id, string role, bool isActive)
    {
        var client = _factory.CreateAuthenticatedClient(_adminToken);
        return await client.PutAsJsonAsync($"/api/users/{id}", new { FullName = "بنو 7.1", Role = role, IsActive = isActive });
    }

    private async Task<(string Token, DateTime Expires)> LoginAsync(string username, string password)
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login '{username}' -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        var body = await TestHttp.JsonAsync(response);
        return (body.GetProperty("token").GetString()!, body.GetProperty("expiresAtUtc").GetDateTime());
    }

    /// <summary>Decodes the unverified JWT payload — enough to inspect the claims the API wrote.</summary>
    private static string DecodeClaims(string token)
    {
        var payload = token.Split('.')[1];
        payload = new string(payload.PadRight(payload.Length + 4 - payload.Length % 4, '=').Select(c => c switch
        {
            '-' => '+',
            '_' => '/',
            _ => c
        }).ToArray());
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
    }
}
