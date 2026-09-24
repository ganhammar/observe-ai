using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Exercises Triage.RunAsync against a fake IBedrockInvoker: that it issues all
/// nine of the tree's sub-questions, that they run concurrently rather than one
/// after another, that one failing sub-question does not fail the row, and that a
/// row with no evidence routes to the baseline answer.
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

    private sealed class FakeInvoker : IBedrockInvoker
    {
        private readonly object _gate = new();
        private int _inFlight;

        public int MaxInFlight { get; private set; }
        public List<string> Prompts { get; } = [];
        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(30);
        public Func<string, byte[]>? RespondTo { get; set; }
        public HashSet<string> FailWhenPromptContains { get; } = [];

        public async Task<byte[]> InvokeModelAsync(
            string modelId, byte[] requestBody, CancellationToken cancellationToken = default)
        {
            var prompt = JsonDocument.Parse(requestBody).RootElement.GetProperty("prompt").GetString()!;
            lock (_gate)
            {
                Prompts.Add(prompt);
            }

            var current = Interlocked.Increment(ref _inFlight);
            lock (_gate)
            {
                MaxInFlight = Math.Max(MaxInFlight, current);
            }
            await Task.Delay(Delay, cancellationToken);
            Interlocked.Decrement(ref _inFlight);

            if (FailWhenPromptContains.Any(prompt.Contains))
            {
                throw new AmazonServiceException("model not ready: endpoint scaled to zero");
            }
            return RespondTo?.Invoke(prompt) ?? DefaultResponse();
        }
    }

    private static readonly string[] ExpectedKeys =
    [
        "baseline", "surface", "unreleased_resource", "self_inflicted_load", "invalid_value_sent",
        "internal_inconsistency", "repeated_work", "external_change", "external_unavailable",
    ];

    [Fact]
    public async Task IssuesAllNineSubQuestions()
    {
        var fake = new FakeInvoker();

        var result = await Triage.RunAsync(fake, "arn:model", RowWithEvidence("row-1"), Tree);

        Assert.Equal(9, fake.Prompts.Count);
        Assert.Equal(ExpectedKeys.OrderBy(k => k), result.Answers.Select(a => a.Key).OrderBy(k => k));
        Assert.All(result.Answers, answer => Assert.Null(answer.Error));
    }

    [Fact]
    public async Task SubQuestionsRunConcurrentlyNotSequentially()
    {
        // A constrained thread pool can delay how quickly nine queued Task.Delay
        // continuations get serviced, which would make this flaky on a wall-clock
        // threshold alone; raising the minimum thread count removes that noise so
        // MaxInFlight reflects actual concurrency rather than scheduling lag.
        ThreadPool.SetMinThreads(32, 32);
        var fake = new FakeInvoker { Delay = TimeSpan.FromMilliseconds(150) };
        var stopwatch = Stopwatch.StartNew();

        await Triage.RunAsync(fake, "arn:model", RowWithEvidence("row-2"), Tree);

        stopwatch.Stop();
        // Nine sequential 150ms round trips would take ~1350ms; concurrent ones
        // should finish in roughly one round trip's worth of time.
        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"expected concurrent scoring to finish well under 1000ms, took {stopwatch.ElapsedMilliseconds}ms");
        Assert.True(fake.MaxInFlight >= 5, $"expected several sub-questions in flight at once, saw {fake.MaxInFlight}");
    }

    [Fact]
    public async Task OneFailingSubQuestionDoesNotFailTheRow()
    {
        var fake = new FakeInvoker();
        fake.FailWhenPromptContains.Add(Tree.Signals.Single(s => s.Key == "repeated_work").Question);

        var result = await Triage.RunAsync(fake, "arn:model", RowWithEvidence("row-3"), Tree);

        Assert.Equal(9, result.Answers.Count);
        var failed = result.Answers.Single(a => a.Key == "repeated_work");
        Assert.NotNull(failed.Error);
        Assert.Null(failed.Probabilities);
        Assert.All(result.Answers.Where(a => a.Key != "repeated_work"), answer => Assert.Null(answer.Error));
        // Combine() still runs on whatever came back rather than throwing.
        Assert.True(result.Verdict.Bug is >= 0.0 and <= 1.0);
    }

    [Fact]
    public async Task NoEvidenceRowRoutesToBaseline()
    {
        var fake = new FakeInvoker
        {
            // Baseline answers almost entirely "bug" (option A); every other
            // sub-question gets the generic response, which must be ignored.
            RespondTo = prompt => prompt.Contains(Tree.Baseline.Question)
                ? CompletionBody(new Dictionary<string, double> { ["A"] = 0.0, ["B"] = -10.0 })
                : DefaultResponse(),
        };

        var result = await Triage.RunAsync(fake, "arn:model", RowWithoutEvidence("row-4"), Tree);

        Assert.True(result.Verdict.Fallback);
        Assert.True(result.Verdict.Bug > 0.99, $"expected the baseline's bug answer to carry the verdict, got {result.Verdict.Bug}");
    }
}
