using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace ObserveAi;

/// <summary>The most likely token at the first sampled position.</summary>
public sealed record TopToken(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("probability")] double Probability);

/// <summary>The full readout for one scored row.</summary>
public sealed record ScoreResult(
    string Id,
    IReadOnlyList<string> OptionIds,
    IReadOnlyList<double> Probabilities,
    IReadOnlyList<double> OptionLogprobs,
    double DeclaredMass,
    IReadOnlyList<string> MissingOptions,
    bool Abstained,
    TopToken TopToken,
    long InputTokens,
    double TotalSeconds,
    string PromptSha256,
    string PromptVersion);

/// <summary>
/// The part of a Bedrock runtime client BedrockBackend uses: send a request body to a model and return
/// the raw response body. Tests implement it with a small fake.
/// </summary>
public interface IBedrockInvoker
{
    Task<byte[]> InvokeModelAsync(string modelId, byte[] requestBody, CancellationToken cancellationToken = default);
}

/// <summary>Adapts the AWSSDK.BedrockRuntime client to <see cref="IBedrockInvoker"/>.</summary>
public sealed class AmazonBedrockInvoker(IAmazonBedrockRuntime client) : IBedrockInvoker
{
    public async Task<byte[]> InvokeModelAsync(
        string modelId, byte[] requestBody, CancellationToken cancellationToken = default)
    {
        using var bodyStream = new MemoryStream(requestBody);
        var request = new InvokeModelRequest
        {
            ModelId = modelId,
            Body = bodyStream,
            ContentType = "application/json",
            Accept = "application/json",
        };
        var response = await client.InvokeModelAsync(request, cancellationToken).ConfigureAwait(false);
        using var responseStream = response.Body;
        using var buffer = new MemoryStream();
        await responseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}

/// <summary>
/// Direct-decision readout from Bedrock Custom Model Import log probabilities, mirroring
/// src/observe_ai/bedrock_backend.py. Requests one token with logprobs, reads each option letter's log
/// probability at the first sampled position, and softmaxes them.
/// </summary>
public static class BedrockBackend
{
    public const string PromptVersion = "bedrock-direct-v1";

