using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Runtime;

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

/// <summary>
/// AWS Lambda entrypoint scoring SemIf rows against Bedrock Custom Model Import.
///
/// Mirrors src/observe_ai/handler.py. The Bedrock client is created lazily on
/// first invocation and reused across warm invocations, since constructing it on
/// every call would be wasted work in a warm container.
/// </summary>
public static class Function
{
    private static readonly Lazy<IBedrockInvoker> LazyClient = new(CreateClient);

    public static async Task Main()
    {
        var serializer = new SourceGeneratorLambdaJsonSerializer<LambdaJsonContext>();
        using var handlerWrapper = HandlerWrapper.GetHandlerWrapper<JsonElement, LambdaResponse>(
            FunctionHandlerAsync, serializer);
        using var bootstrap = new LambdaBootstrap(handlerWrapper);
        await bootstrap.RunAsync();
    }

    /// <summary>
    /// Scores a single SemIf row or a {"rows": [...]} batch, returning {"results": [...]}.
    ///
    /// Takes an already-parsed event: a direct Lambda invoke, or a consumer
    /// reading rows off a queue such as SQS. No API Gateway / HTTP body parsing
    /// is implemented here.
    /// </summary>
    public static async Task<LambdaResponse> FunctionHandlerAsync(JsonElement lambdaEvent, ILambdaContext context)
    {
        var modelArn = Environment.GetEnvironmentVariable("MODEL_ARN")
            ?? throw new InvalidOperationException("MODEL_ARN environment variable is not set");
        // SEMIF_API selects the Bedrock request shape. It is an environment
        // switch rather than a constant so the chat path can be tried against a
        // deployed model without shipping code.
        var api = Environment.GetEnvironmentVariable("SEMIF_API") ?? "completion";

        var rows = ExtractRows(lambdaEvent);
        var results = new List<RowResultDto>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(await ScoreRowAsync(row, modelArn, api, context).ConfigureAwait(false));
        }

        var failures = results.Count(result => result.Error is not null);
        context.Logger.LogInformation($"Scored {results.Count} rows, {failures} rejected");

        return new LambdaResponse { Results = results };
    }

    private static List<JsonElement> ExtractRows(JsonElement lambdaEvent)
    {
        if (lambdaEvent.ValueKind == JsonValueKind.Object
            && lambdaEvent.TryGetProperty("rows", out var rows)
            && rows.ValueKind == JsonValueKind.Array)
        {
            return [.. rows.EnumerateArray()];
        }
        return [lambdaEvent];
    }

    private static async Task<RowResultDto> ScoreRowAsync(
        JsonElement row, string modelArn, string api, ILambdaContext context)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            var kind = DescribeKind(row);
            context.Logger.LogWarning($"Row rejected: expected an object, got {kind}");
            return RowResultDto.FromError(null, $"Row must be a JSON object, got {kind}");
        }

        string? rowId = row.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String
            ? idProperty.GetString()
            : null;

        try
        {
            var score = await BedrockBackend.ScoreAsync(LazyClient.Value, modelArn, row, api: api)
                .ConfigureAwait(false);
            return RowResultDto.FromScore(score);
        }
        catch (Exception error) when (error is RowValidationException or AmazonServiceException or AmazonClientException)
        {
            context.Logger.LogWarning($"Row {rowId} rejected: {error.GetType().Name}: {error.Message}");
            return RowResultDto.FromError(rowId, $"{error.GetType().Name}: {error.Message}");
        }
    }

    private static string DescribeKind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => "str",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Array => "list",
        JsonValueKind.Null or JsonValueKind.Undefined => "NoneType",
        _ => element.ValueKind.ToString(),
    };

    private static IBedrockInvoker CreateClient()
    {
        var region = Environment.GetEnvironmentVariable("BEDROCK_REGION")
            ?? throw new InvalidOperationException("BEDROCK_REGION environment variable is not set");
        var config = new AmazonBedrockRuntimeConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
        };
        config.RetryMode = RequestRetryMode.Standard;
        config.MaxErrorRetry = 10;
        return new AmazonBedrockInvoker(new AmazonBedrockRuntimeClient(config));
    }
}
