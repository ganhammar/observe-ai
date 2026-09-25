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

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("fallback")]
    public bool? Fallback { get; init; }

    [JsonPropertyName("signals")]
    public IReadOnlyDictionary<string, double>? Signals { get; init; }

    public static RowResultDto FromError(string? id, string error) => new() { Id = id, Error = error };

    public static RowResultDto FromTriage(string? id, TriageResult result, QuestionTree tree) => new()
    {
        Id = id,
        OptionIds = ["bug", "downstream"],
        Probabilities = [result.Verdict.Bug, result.Verdict.Downstream],
        Fallback = result.Verdict.Fallback,
        Signals = tree.Signals
            .Where(signal => result.Answers.Any(answer => answer.Key == signal.Key && answer.Probabilities is not null))
            .ToDictionary(signal => signal.Key, signal => result.YesProbability(signal.Key)),
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
/// The response for every action. Each action sets only its own fields: Results (triage), Escalate
/// (escalate), Allowed/Tripped/Count/Limit (check-rate), Started/AlreadyKnown/Unparseable/NoRepo (Kinesis
/// batch) and Outcome/IssueNumber (file-issue). Fields stay flat because the state machine reads them off
/// each task's ResultPath, as in $.rate.tripped and $.verdict.results[0].
/// </summary>
public sealed class LambdaResponse
{
    [JsonPropertyName("results")]
    public IReadOnlyList<RowResultDto>? Results { get; init; }

    [JsonPropertyName("escalate")]
    public EscalateResultDto? Escalate { get; init; }

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
[JsonSerializable(typeof(EscalateResultDto))]
[JsonSerializable(typeof(ExecutionInput))]
public partial class LambdaJsonContext : JsonSerializerContext;