    public static async Task<ScoreResult> ScoreAsync(
        IBedrockInvoker client,
        string modelArn,
        JsonElement row,
        int topLogprobs = 20,
        bool constrain = true,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Semif.ValidateRow(row);
        var options = row.GetProperty("options");
        var optionCount = options.GetArrayLength();
        var rowId = row.GetProperty("id").GetString()!;

        if (optionCount > topLogprobs)
        {
            throw new RowValidationException(
                $"Row {rowId}: {optionCount} options exceed top_logprobs={topLogprobs}; " +
                "not enough candidates could possibly be returned to cover every option letter");
        }

        var messages = Semif.DirectMessages(row);
        var letters = Semif.Letters[..optionCount];

        string promptHash;
        byte[] requestBody;
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("max_tokens", 1);
                writer.WriteNumber("temperature", 0);

                var prompt = Semif.RenderQwen3Prompt(messages);
                promptHash = Semif.Digest(prompt);
                writer.WriteString("prompt", prompt);
                // logprobs is the candidate count as an integer. A boolean is accepted but coerces to 1, returning no distribution.
                writer.WriteNumber("logprobs", topLogprobs);

                if (constrain)
                {
                    writer.WritePropertyName("structured_outputs");
                    writer.WriteStartObject();
                    writer.WritePropertyName("choice");
                    writer.WriteStartArray();
                    foreach (var letter in letters)
                    {
                        writer.WriteStringValue(letter.ToString());
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }
            requestBody = stream.ToArray();
        }

        var responseBytes = await client.InvokeModelAsync(modelArn, requestBody, cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(responseBytes);
        var payload = document.RootElement;

        var byLetter = FirstPositionLogprobs(payload);
        if (byLetter.Count == 0)
        {
            throw new RowValidationException($"Row {rowId}: response returned no candidate tokens in top_logprobs");
        }

        var optionIds = new List<string>();
        var optionLogprobs = new List<double>();
        var missingOptions = new List<string>();
        var index = 0;
        foreach (var option in options.EnumerateArray())
        {
            var optionId = option.GetProperty("id").GetString()!;
            optionIds.Add(optionId);
            var letter = letters[index].ToString();
            if (TryGetLogprob(byLetter, letter, out var logprob))
            {
                optionLogprobs.Add(logprob);
            }
            else
            {
                missingOptions.Add(optionId);
                optionLogprobs.Add(double.NegativeInfinity);
            }
            index++;
        }

        var declaredMass = optionLogprobs.Where(double.IsFinite).Sum(Math.Exp);
        var probabilities = SoftmaxAllowMissing(optionLogprobs);
        var abstained = missingOptions.Count == optionCount;

        string? topTokenName = null;
        var topTokenLogprob = double.NegativeInfinity;
        foreach (var entry in byLetter)
        {
            if (entry.Value > topTokenLogprob)
            {
                topTokenLogprob = entry.Value;
                topTokenName = entry.Key;
            }
        }
        var topToken = new TopToken(topTokenName!, Math.Exp(topTokenLogprob));

        var inputTokens = payload.GetProperty("usage").GetProperty("prompt_tokens").GetInt64();

        return new ScoreResult(
            rowId,
            optionIds,
            probabilities,
            optionLogprobs,
            declaredMass,
            missingOptions,
            abstained,
            topToken,
            inputTokens,
            stopwatch.Elapsed.TotalSeconds,
            promptHash,
            PromptVersion);
    }

    private static double[] SoftmaxAllowMissing(IReadOnlyList<double> logprobs)
    {
        var finite = logprobs.Where(double.IsFinite).ToArray();
        if (finite.Length == 0)
        {
            return new double[logprobs.Count];
        }
        if (finite.Length == 1)
        {
            return logprobs.Select(value => double.IsFinite(value) ? 1.0 : 0.0).ToArray();
        }
        var finiteProbabilities = new Queue<double>(Semif.Softmax(finite));
        return logprobs.Select(value => double.IsFinite(value) ? finiteProbabilities.Dequeue() : 0.0).ToArray();
    }

    /// <summary>
    /// Maps token to log probability at the first sampled position. The chat shape nests {token, logprob}
    /// objects under logprobs.content[0].top_logprobs. The completion shape follows the OpenAI Completions
    /// schema: logprobs.top_logprobs holds one token-to-logprob map per position.
    /// </summary>
    private static List<KeyValuePair<string, double>> FirstPositionLogprobs(JsonElement payload)
    {
        var choice = payload.GetProperty("choices")[0];
        if (!choice.TryGetProperty("logprobs", out var logprobs) || IsFalsy(logprobs))
        {
            throw new RowValidationException(
                "Response carries no logprobs; confirm logprobs and top_logprobs were accepted");
        }

        if (logprobs.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0)
        {
            var ordered = new OrderedTokenLogprobs();
            foreach (var entry in content[0].GetProperty("top_logprobs").EnumerateArray())
            {
                ordered.Set(entry.GetProperty("token").GetString()!, entry.GetProperty("logprob").GetDouble());
            }
            return ordered.Entries;
        }

        if (logprobs.TryGetProperty("top_logprobs", out var positions)
            && positions.ValueKind == JsonValueKind.Array && positions.GetArrayLength() > 0)
        {
            var ordered = new OrderedTokenLogprobs();
            foreach (var property in positions[0].EnumerateObject())
            {
                ordered.Set(property.Name, property.Value.GetDouble());
            }
            return ordered.Entries;
        }

        var keys = logprobs.ValueKind == JsonValueKind.Object
            ? string.Join(", ", logprobs.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal))
            : string.Empty;
        throw new RowValidationException($"Unrecognised logprobs shape: [{keys}]");
    }

    private static bool TryGetLogprob(List<KeyValuePair<string, double>> byLetter, string letter, out double logprob)
    {
        foreach (var entry in byLetter)
        {
            if (entry.Key == letter)
            {
                logprob = entry.Value;
                return true;
            }
        }
        logprob = 0;
        return false;
    }

    private static bool IsFalsy(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.False => true,
        JsonValueKind.Object => !element.EnumerateObject().Any(),
        JsonValueKind.Array => element.GetArrayLength() == 0,
        JsonValueKind.String => element.GetString()!.Length == 0,
        JsonValueKind.Number => element.GetDouble() == 0,
        _ => false,
    };

    /// <summary>A token-to-logprob map with Python dict ordering: insertion order is kept and an overwrite stays in place.</summary>
    private sealed class OrderedTokenLogprobs
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        public List<KeyValuePair<string, double>> Entries { get; } = [];

        public void Set(string token, double logprob)
        {
            if (_index.TryGetValue(token, out var position))
            {
                Entries[position] = new KeyValuePair<string, double>(token, logprob);
            }
            else
            {
                _index[token] = Entries.Count;
                Entries.Add(new KeyValuePair<string, double>(token, logprob));
            }
        }
    }
}
