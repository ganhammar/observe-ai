using System.Text.Json;
using Amazon.Runtime;

namespace ObserveAi;

/// <summary>One sub-question's outcome: either option-id-to-probability, or an error, never both.</summary>
public sealed record TriageAnswer(string Key, IReadOnlyDictionary<string, double>? Probabilities, string? Error);

/// <summary>
/// The tree's verdict for one row, plus every sub-answer it was built from, so a
/// wrong result can be traced to a wrong signal rather than a wrong rule.
/// </summary>
public sealed record TriageResult(CombineResult Verdict, IReadOnlyList<TriageAnswer> Answers)
{
    /// <summary>The yes-probability for a signal key, or 0.0 when it did not answer.</summary>
    public double YesProbability(string key) =>
        Answers.FirstOrDefault(answer => answer.Key == key)?.Probabilities?.GetValueOrDefault("yes") ?? 0.0;
}

/// <summary>
/// Runs the question tree against one row: enrich its state with derived facts,
/// score the baseline, the surface question, and every signal, then combine the
/// typed answers into a bug-versus-downstream verdict.
///
/// Mirrors eval/run_tree.py, except the sub-questions are scored concurrently
/// rather than in a loop. The Python runner is a batch script with no reason to
/// wait on one round trip before starting the next; a live Lambda pays for that
/// wait directly, and nothing here depends on one sub-answer to ask another.
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

        var scoring = SubQuestions(tree).Select(sub =>
            ScoreSubQuestionAsync(client, modelArn, id, state, sub.Key, sub.Question, sub.Options, cancellationToken));
        var answers = await Task.WhenAll(scoring).ConfigureAwait(false);

        var byKey = answers
            .Where(answer => answer.Probabilities is not null)
            .ToDictionary(answer => answer.Key, answer => answer.Probabilities!);
        var verdict = tree.Combine(byKey, QuestionTree.HasEvidence(state));
        return new TriageResult(verdict, answers);
    }

    /// <summary>The tree's individual decision rows: baseline, surface, then every signal.</summary>
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

    private static async Task<TriageAnswer> ScoreSubQuestionAsync(
        IBedrockInvoker client, string modelArn, string rowId, JsonElement state,
        string key, string question, IReadOnlyList<TreeOption> options, CancellationToken cancellationToken)
    {
        var subRow = BuildSubRow(rowId, key, state, question, options);
        try
        {
            // constrain: false matches how the sub-questions were measured.
            var score = await BedrockBackend.ScoreAsync(
                client, modelArn, subRow, constrain: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            var probabilities = score.OptionIds
                .Zip(score.Probabilities, (optionId, probability) => (optionId, probability))
                .ToDictionary(pair => pair.optionId, pair => pair.probability);
            return new TriageAnswer(key, probabilities, null);
        }
        catch (Exception error) when (error is RowValidationException or AmazonServiceException or AmazonClientException)
        {
            // A missing signal contributes 0.0 to Combine, same as an explicit "no".
            // The error is kept here purely for tracing.
            return new TriageAnswer(key, null, $"{error.GetType().Name}: {error.Message}");
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
