using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Exercises Triage.RunAsync against a fake Invoke, checking that it issues all
/// nine of the tree's sub-questions concurrently, that one failing sub-question
/// does not fail the row, and that a row with no evidence routes to the baseline
/// answer.
/// </summary>
public class TriageTests
{
    private static readonly QuestionTree Tree = QuestionTree.LoadEmbedded();

    private static JsonElement RowWithEvidence(string id)
    {
        var row = new JsonObject
        {
            ["id"] = id,
            ["state"] = new JsonObject
            {
                ["stack_trace"] = "boom",
                ["evidence"] = new JsonObject { ["connections_opened"] = 900, ["connections_disposed"] = 3 },
            },
        };
        return JsonDocument.Parse(row.ToJsonString()).RootElement;
    }

    private static JsonElement RowWithoutEvidence(string id)
    {
        var row = new JsonObject { ["id"] = id, ["state"] = new JsonObject { ["stack_trace"] = "boom" } };
        return JsonDocument.Parse(row.ToJsonString()).RootElement;
    }

    private static byte[] CompletionBody(Dictionary<string, double> letterLogprobs, long promptTokens = 50)
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
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = promptTokens, ["completion_tokens"] = 1, ["total_tokens"] = promptTokens + 1,
            },
        };
        return Encoding.UTF8.GetBytes(payload.ToJsonString());
    }

    // A/B/C/D logprobs distinct enough to answer every question (2 options or, for
    // surface, 4) without abstaining.
    private static byte[] DefaultResponse() =>
        CompletionBody(new Dictionary<string, double> { ["A"] = -0.2, ["B"] = -1.0, ["C"] = -1.5, ["D"] = -2.0 });

    /// <summary>
    /// An Invoke that records each prompt and, after a short delay, answers with respondTo or DefaultResponse.
    /// </summary>
    private static BedrockBackend.Invoke Fake(List<string> prompts, Func<string, byte[]>? respondTo = null) =>
        async (_, body, cancellationToken) =>
        {
            var prompt = JsonDocument.Parse(body).RootElement.GetProperty("prompt").GetString()!;
            lock (prompts)
            {
                prompts.Add(prompt);
            }
            await Task.Delay(30, cancellationToken);
            return respondTo?.Invoke(prompt) ?? DefaultResponse();
        };

    private static readonly string[] ExpectedKeys =
    [
        "baseline", "surface", "unreleased_resource", "self_inflicted_load", "invalid_value_sent",
        "internal_inconsistency", "repeated_work", "external_change", "external_unavailable",
    ];

    [Fact]
    public async Task IssuesAllNineSubQuestions()
    {
        var prompts = new List<string>();

        var result = await Triage.RunAsync(Fake(prompts), "arn:model", RowWithEvidence("row-1"), Tree);

        Assert.Equal(9, prompts.Count);
        Assert.Equal(ExpectedKeys.OrderBy(k => k), result.Answers.Select(a => a.Key).OrderBy(k => k));
        Assert.All(result.Answers, answer => Assert.Null(answer.Error));
    }

    [Fact]
    public async Task DeclaredMassIsTheLowestAcrossAnsweredSubQuestions()
    {
        var result = await Triage.RunAsync(Fake([]), "arn:model", RowWithEvidence("row-6"), Tree);

        // Two-option questions see only A and B, which carry less mass than surface's four letters.
        Assert.Equal(Math.Exp(-0.2) + Math.Exp(-1.0), result.DeclaredMass, precision: 9);
    }

    [Fact]
    public async Task SubQuestionsRunConcurrentlyNotSequentially()
    {
        // A constrained thread pool can delay how quickly nine queued Task.Delay
        // continuations get serviced, which would make this flaky on a wall-clock
        // threshold alone; raising the minimum thread count removes that noise so
        // MaxInFlight reflects concurrency rather than scheduling lag.
        ThreadPool.SetMinThreads(32, 32);
        var gate = new object();
        var inFlight = 0;
        var maxInFlight = 0;
        BedrockBackend.Invoke invoke = async (_, _, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            lock (gate)
            {
                maxInFlight = Math.Max(maxInFlight, current);
            }
            await Task.Delay(150, cancellationToken);
            Interlocked.Decrement(ref inFlight);
            return DefaultResponse();
        };
        var stopwatch = Stopwatch.StartNew();

        await Triage.RunAsync(invoke, "arn:model", RowWithEvidence("row-2"), Tree);

        stopwatch.Stop();
        // Nine sequential 150ms round trips would take ~1350ms; concurrent ones
        // should finish in roughly one round trip's worth of time.
        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"expected concurrent scoring to finish well under 1000ms, took {stopwatch.ElapsedMilliseconds}ms");
        Assert.True(maxInFlight >= 5, $"expected several sub-questions in flight at once, saw {maxInFlight}");
    }

    [Fact]
    public async Task OneFailingSubQuestionDoesNotFailTheRow()
    {
        var failing = Tree.Signals.Single(s => s.Key == "repeated_work").Question;
        var invoke = Fake([], prompt => prompt.Contains(failing)
            ? throw new AmazonServiceException("model not ready: endpoint scaled to zero")
            : DefaultResponse());

        var result = await Triage.RunAsync(invoke, "arn:model", RowWithEvidence("row-3"), Tree);

        Assert.Equal(9, result.Answers.Count);
        var failed = result.Answers.Single(a => a.Key == "repeated_work");
        Assert.NotNull(failed.Error);
        Assert.Null(failed.Probabilities);
        Assert.All(result.Answers.Where(a => a.Key != "repeated_work"), answer => Assert.Null(answer.Error));
        // Combine() still runs on whatever came back rather than throwing.
        Assert.True(result.Verdict.Bug is >= 0.0 and <= 1.0);
    }

    [Fact]
    public async Task NoEvidenceRowIssuesOnlyTheBaselineQuestion()
    {
        var prompts = new List<string>();
        // Baseline answers almost entirely "bug" (option A); anything else
        // would mean a sub-question the row's lack of evidence should have skipped.
        var invoke = Fake(prompts, prompt => prompt.Contains(Tree.Baseline.Question)
            ? CompletionBody(new Dictionary<string, double> { ["A"] = 0.0, ["B"] = -10.0 })
            : throw new InvalidOperationException($"unexpected sub-question call: {prompt}"));

        var result = await Triage.RunAsync(invoke, "arn:model", RowWithoutEvidence("row-4"), Tree);

        Assert.Single(prompts);
        Assert.True(result.Verdict.Fallback);
        Assert.True(result.Verdict.Bug > 0.99, $"expected the baseline's bug answer to carry the verdict, got {result.Verdict.Bug}");
    }

    [Fact]
    public async Task ThrowsWhenEverySubQuestionFails()
    {
        var invoke = Fake([], _ => throw new AmazonServiceException("model not ready: endpoint scaled to zero"));

        var error = await Assert.ThrowsAsync<RowValidationException>(
            () => Triage.RunAsync(invoke, "arn:model", RowWithEvidence("row-5"), Tree));

        Assert.Contains("Every sub-question failed", error.Message);
    }
}
