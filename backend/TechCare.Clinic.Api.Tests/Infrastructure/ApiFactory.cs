using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Features.Ai;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Tests.Infrastructure;

/// <summary>
/// Boots the real API (same Program.cs, same middleware, same controllers) against a private SQLite
/// database in memory. Nothing outside the machine's own disk is touched:
/// <list type="bullet">
/// <item>database: a named in-memory SQLite database, kept alive by <see cref="_keepAlive"/>;</item>
/// <item>medical library: a throwaway folder under the temp path;</item>
/// <item>vector store: disabled through configuration;</item>
/// <item>LM Studio: replaced by <see cref="FakeLmStudioClient"/>, so no socket is ever opened.</item>
/// </list>
/// Configuration comes from <see cref="TestEnvironment"/> because the API reads it before the host is built.
/// One instance owns one database, so tests that share it must not assume an empty clinic
/// (see <see cref="TestData"/> for the fixtures they may rely on).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Holds the in-memory database open for the lifetime of the factory.</summary>
    private readonly SqliteConnection _keepAlive;

    private readonly Dictionary<(string Clinic, string Role), string> _tokens = new();
    private readonly Dictionary<string, Task> _once = new();
    private readonly object _gate = new();

    public ApiFactory()
    {
        TestEnvironment.ApplyToProcess();

        _keepAlive = new SqliteConnection(TestEnvironment.SqliteConnectionString);
        _keepAlive.Open();
    }

    /// <summary>Empty folder that stands in for the real <c>MedicalLibrary</c>.</summary>
    public static string LibraryPath => TestEnvironment.LibraryPath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<LmStudioClient>();
            services.AddSingleton<LmStudioClient>(sp =>
                new FakeLmStudioClient(sp.GetRequiredService<IOptions<AiOptions>>()));
        });
    }

    /// <summary>
    /// Runs <paramref name="action"/> inside a fresh DI scope (used to arrange fixture data and to assert
    /// directly against the database). Resolve <c>ClinicDbContext</c> inside the action.
    /// </summary>
    public async Task<T> WithServicesAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>
    /// Runs a database action scoped to one clinic, the way a request coming from that clinic sees the data.
    /// Reading with the other clinic's id is how the isolation tests arrange and verify their fixtures.
    /// </summary>
    public Task<T> WithClinicAsync<T>(int clinicId, Func<ClinicDbContext, Task<T>> action) =>
        WithServicesAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().SetClinic(clinicId);
            return await action(services.GetRequiredService<ClinicDbContext>());
        });

    /// <summary>Runs <paramref name="initialise"/> at most once per factory (lazily, thread-safe).</summary>
    public async Task<T> EnsureOnceAsync<T>(string key, Func<Task<T>> initialise)
    {
        Task pending;
        lock (_gate)
        {
            if (!_once.TryGetValue(key, out var existing))
            {
                existing = Task.Run(initialise);
                _once[key] = existing;
            }
            pending = existing;
        }

        return await (Task<T>)pending;
    }

    /// <summary>Client with a bearer token already attached.</summary>
    public HttpClient CreateAuthenticatedClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Bearer token for a seeded account, obtained through the real login endpoint and cached per
    /// (clinic, role). Caching matters: the login endpoint is rate limited to 10 calls per 5 minutes and
    /// every test request comes from the same fake IP address.
    /// </summary>
    public async Task<string> TokenAsync(string clinicSlug, string role)
    {
        var key = (clinicSlug, role);
        lock (_gate)
        {
            if (_tokens.TryGetValue(key, out var cached)) return cached;
        }

        var (username, password) = TestData.Credentials(clinicSlug, role);
        using var client = CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login failed for {username}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()
            ?? throw new InvalidOperationException($"Login for {username} returned no token.");

        lock (_gate) _tokens[key] = token;
        return token;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        _keepAlive.Dispose();
    }
}
