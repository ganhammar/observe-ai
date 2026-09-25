using System.Text.Json;
using Amazon.Runtime;

namespace ObserveAi;

/// <summary>One sub-question's outcome: either option-id-to-probability, or an error, never both.</summary>
public sealed record TriageAnswer(string Key, IReadOnlyDictionary<string, double>? Probabilities, string? Error);

/// <summary>
/// The tree's verdict for one row plus every sub-answer, so a wrong result can be traced to its signal.
/// DeclaredMass is the lowest option-letter mass across the answered sub-questions.
/// </summary>
public sealed record TriageResult(CombineResult Verdict, IReadOnlyList<TriageAnswer> Answers, double DeclaredMass)
{
    /// <summary>The yes-probability for a signal key, or 0.0 when it did not answer.</summary>
    public double YesProbability(string key) =>
        Answers.FirstOrDefault(answer => answer.Key == key)?.Probabilities?.GetValueOrDefault("yes") ?? 0.0;
}

/// <summary>
/// Runs the question tree against one row: adds derived facts to its state, scores the baseline, surface
/// and signal questions concurrently, and combines the answers into a bug-versus-downstream verdict.
/// Mirrors eval/run_tree.py, which scores sequentially; no sub-question depends on another's answer.
/// </summary>
public static class Triage
{
    public static async Task<TriageResult> RunAsync(
        IBedrockInvoker client, string modelArn, JsonElement row, QuestionTree tree,
        CancellationToken cancellationToken = default)
    {
        var id = RequireString(row, "id");
        if (!row.TryGetProperty("state", out var rawState))
        {
            throw new RowValidationException("Row is missing fields: ['state']");
        }
        var state = Derived.WithDerived(rawState);
        var hasEvidence = QuestionTree.HasEvidence(state);

        // Without evidence Combine reads only the baseline, so the other eight questions are skipped.
        var questions = hasEvidence ? SubQuestions(tree) : BaselineOnly(tree);
        var scoring = questions.Select(sub =>
            ScoreSubQuestionAsync(client, modelArn, id, state, sub.Key, sub.Question, sub.Options, cancellationToken));
        var scored = await Task.WhenAll(scoring).ConfigureAwait(false);
        var answers = scored.Select(result => result.Answer).ToList();

        var byKey = answers
            .Where(answer => answer.Probabilities is not null)
            .ToDictionary(answer => answer.Key, answer => answer.Probabilities!);

        // Combine returns a verdict even from no answers, so losing every sub-question is raised as an outage.
        if (byKey.Count == 0)
        {
            var reasons = answers.Select(answer => answer.Error).Where(e => e is not null).Distinct();
            throw new RowValidationException(
                $"Every sub-question failed, so there is no verdict: {string.Join("; ", reasons)}");
        }

        var verdict = tree.Combine(byKey, hasEvidence);
        var declaredMass = scored.Where(result => result.Answer.Probabilities is not null).Min(result => result.DeclaredMass);
        return new TriageResult(verdict, answers, declaredMass);
    }

    private static IEnumerable<(string Key, string Question, IReadOnlyList<TreeOption> Options)> BaselineOnly(
        QuestionTree tree)
    {
        yield return (tree.Baseline.Key, tree.Baseline.Question, tree.Baseline.Options);
    }

    private static IEnumerable<(string Key, string Question, IReadOnlyList<TreeOption> Options)> SubQuestions(
        QuestionTree tree)
    {
        yield return (tree.Baseline.Key, tree.Baseline.Question, tree.Baseline.Options);
        yield return (tree.Surface.Key, tree.Surface.Question, tree.Surface.Options);
        foreach (var signal in tree.Signals)
        {
            yield return (signal.Key, signal.Question, signal.Options);
        }
    }

    private static async Task<(TriageAnswer Answer, double DeclaredMass)> ScoreSubQuestionAsync(
        IBedrockInvoker client, string modelArn, string rowId, JsonElement state,
        string key, string question, IReadOnlyList<TreeOption> options, CancellationToken cancellationToken)
    {
        var subRow = BuildSubRow(rowId, key, state, question, options);
        try
        {
            var score = await BedrockBackend.ScoreAsync(client, modelArn, subRow, cancellationToken).ConfigureAwait(false);
            var probabilities = score.OptionIds
                .Zip(score.Probabilities, (optionId, probability) => (optionId, probability))
                .ToDictionary(pair => pair.optionId, pair => pair.probability);
            return (new TriageAnswer(key, probabilities, null), score.DeclaredMass);
        }
        catch (Exception error) when (error is RowValidationException or AmazonServiceException or AmazonClientException)
        {
            // A missing signal counts as 0.0 in Combine, the same as "no"; the error is kept for tracing.
            return (new TriageAnswer(key, null, $"{error.GetType().Name}: {error.Message}"), 0.0);
        }
    }

    private static JsonElement BuildSubRow(
        string rowId, string key, JsonElement state, string question, IReadOnlyList<TreeOption> options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", $"{rowId}::{key}");
            writer.WritePropertyName("state");
            state.WriteTo(writer);
            writer.WriteString("question", question);
            writer.WriteStartArray("options");
            foreach (var option in options)
            {
                writer.WriteStartObject();
                writer.WriteString("id", option.Id);
                writer.WriteString("description", option.Description);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string RequireString(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 })
        {
            throw new RowValidationException($"Row is missing fields: ['{field}']");
        }
        return value.GetString()!;
    }
}
