using Microsoft.Extensions.Options;
using TechCare.Clinic.Api.Features.Ai;

namespace TechCare.Clinic.Api.Tests.Infrastructure;

/// <summary>
/// Stand-in for <see cref="LmStudioClient"/> that never opens a socket, so tests can run with the LM Studio
/// server switched off (PLAN_ADDENDUM.md 0.1). Answers are deterministic; flipping <see cref="Reachable"/>
/// to <c>false</c> simulates the AI server being down, which later phases use to check their fallbacks.
/// </summary>
public sealed class FakeLmStudioClient : LmStudioClient
{
    /// <summary>Length of the vectors returned by <see cref="EmbedAsync"/>, matching the configured 768 dims.</summary>
    private const int EmbeddingDimensions = 768;

    public FakeLmStudioClient(IOptions<AiOptions> options) : base(new HttpClient(), options) { }

    /// <summary>Set to <c>false</c> to mimic "LM Studio is not running".</summary>
    public bool Reachable { get; set; } = true;

    /// <summary>Answer returned by <see cref="ChatAsync"/>/<see cref="ChatExternalAsync"/>.</summary>
    public string Answer { get; set; } = "إجابة تجريبية من النموذج المزيّف";

    /// <summary>Everything the code under test sent to the model, in order (useful for prompt assertions).</summary>
    public List<IReadOnlyList<ChatMessage>> Prompts { get; } = new();

    /// <summary>The max_tokens of the last <see cref="ChatAsync"/> call — null when the caller kept the option default.</summary>
    public int? LastMaxTokens { get; private set; }

    public override Task<string> ChatAsync(IEnumerable<ChatMessage> messages, double temperature = 0.3,
        CancellationToken ct = default, int? maxTokens = null, string? model = null)
    {
        LastMaxTokens = maxTokens;
        Record(messages);
        return Task.FromResult(Answer);
    }

    public override Task<string> ChatExternalAsync(IEnumerable<ChatMessage> messages, double temperature = 0.3,
        int? maxTokens = null, CancellationToken ct = default)
    {
        Record(messages);
        return Task.FromResult(Answer);
    }

    public override Task<List<float[]>> EmbedAsync(IReadOnlyList<string> inputs, bool isQuery, CancellationToken ct = default)
    {
        if (!Reachable) throw new AiUnavailableException("تعذّر الاتصال بخادم الذكاء الاصطناعي");

        var vectors = inputs.Select(input => Embedding(input, isQuery)).ToList();
        return Task.FromResult(vectors);
    }

    public override Task<(bool Reachable, List<string> Models)> StatusAsync(CancellationToken ct = default) =>
        Task.FromResult((Reachable, Reachable
            ? new List<string> { Options.ChatModel, Options.EmbeddingModel }
            : new List<string>()));

    private void Record(IEnumerable<ChatMessage> messages)
    {
        if (!Reachable) throw new AiUnavailableException("تعذّر الاتصال بخادم الذكاء الاصطناعي");
        Prompts.Add(messages.ToList());
    }

    /// <summary>Deterministic, non-zero pseudo-embedding so cosine similarity is stable across runs.</summary>
    private static float[] Embedding(string text, bool isQuery)
    {
        var vector = new float[EmbeddingDimensions];
        var seed = 17u;
        foreach (var c in text) seed = seed * 31 + c;
        seed = seed * 31 + (isQuery ? 7u : 3u);

        for (var i = 0; i < vector.Length; i++)
        {
            seed = seed * 1664525 + 1013904223;
            vector[i] = (seed >> 8) % 1000 / 1000f - 0.5f;
        }

        vector[0] += 1f; // guarantees a non-zero vector
        return vector;
    }
}
