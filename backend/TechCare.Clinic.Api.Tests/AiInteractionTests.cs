using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Ai;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_ADDENDUM.md 9.1 + 9.2 — every AI request writes one row into the interaction log (who, about which
/// patient, which sources, how long it took), the text only when the clinic enabled it, and every generated
/// summary or draft carries the medical disclaimer.
/// </summary>
[Collection(ApiCollection.Name)]
public class AiInteractionTests
{
    private readonly ApiFactory _factory;

    public AiInteractionTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_ask_request_writes_one_interaction_row_and_stays_content_free_by_default()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        await PutSettingsAsync(a.ClinicId, logText: false);
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);

        var before = DateTime.UtcNow.AddSeconds(-2);
        using var response = await client.PostAsJsonAsync("/api/ai/ask",
            new { question = "ما العلاج؟", patientId = a.PatientId });
        response.EnsureSuccessStatusCode();

        var row = await InteractionRowAsync(a.ClinicId, "ask", a.PatientId, before);
        Assert.NotNull(row);
        Assert.Equal(a.ClinicId, row.ClinicId);
        Assert.Equal(a.PatientId, row.PatientId);
        Assert.NotNull(row.UserId);
        // Text logging is off by default: the row proves the request happened without keeping its content.
        Assert.Null(row.QuestionText);
        Assert.Null(row.AnswerText);
        var sources = JsonSerializer.Deserialize<List<string>>(row.Sources);
        Assert.NotNull(sources);
        Assert.Empty(sources);
        Assert.True(row.DurationMs >= 0);
    }

    [Fact]
    public async Task Enabling_text_logging_in_the_settings_stores_the_question_and_answer()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        await PutSettingsAsync(a.ClinicId, logText: true);
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);

        var question = "سؤال مميز لتجربة تسجيل النص";
        var before = DateTime.UtcNow.AddSeconds(-2);
        using var response = await client.PostAsJsonAsync("/api/ai/ask", new { question, patientId = a.PatientId });
        response.EnsureSuccessStatusCode();

        var row = await InteractionRowAsync(a.ClinicId, "ask", a.PatientId, before);
        Assert.NotNull(row);
        Assert.Equal(question, row.QuestionText);
        Assert.NotNull(row.AnswerText);
        Assert.Contains("إجابة", row.AnswerText);
    }

    [Fact]
    public async Task Summary_and_draft_carry_the_disclaimer_and_each_writes_its_own_log_row()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        await PutSettingsAsync(a.ClinicId, logText: false);
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);

        var before = DateTime.UtcNow.AddSeconds(-2);

        using var summary = await client.PostAsJsonAsync($"/api/ai/patients/{a.PatientId}/summary", new { });
        summary.EnsureSuccessStatusCode();
        var summaryText = (await summary.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("summary").GetString();
        Assert.Contains("لا تغني عن تقدير الطبيب المعالج", summaryText);

        using var draft = await client.PostAsJsonAsync("/api/ai/draft", new { patientId = a.PatientId, kind = "report" });
        draft.EnsureSuccessStatusCode();
        var draftText = (await draft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString();
        Assert.Contains("لا تغني عن تقدير الطبيب المعالج", draftText);

        Assert.NotNull(await InteractionRowAsync(a.ClinicId, "summary", a.PatientId, before));
        Assert.NotNull(await InteractionRowAsync(a.ClinicId, "draft-report", a.PatientId, before));
    }

    [Fact]
    public async Task A_clinic_admin_reads_only_its_own_interactions_and_the_owner_reads_every_clinic()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var b = await ClinicFixtures.ClinicBAsync(_factory);

        var docA = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        var docB = await _factory.TokenAsync(TestEnvironment.ClinicBSlug, TestData.DoctorRole);
        var before = DateTime.UtcNow.AddSeconds(-2);

        using (var clientA = _factory.CreateAuthenticatedClient(docA))
        {
            using var resA = await clientA.PostAsJsonAsync("/api/ai/ask",
                new { question = "سؤال من العيادة الأولى", patientId = a.PatientId });
            resA.EnsureSuccessStatusCode();
        }
        using (var clientB = _factory.CreateAuthenticatedClient(docB))
        {
            using var resB = await clientB.PostAsJsonAsync("/api/ai/ask",
                new { question = "سؤال من العيادة الثانية", patientId = b.PatientId });
            resB.EnsureSuccessStatusCode();
        }

        var rowA = (await InteractionRowAsync(a.ClinicId, "ask", a.PatientId, before))!;
        var rowB = (await InteractionRowAsync(b.ClinicId, "ask", b.PatientId, before))!;

        var adminA = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole);
        int docAId;
        using (var meClient = _factory.CreateAuthenticatedClient(docA))
        {
            var me = await meClient.GetAsync("/api/auth/me");
            me.EnsureSuccessStatusCode();
            docAId = (await me.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("user").GetProperty("id").GetInt32();
        }

        using (var clientAdmin = _factory.CreateAuthenticatedClient(adminA))
        {
            // The clinic door: exactly this clinic's rows, never another clinic's.
            using var res = await clientAdmin.GetAsync("/api/audit-logs/ai?take=1000");
            res.EnsureSuccessStatusCode();
            var items = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
            Assert.Contains(items, e => e.GetProperty("id").GetInt64() == rowA.Id);
            Assert.DoesNotContain(items, e => e.GetProperty("id").GetInt64() == rowB.Id);

            // The user filter narrows the log to one account.
            using var filtered = await clientAdmin.GetAsync($"/api/audit-logs/ai?userId={docAId}&take=1000");
            filtered.EnsureSuccessStatusCode();
            var filteredItems = (await filtered.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(filteredItems);
            Assert.All(filteredItems, e => Assert.Equal(docAId, e.GetProperty("userId").GetInt32()));
        }

        // The owner's door: every clinic on the installation, and the clinic filter narrows it back to one.
        var owner = await _factory.TokenAsync(TestEnvironment.OwnerSlug, TestData.SuperAdminRole);
        using var clientOwner = _factory.CreateAuthenticatedClient(owner);
        using var all = await clientOwner.GetAsync("/api/owner/audit-logs/ai?take=1000");
        all.EnsureSuccessStatusCode();
        var allItems = (await all.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(allItems, e => e.GetProperty("id").GetInt64() == rowA.Id);
        Assert.Contains(allItems, e => e.GetProperty("id").GetInt64() == rowB.Id);

        using var onlyB = await clientOwner.GetAsync($"/api/owner/audit-logs/ai?clinicId={b.ClinicId}&take=1000");
        onlyB.EnsureSuccessStatusCode();
        var bItems = (await onlyB.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(bItems, e => e.GetProperty("id").GetInt64() == rowB.Id);
        Assert.DoesNotContain(bItems, e => e.GetProperty("id").GetInt64() == rowA.Id);
    }

    private async Task PutSettingsAsync(int clinicId, bool logText)
    {
        var owner = await _factory.TokenAsync(TestEnvironment.OwnerSlug, TestData.SuperAdminRole);
        using var client = _factory.CreateAuthenticatedClient(owner);
        using var saved = await client.PutAsJsonAsync("/api/owner/ai-settings", new
        {
            clinicId,
            baseUrl = "http://127.0.0.1:9999/v1",
            apiKey = "",
            model = "qwen-test",
            logQuestionText = logText
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    private async Task<AiInteraction?> InteractionRowAsync(int clinicId, string kind, int patientId, DateTime after) =>
        await _factory.WithClinicAsync(clinicId, db => db.AiInteractions.AsNoTracking()
            .Where(x => x.Kind == kind && x.PatientId == patientId && x.AtUtc >= after)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync());
}

/// <summary>
/// PLAN_ADDENDUM.md 9.4 — the interactive budget: the chat call gets a short cap and exactly one retry when the
/// first attempt fails in a transient way (a 5xx or a dropped connection); anything else, and a hung server,
/// answer without retrying.
/// </summary>
public class LmStudioInteractiveBudgetTests
{
    private const string Answer = "إجابة الاختبار";

    private static LmStudioClient ClientWith(HttpMessageHandler handler)
    {
        var options = Options.Create(new AiOptions
        {
            BaseUrl = "http://127.0.0.1:9999/v1",
            InteractiveTimeoutSeconds = 1
        });
        return new LmStudioClient(new HttpClient(handler), options);
    }

    private static IReadOnlyList<ChatMessage> OneQuestion() =>
        new[] { new ChatMessage("user", "ما العلاج؟") };

    [Fact]
    public async Task A_transient_503_is_retried_once_and_succeeds()
    {
        var handler = new ScriptedHandler((attempt, _) =>
            attempt == 1
                ? Task.FromResult(Chat(HttpStatusCode.ServiceUnavailable))
                : Task.FromResult(Chat(Answer)));
        var client = ClientWith(handler);

        var answer = await client.ChatAsync(OneQuestion());

        Assert.Equal(Answer, answer);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task A_dropped_connection_is_retried_once_and_succeeds()
    {
        var handler = new ScriptedHandler((attempt, _) =>
            attempt == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("dropped"))
                : Task.FromResult(Chat(Answer)));
        var client = ClientWith(handler);

        var answer = await client.ChatAsync(OneQuestion());

        Assert.Equal(Answer, answer);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task A_non_transient_400_is_not_retried()
    {
        var handler = new ScriptedHandler((attempt, _) => Task.FromResult(Chat(HttpStatusCode.BadRequest)));
        var client = ClientWith(handler);

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.ChatAsync(OneQuestion()));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task A_hung_server_answers_with_the_arabic_timeout_message()
    {
        var client = ClientWith(new HangingHandler());

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.ChatAsync(OneQuestion()));

        // The doctor gets the clear Arabic budget answer, naming the cap, not a raw cancellation.
        Assert.Contains("استغرق رد خدمة الذكاء", ex.Message);
        Assert.Contains("(1 ثانية)", ex.Message);
    }

    /// <summary>The exact shape LmStudioClient.ChatAsync parses: choices[0].message.content.</summary>
    private static HttpResponseMessage Chat(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { choices = new[] { new { message = new { role = "assistant", content } } } })
        };

    /// <summary>A provider error answer; its body is never parsed, only the status matters.</summary>
    private static HttpResponseMessage Chat(HttpStatusCode status) =>
        new(status)
        {
            Content = new StringContent("refused")
        };

    /// <summary>Answers every attempt according to its number (1-based).</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _respond;

        public ScriptedHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return _respond(RequestCount, cancellationToken);
        }
    }

    /// <summary>
    /// Never answers: waits out the budget token, which is what a hung model server does to the caller. Cancelling
    /// the delay faults it with a TaskCanceledException, exactly like the real HttpClient does when its token fires.
    /// </summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The hung handler must never answer.");
        }
    }
}
