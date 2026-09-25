using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Checks Escalation.RunAsync's per-frame "could this code throw here" question
/// against a fake Invoke and its root-cause read against a fake Converse that
/// records what it is shown, confirming it returns frame verdicts and a drafted
/// issue built from a real IssueDraft/SourceVerification pass without calling
/// GitHub.
/// </summary>
public class EscalateTests
{
    /// <summary>An Invoke that answers every question with response, recording each prompt in questions.</summary>
    private static BedrockBackend.Invoke Answering(byte[] response, List<string> questions) => (_, body, _) =>
    {
        questions.Add(JsonDocument.Parse(body).RootElement.GetProperty("prompt").GetString()!);
        return Task.FromResult(response);
    };

    private static byte[] CompletionBody(Dictionary<string, double> letterLogprobs)
    {
        var top = new JsonObject();
        foreach (var (letter, logprob) in letterLogprobs)
        {
            top[letter] = logprob;
        }
        var payload = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["text"] = "A",
                ["logprobs"] = new JsonObject { ["top_logprobs"] = new JsonArray(top) },
                ["finish_reason"] = "stop",
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 50, ["completion_tokens"] = 1, ["total_tokens"] = 51 },
        };
        return Encoding.UTF8.GetBytes(payload.ToJsonString());
    }

    private sealed class FakeDiagnosis(string answer)
    {
        public List<(string ModelId, string Prompt)> Calls { get; } = [];

        public Task<string> Converse(string modelId, string system, string user, CancellationToken cancellationToken)
        {
            Calls.Add((modelId, user));
            return Task.FromResult(answer);
        }
    }

    private static readonly ParsedTrace Trace = new(
        "dotnet", "System.IndexOutOfRangeException",
        [
            new Frame("Pricing.Tiers.TierResolver.Resolve", true),
            new Frame("System.Linq.Enumerable.First", false),
        ]);

    private const string RawTrace = """
        System.IndexOutOfRangeException: Index was outside the bounds of the array.
           at Pricing.Tiers.TierResolver.Resolve(Int32 index) in src/Pricing/Tiers/TierResolver.cs:line 42
           at System.Linq.Enumerable.First[TSource](IEnumerable`1 source)
        """;

    [Fact]
    public async Task VerifiesFetchedFramesDiagnosesFromTheSourceAndDrafts()
    {
        const string source = "public void Resolve(int index) { return tiers[index]; }";
        var request = new EscalateRequest(
            "acme/catalog", "abc1234", Trace, RawTrace,
            Sources: new Dictionary<string, string> { ["src/Pricing/Tiers/TierResolver.cs"] = source },
            Bug: 0.9,
            FirstSeen: DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var questions = new List<string>();
        var invoke = Answering(CompletionBody(new Dictionary<string, double> { ["A"] = -0.1, ["B"] = -3.0 }), questions);
        var diagnosis = new FakeDiagnosis("The tier list is shorter than the resolved index.");

        var result = await Escalation.RunAsync(invoke, "arn:model", diagnosis.Converse, "eu.model", request);

        // Only the frame that is both in-app and has a fetched source gets asked about.
        var verdict = Assert.Single(result.FrameVerdicts);
        Assert.Equal("Pricing.Tiers.TierResolver.Resolve", verdict.Frame.Method);
        Assert.True(verdict.Matched);
        Assert.Single(questions);

        // The diagnosis model sees the trace and the fetched file, once.
        var call = Assert.Single(diagnosis.Calls);
        Assert.Equal("eu.model", call.ModelId);
        Assert.Contains("TierResolver.Resolve(Int32 index)", call.Prompt);
        Assert.Contains(source, call.Prompt);

        Assert.Equal("System.IndexOutOfRangeException in Pricing.Tiers.TierResolver.Resolve", result.Draft.Title);
        Assert.Contains("P(bug) = 0.90", result.Draft.Body);
        Assert.Contains("The tier list is shorter than the resolved index.", result.Draft.Body);
        Assert.Contains("1 of 1 matched.", result.Draft.Body);
        Assert.Contains("- `src/Pricing/Tiers/TierResolver.cs`", result.Draft.Body);
    }

    [Fact]
    public async Task NoFetchedSourcesSkipsVerificationAndDiagnosesFromTheTraceAlone()
    {
        var request = new EscalateRequest(
            "acme/catalog", "main", Trace, RawTrace, Sources: new Dictionary<string, string>(),
            Bug: 0.6,
            FirstSeen: DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var questions = new List<string>();
        var invoke = Answering(CompletionBody(new Dictionary<string, double> { ["A"] = -0.1, ["B"] = -3.0 }), questions);
        var diagnosis = new FakeDiagnosis("Unclear without a checkout.");

        var result = await Escalation.RunAsync(invoke, "arn:model", diagnosis.Converse, "eu.model", request);

        Assert.Empty(result.FrameVerdicts);
        Assert.Empty(questions);
        Assert.Contains("No source file named by the trace could be fetched.", Assert.Single(diagnosis.Calls).Prompt);
        Assert.Contains("0 of 0 matched.", result.Draft.Body);
        Assert.Contains("(none; no file named by the trace could be fetched)", result.Draft.Body);
    }
}
