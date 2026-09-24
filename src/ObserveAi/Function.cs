using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Runtime;

namespace ObserveAi;

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
    private static readonly Lazy<QuestionTree> LazyTree = new(QuestionTree.LoadEmbedded);

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
    public static Task<LambdaResponse> FunctionHandlerAsync(JsonElement lambdaEvent, ILambdaContext context)
    {
        var modelArn = Environment.GetEnvironmentVariable("MODEL_ARN")
            ?? throw new InvalidOperationException("MODEL_ARN environment variable is not set");
        // SEMIF_MODE picks the triage path: the tree of sub-questions (default), or
        // the single flat question it replaced, kept so the two can be compared
        // against a deployed model without shipping code.
        var mode = Environment.GetEnvironmentVariable("SEMIF_MODE") ?? "tree";
        // SEMIF_API selects the Bedrock request shape. It is an environment
        // switch rather than a constant so the chat path can be tried against a
        // deployed model without shipping code.
        var api = Environment.GetEnvironmentVariable("SEMIF_API") ?? "completion";

        return ScoreRowsAsync(lambdaEvent, LazyClient.Value, modelArn, mode, api, context);
    }

    /// <summary>
    /// The handler's body with the Bedrock client passed in, so a test can supply a
    /// fake invoker instead of one built from real AWS configuration.
    /// </summary>
    internal static async Task<LambdaResponse> ScoreRowsAsync(
        JsonElement lambdaEvent, IBedrockInvoker client, string modelArn, string mode, string api,
        ILambdaContext context)
    {
        var rows = ExtractRows(lambdaEvent);
        var results = new List<RowResultDto>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(await ScoreRowAsync(row, client, modelArn, mode, api, context).ConfigureAwait(false));
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
        JsonElement row, IBedrockInvoker client, string modelArn, string mode, string api, ILambdaContext context)
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
            if (mode == "flat")
            {
                var score = await BedrockBackend.ScoreAsync(client, modelArn, row, api: api).ConfigureAwait(false);
                return RowResultDto.FromScore(score);
            }

            var tree = LazyTree.Value;
            var result = await Triage.RunAsync(client, modelArn, row, tree).ConfigureAwait(false);
            return RowResultDto.FromTriage(rowId, result, tree);
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
        // A Bedrock model that has scaled to zero can sit there until something
        // times out; with the old MaxErrorRetry of 10 that something was the whole
        // 60 second Lambda invocation, billed in full for a request that never
        // returned. A small retry count with a per-attempt timeout well inside the
        // Lambda timeout fails the row instead, with an error the caller can act on.
        config.MaxErrorRetry = 3;
        config.Timeout = TimeSpan.FromSeconds(10);
        return new AmazonBedrockInvoker(new AmazonBedrockRuntimeClient(config));
    }
}
