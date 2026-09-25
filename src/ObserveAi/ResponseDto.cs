using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObserveAi;

/// <summary>
/// One row's result. A scored row and a rejected row set disjoint fields, and WhenWritingNull on the
/// serializer context omits whichever are unset.
/// </summary>
public sealed class RowResultDto
{
    // id is always written, as null for an unidentifiable row.
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

    // Set in tree mode only.

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

    public static RowResultDto FromTriage(string? id, TriageResult result, QuestionTree tree) => new()
    {
        Id = id,
        OptionIds = ["bug", "downstream"],
        Probabilities = [result.Verdict.Bug, result.Verdict.Downstream],
        Fallback = result.Verdict.Fallback,
        Signals = tree.Signals.ToDictionary(signal => signal.Key, signal => result.YesProbability(signal.Key)),
    };
}

/// <summary>identify's result. When Parsed is false, only Error, ResourceKind and ResourceName are set.</summary>
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
/// The response for every action. Each action sets only its own fields: Results (triage), Identify
/// (identify), Escalate (escalate), Repo/ResolvedBy (resolve-repo), Allowed/Tripped/Count/Limit
/// (check-rate), Started/AlreadyKnown/Unparseable/NoRepo (Kinesis batch) and Outcome/IssueNumber
/// (file-issue). Fields stay flat because the state machine reads them off each task's ResultPath, as in
/// $.repo.repo, $.rate.tripped and $.verdict.results[0].
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

    [JsonPropertyName("alreadyKnown")]
    public long? AlreadyKnown { get; init; }

    [JsonPropertyName("unparseable")]
    public long? Unparseable { get; init; }

    [JsonPropertyName("noRepo")]
    public long? NoRepo { get; init; }

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
