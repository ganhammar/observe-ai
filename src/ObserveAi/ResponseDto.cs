using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObserveAi;

/// <summary>
/// One row's result. Nullable throughout because a successful row and a failed
/// row populate disjoint sets of fields; DefaultIgnoreCondition.WhenWritingNull
/// on the serializer context drops the unused half, matching the two distinct
/// dict shapes returned by score() and by handler._score_row's error path.
/// </summary>
public sealed class RowResultDto
{
    // id is always present in the response, even when null (an unidentifiable
    // row), unlike every other field here which is entirely absent on the row
    // shape it does not belong to.
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Id { get; init; }

    [JsonPropertyName("option_ids")]
    public IReadOnlyList<string>? OptionIds { get; init; }

    [JsonPropertyName("probabilities")]
    public IReadOnlyList<double>? Probabilities { get; init; }

    [JsonPropertyName("option_logprobs")]
    public IReadOnlyList<double>? OptionLogprobs { get; init; }

    [JsonPropertyName("declared_mass")]
    public double? DeclaredMass { get; init; }

    [JsonPropertyName("missing_options")]
    public IReadOnlyList<string>? MissingOptions { get; init; }

    [JsonPropertyName("abstained")]
    public bool? Abstained { get; init; }

    [JsonPropertyName("top_token")]
    public TopToken? TopToken { get; init; }

    [JsonPropertyName("input_tokens")]
    public long? InputTokens { get; init; }

    [JsonPropertyName("total_seconds")]
    public double? TotalSeconds { get; init; }

    [JsonPropertyName("prompt_sha256")]
    public string? PromptSha256 { get; init; }

    [JsonPropertyName("prompt_version")]
    public string? PromptVersion { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    // The remaining fields are tree-mode only: the flat path never sets them.

    [JsonPropertyName("fallback")]
    public bool? Fallback { get; init; }

    [JsonPropertyName("signals")]
    public IReadOnlyDictionary<string, double>? Signals { get; init; }

    public static RowResultDto FromScore(ScoreResult score) => new()
    {
        Id = score.Id,
        OptionIds = score.OptionIds,
        Probabilities = score.Probabilities,
        OptionLogprobs = score.OptionLogprobs,
        DeclaredMass = score.DeclaredMass,
        MissingOptions = score.MissingOptions,
        Abstained = score.Abstained,
        TopToken = score.TopToken,
        InputTokens = score.InputTokens,
        TotalSeconds = score.TotalSeconds,
        PromptSha256 = score.PromptSha256,
        PromptVersion = score.PromptVersion,
    };

    public static RowResultDto FromError(string? id, string error) => new() { Id = id, Error = error };

    /// <summary>option_ids is always ["bug", "downstream"]: the tree only ever decides between the two.</summary>
    public static RowResultDto FromTriage(string? id, TriageResult result, QuestionTree tree) => new()
    {
        Id = id,
        OptionIds = ["bug", "downstream"],
        Probabilities = [result.Verdict.Bug, result.Verdict.Downstream],
        Fallback = result.Verdict.Fallback,
        Signals = tree.Signals.ToDictionary(signal => signal.Key, signal => result.YesProbability(signal.Key)),
    };
}

public sealed class LambdaResponse
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<RowResultDto> Results { get; init; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(LambdaResponse))]
[JsonSerializable(typeof(RowResultDto))]
[JsonSerializable(typeof(TopToken))]
public partial class LambdaJsonContext : JsonSerializerContext;
