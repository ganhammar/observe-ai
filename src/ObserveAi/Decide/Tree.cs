using System.Text.Json;

namespace ObserveAi;

public sealed record TreeOption(string Id, string Description);

public sealed record TreeQuestion(string Key, string Question, IReadOnlyList<TreeOption> Options);

public sealed record TreeSignal(
    string Key, string Side, string Question, IReadOnlyList<TreeOption> Options, string? DampenedBy);

/// <summary>The combined bug-versus-downstream verdict for one triage row.</summary>
public sealed record CombineResult(double Bug, double Downstream, bool Fallback);

/// <summary>
/// The question and signal definitions from tree.json and the rule combining their answers into a
/// bug-versus-downstream probability. Mirrors eval/tree.py's BASELINE, SURFACE, BUG_SIGNALS,
/// DOWNSTREAM_SIGNALS, PRIOR and combine().
/// </summary>
public sealed class QuestionTree
{
    private const string EmbeddedResourceName = "ObserveAi.tree.json";

    public double Prior { get; }
    public TreeQuestion Baseline { get; }
    public TreeQuestion Surface { get; }
    public IReadOnlyList<TreeSignal> Signals { get; }

    private QuestionTree(double prior, TreeQuestion baseline, TreeQuestion surface, IReadOnlyList<TreeSignal> signals)
    {
        Prior = prior;
        Baseline = baseline;
        Surface = surface;
        Signals = signals;
    }

    public static QuestionTree LoadEmbedded()
    {
        var assembly = typeof(QuestionTree).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedResourceName}' was not found in {assembly.FullName}");
        using var document = JsonDocument.Parse(stream);
        return Parse(document.RootElement);
    }

    public static QuestionTree Parse(JsonElement root)
    {
        var prior = root.GetProperty("prior").GetDouble();
        var baseline = ParseQuestion(root.GetProperty("baseline"));
        var surface = ParseQuestion(root.GetProperty("surface"));
        var signals = root.GetProperty("signals").EnumerateArray().Select(ParseSignal).ToList();
        return new QuestionTree(prior, baseline, surface, signals);
    }

    /// <summary>
    /// Combines typed answers, keyed by question then option id, into a bug-versus-downstream probability.
    /// A noisy-OR per side treats signals as independent, so one confident signal decides and weak ones
    /// accumulate. Prior is added to both sides before normalising; without it a lone 0.92 signal
    /// normalises to 1.0.
    /// </summary>
    public CombineResult Combine(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> answers, bool evidencePresent = true)
    {
        if (!evidencePresent)
        {
            // With no evidence, signals can only echo the stack trace, which the baseline reads better.
            var bugP = GetAnswerValue(answers, Baseline.Key, "bug", 0.5);
            return new CombineResult(bugP, 1.0 - bugP, true);
        }

        var externalChange = Yes(answers, "external_change");
        var bugValues = new List<double>();
        foreach (var signal in Signals.Where(signal => signal.Side == "bug"))
        {
            var value = Yes(answers, signal.Key);
            if (signal.DampenedBy is { } dampener)
            {
                // Sending a value the other side rejects is ours only if the other side's contract did not just change.
                value *= 1.0 - Yes(answers, dampener);
            }
            bugValues.Add(value);
        }
        var bug = NoisyOr(bugValues);
        var downstream = NoisyOr(Signals.Where(signal => signal.Side == "downstream").Select(signal => Yes(answers, signal.Key)));

        var total = bug + downstream;
        if (total == 0.0)
        {
            // Nothing fired: a failure surfacing on a call out is more often the other side's.
            var network = GetAnswerValue(answers, Surface.Key, "network", 0.0);
            return new CombineResult(1.0 - network, network, true);
        }

        return new CombineResult(
            (bug + Prior) / (bug + downstream + 2 * Prior),
            (downstream + Prior) / (bug + downstream + 2 * Prior),
            false);
    }

    /// <summary>True when the state has evidence or derived_facts for the signal questions to read.</summary>
    public static bool HasEvidence(JsonElement state) =>
        (state.TryGetProperty("evidence", out var evidence)
            && evidence.ValueKind == JsonValueKind.Object && evidence.EnumerateObject().Any())
        || (state.TryGetProperty("derived_facts", out var derivedFacts)
            && derivedFacts.ValueKind == JsonValueKind.Array && derivedFacts.GetArrayLength() > 0);

    private static double Yes(IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> answers, string key) =>
        GetAnswerValue(answers, key, "yes", 0.0);

    private static double GetAnswerValue(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> answers,
        string questionKey, string optionId, double fallback) =>
        answers.TryGetValue(questionKey, out var answer) && answer.TryGetValue(optionId, out var value)
            ? value
            : fallback;

    private static double NoisyOr(IEnumerable<double> values)
    {
        var product = 1.0;
        foreach (var value in values)
        {
            product *= 1.0 - value;
        }
        return 1.0 - product;
    }

    private static TreeQuestion ParseQuestion(JsonElement element) => new(
        element.GetProperty("key").GetString()!,
        element.GetProperty("question").GetString()!,
        ParseOptions(element.GetProperty("options")));

    private static TreeSignal ParseSignal(JsonElement element) => new(
        element.GetProperty("key").GetString()!,
        element.GetProperty("side").GetString()!,
        element.GetProperty("question").GetString()!,
        ParseOptions(element.GetProperty("options")),
        element.TryGetProperty("dampened_by", out var dampenedBy) ? dampenedBy.GetString() : null);

    private static IReadOnlyList<TreeOption> ParseOptions(JsonElement options) => options.EnumerateArray()
        .Select(option => new TreeOption(
            option.GetProperty("id").GetString()!, option.GetProperty("description").GetString()!))
        .ToList();
}
