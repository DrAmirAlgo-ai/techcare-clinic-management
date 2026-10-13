using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Features.Ai;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_ADDENDUM.md 0.1 — the safety net itself: the API boots through the harness, the seeded accounts work,
/// and nothing outside the test host is contacted (no LM Studio, no pgvector, no real library folder).
/// </summary>
[Collection(ApiCollection.Name)]
public class SmokeTests
{
    private readonly ApiFactory _factory;

    public SmokeTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_endpoint_answers_ok()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Seeded_admin_of_clinic_a_can_log_in_and_read_his_own_profile()
    {
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/api/auth/me");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TestEnvironment.ClinicAName, body.GetProperty("clinic").GetProperty("name").GetString());
        Assert.Equal(TestData.AdminRole, body.GetProperty("user").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Protected_endpoint_without_a_token_is_unauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void The_ai_client_is_faked_and_the_vector_store_is_disabled()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.IsType<FakeLmStudioClient>(scope.ServiceProvider.GetRequiredService<LmStudioClient>());
        Assert.False(scope.ServiceProvider.GetRequiredService<VectorStore>().Enabled);
    }

    [Fact]
    public async Task The_second_clinic_fixture_can_log_in()
    {
        await TestData.EnsureClinicBAsync(_factory);
        var token = await _factory.TokenAsync(TestEnvironment.ClinicBSlug, TestData.AdminRole);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/api/auth/me");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TestEnvironment.ClinicBName, body.GetProperty("clinic").GetProperty("name").GetString());
    }
}
