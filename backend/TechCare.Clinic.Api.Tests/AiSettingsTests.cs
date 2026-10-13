using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Features.Ai;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN.md 1.2 — the AI settings live in the database, managed from the owner panel: a global row plus one
/// override row per clinic, and the client resolves the effective settings for the calling clinic.
/// </summary>
[Collection(ApiCollection.Name)]
public class AiSettingsTests
{
    private readonly ApiFactory _factory;

    public AiSettingsTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_owner_can_save_a_global_settings_row_and_it_becomes_effective()
    {
        using var owner = await OwnerClientAsync();

        using var saved = await owner.PutAsJsonAsync("/api/owner/ai-settings", new
        {
            clinicId = (int?)null,
            baseUrl = "http://127.0.0.1:9999/v1",
            apiKey = "global-key",
            model = "qwen-global"
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // Resolve the global scope itself (no clinic): the row the owner just saved must be the one in force,
        // whatever other tests have left in the shared database.
        var effective = await _factory.WithServicesAsync(services =>
            services.GetRequiredService<AiSettingsService>().ResolveAsync(null));
        Assert.Equal("http://127.0.0.1:9999/v1", effective.BaseUrl);
        Assert.Equal("global-key", effective.ApiKey);
        Assert.Equal("qwen-global", effective.ChatModel);
    }

    [Fact]
    public async Task A_clinic_row_overrides_the_global_one_for_that_clinic_only()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var b = await ClinicFixtures.ClinicBAsync(_factory);
        using var owner = await OwnerClientAsync();

        using var global = await owner.PutAsJsonAsync("/api/owner/ai-settings", new
        {
            clinicId = (int?)null,
            baseUrl = "http://127.0.0.1:9999/v1",
            apiKey = "",
            model = "qwen-global"
        });
        Assert.Equal(HttpStatusCode.OK, global.StatusCode);

        using var clinicA = await owner.PutAsJsonAsync("/api/owner/ai-settings", new
        {
            clinicId = a.ClinicId,
            baseUrl = "http://gpu.local:8000/v1",
            apiKey = "clinic-a-key",
            model = "qwen-big"
        });
        Assert.Equal(HttpStatusCode.OK, clinicA.StatusCode);

        var effectiveA = await _factory.WithServicesAsync(services =>
            services.GetRequiredService<AiSettingsService>().ResolveAsync(a.ClinicId));
        var effectiveB = await _factory.WithServicesAsync(services =>
            services.GetRequiredService<AiSettingsService>().ResolveAsync(b.ClinicId));

        Assert.Equal("http://gpu.local:8000/v1", effectiveA.BaseUrl);
        Assert.Equal("clinic-a-key", effectiveA.ApiKey);
        Assert.Equal("qwen-big", effectiveA.ChatModel);
        // Clinic B keeps the global settings: its scope never sees A's row.
        Assert.Equal("http://127.0.0.1:9999/v1", effectiveB.BaseUrl);
        Assert.Equal("qwen-global", effectiveB.ChatModel);
    }

    [Fact]
    public async Task A_clinic_tenant_only_sees_its_own_settings_row_and_delete_is_idempotent()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        using var owner = await OwnerClientAsync();

        using var created = await owner.PutAsJsonAsync("/api/owner/ai-settings", new
        {
            clinicId = a.ClinicId,
            baseUrl = "http://gpu.local:8000/v1",
            apiKey = "",
            model = "qwen-big"
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var visible = await _factory.WithClinicAsync(a.ClinicId, db =>
            db.AiSettings.Select(s => s.ClinicId).ToListAsync());
        Assert.Equal(a.ClinicId, visible.Single());   // the global and any other clinic's rows stay out of the tenant's view

        using var deleted = await owner.DeleteAsync($"/api/owner/ai-settings/{a.ClinicId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // Deleting a scope that has no row answers 204 the same way — the endpoint is idempotent.
        using var deletedAgain = await owner.DeleteAsync($"/api/owner/ai-settings/{a.ClinicId}");
        Assert.Equal(HttpStatusCode.NoContent, deletedAgain.StatusCode);
    }

    [Fact]
    public async Task Saving_a_settings_row_with_a_broken_base_url_is_refused()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        using var owner = await OwnerClientAsync();

        using var bad = await owner.PutAsJsonAsync("/api/owner/ai-settings", new
        {
            clinicId = (int?)null,
            baseUrl = "not a url",
            apiKey = "",
            model = "qwen"
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private async Task<HttpClient> OwnerClientAsync()
    {
        var token = await _factory.TokenAsync(TestEnvironment.OwnerSlug, TestData.SuperAdminRole);
        return _factory.CreateAuthenticatedClient(token);
    }
}
