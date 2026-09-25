using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObserveAi;

/// <summary>
/// The response for every action. Each action sets only its own fields:
/// OptionIds/Probabilities/Fallback/Signals/DeclaredMass (triage), Escalate (escalate),
/// Allowed/Tripped/Count/Limit (check-rate), Started/AlreadyKnown/Unparseable/NoRepo (Kinesis batch) and
/// Outcome/IssueNumber (file-issue). Fields stay flat because the state machine reads them off each task's
/// ResultPath, as in $.rate.tripped and $.verdict.probabilities[0].
/// </summary>
public sealed class LambdaResponse
{
    [JsonPropertyName("option_ids")]
    public IReadOnlyList<string>? OptionIds { get; init; }

    [JsonPropertyName("probabilities")]
    public IReadOnlyList<double>? Probabilities { get; init; }

    [JsonPropertyName("fallback")]
    public bool? Fallback { get; init; }

    /// <summary>The yes-probability of each signal that answered.</summary>
    [JsonPropertyName("signals")]
    public IReadOnlyDictionary<string, double>? Signals { get; init; }

    [JsonPropertyName("declared_mass")]
    public double? DeclaredMass { get; init; }

    [JsonPropertyName("escalate")]
    public EscalateResult? Escalate { get; init; }

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

    public static LambdaResponse FromTriage(TriageResult result, QuestionTree tree) => new()
    {
        OptionIds = ["bug", "external"],
        Probabilities = [result.Verdict.Bug, result.Verdict.External],
        Fallback = result.Verdict.Fallback,
        Signals = tree.Signals
            .Where(signal => result.Answers.Any(answer => answer.Key == signal.Key && answer.Probabilities is not null))
            .ToDictionary(signal => signal.Key, signal => result.YesProbability(signal.Key)),
        DeclaredMass = result.DeclaredMass,
    };
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(LambdaResponse))]
[JsonSerializable(typeof(ExecutionInput))]
public partial class LambdaJsonContext : JsonSerializerContext;
