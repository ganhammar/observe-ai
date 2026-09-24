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

/// <summary>identify's result. Only Error is set when Parsed is false; every other field is only set when it is true.</summary>
public sealed class IdentifyResultDto
{
    [JsonPropertyName("parsed")]
    public required bool Parsed { get; init; }

    [JsonPropertyName("runtime")]
    public string? Runtime { get; init; }

    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; init; }

    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    [JsonPropertyName("signature")]
    public string? Signature { get; init; }

    [JsonPropertyName("namespacePrefix")]
    public string? NamespacePrefix { get; init; }

    [JsonPropertyName("resourceKind")]
    public string? ResourceKind { get; init; }

    [JsonPropertyName("resourceName")]
    public string? ResourceName { get; init; }

    [JsonPropertyName("frames")]
    public IReadOnlyList<Frame>? Frames { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    public static IdentifyResultDto From(IdentifyResult result) => new()
    {
        Parsed = result.Parsed,
        Runtime = result.Runtime,
        ExceptionType = result.ExceptionType,
        Fingerprint = result.Fingerprint,
        Signature = result.Signature,
        NamespacePrefix = result.NamespacePrefix,
        ResourceKind = result.ResourceKind?.ToString(),
        ResourceName = result.ResourceName,
        Frames = result.Frames,
        Error = result.Error,
    };
}

/// <summary>escalate's result: what to fetch, what it would have verified, and the issue drafted from both.</summary>
public sealed class EscalateResultDto
{
    [JsonPropertyName("fetchRequests")]
    public required IReadOnlyList<SourceFetchRequest> FetchRequests { get; init; }

    [JsonPropertyName("frameVerdicts")]
    public required IReadOnlyList<FrameVerdict> FrameVerdicts { get; init; }

    [JsonPropertyName("draft")]
    public required Draft Draft { get; init; }

    public static EscalateResultDto From(EscalateResult result) => new()
    {
        FetchRequests = result.FetchRequests,
        FrameVerdicts = result.FrameVerdicts,
        Draft = result.Draft,
    };
}

/// <summary>
/// The Lambda response shape. Only Results is set for triage, keeping that
/// action's wire format exactly as it was before identify and escalate existed;
/// Identify and Escalate are each set only by their own action. Repo/ResolvedBy
/// (resolve-repo), Allowed/Tripped/Count/Limit (check-rate), Started
/// (start-executions), and Outcome/IssueNumber (file-issue) stay flat here
/// rather than nested under their own key, because the state machine reads
/// them straight off the ResultPath it assigns their task to (for example
/// $.repo.repo, $.rate.tripped), the same way $.verdict.results[0] reads
/// Results.
/// </summary>
public sealed class LambdaResponse
{
    [JsonPropertyName("results")]
    public IReadOnlyList<RowResultDto>? Results { get; init; }

    [JsonPropertyName("identify")]
    public IdentifyResultDto? Identify { get; init; }

    [JsonPropertyName("escalate")]
    public EscalateResultDto? Escalate { get; init; }

    [JsonPropertyName("repo")]
    public string? Repo { get; init; }

    [JsonPropertyName("resolvedBy")]
    public string? ResolvedBy { get; init; }

    [JsonPropertyName("allowed")]
    public bool? Allowed { get; init; }

    [JsonPropertyName("tripped")]
    public bool? Tripped { get; init; }

    [JsonPropertyName("count")]
    public long? Count { get; init; }

    [JsonPropertyName("limit")]
    public long? Limit { get; init; }

    [JsonPropertyName("started")]
    public long? Started { get; init; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }

    [JsonPropertyName("issueNumber")]
    public long? IssueNumber { get; init; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(LambdaResponse))]
[JsonSerializable(typeof(RowResultDto))]
[JsonSerializable(typeof(TopToken))]
[JsonSerializable(typeof(IdentifyResultDto))]
[JsonSerializable(typeof(EscalateResultDto))]
[JsonSerializable(typeof(ExecutionInput))]
public partial class LambdaJsonContext : JsonSerializerContext;
