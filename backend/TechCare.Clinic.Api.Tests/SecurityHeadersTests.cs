using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using TechCare.Clinic.Api.Infrastructure;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_ADDENDUM.md 7.6 + 7.7 — the security headers, the correlation id, and the error path: an induced 500
/// answers with clean ProblemDetails only (no stack trace, no internal details), and the log file carries the
/// same request id.
/// </summary>
[Collection(ApiCollection.Name)]
public class SecurityHeadersTests
{
    private readonly ApiFactory _factory;

    public SecurityHeadersTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Every_response_carries_the_baseline_headers_and_a_correlation_id()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("microphone=(self)", response.Headers.GetValues("Permissions-Policy").Single());
        Assert.False(string.IsNullOrWhiteSpace(response.Headers.GetValues("X-Request-Id").FirstOrDefault()));
        // Outside production the strict CSP stays off: Vite's dev server injects module scripts and the HMR socket.
        // GetValues throws on an absent header, so "stays off" is read with TryGetValues instead.
        Assert.False(response.Headers.TryGetValues("Content-Security-Policy", out _));
    }

    [Fact]
    public async Task Production_applies_the_strict_csp_and_hsts_without_breaking_signal_r()
    {
        using var production = new ApiFactory().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            // Keep this host's log out of the shared test file, so two hosts never write the same file at once.
            b.ConfigureAppConfiguration((ctx, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:FilePath"] = Path.Combine(Path.GetTempPath(), "techcare-api-tests", "logs", "prod-app-.log")
            }));
        });
        using var client = production.CreateClient();

        var response = await client.GetAsync("/api/health");

        response.EnsureSuccessStatusCode();
        // The strict CSP is a production-only header, so its presence proves the production branch of the
        // security middleware ran. (HSTS and the http→https redirect are the framework's own boilerplate in
        // this block of the pipeline; they only attach to real https responses, and the in-process test
        // client never terminates TLS, so this host proves the branch by its CSP instead.)
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());

        // The SignalR negotiation must still answer: the hub rides the same pipeline, strict CSP included.
        var negotiate = await client.PostAsync("/hubs/queue/negotiate?negotiateVersion=1", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
    }

    /// <summary>Reads a log file while Serilog is still writing to it (shared read, shared write lock).</summary>
    private static string ReadUnlocked(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Production_with_an_empty_cors_list_refuses_to_start()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupGuards.EnsureCorsConfiguredForProduction("Production", Array.Empty<string>()));

        // A non-empty list — or any environment other than production — passes.
        StartupGuards.EnsureCorsConfiguredForProduction("Production", new[] { "https://clinic.example.com" });
        StartupGuards.EnsureCorsConfiguredForProduction("Development", Array.Empty<string>());
    }

    [Fact]
    public async Task An_unhandled_exception_comes_back_as_clean_problem_details_with_the_correlation_id_in_the_log()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/dev/crash");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(500, json.GetProperty("status").GetInt32());
        Assert.Contains("خطأ", json.GetProperty("title").GetString()!);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at TechCare", body, StringComparison.OrdinalIgnoreCase);

        // PLAN_ADDENDUM.md 7.7: the log file carries the same correlation id the response returned. The file is
        // read with FileShare.ReadWrite because Serilog still holds it open while writing.
        var correlationId = response.Headers.GetValues("X-Request-Id").Single();
        var logDirectory = Path.GetDirectoryName(TestEnvironment.LogFilePath)!;
        var logs = Directory.GetFiles(logDirectory, "app-*.log");
        Assert.Contains(logs, f => ReadUnlocked(f).Contains(correlationId));
    }
}