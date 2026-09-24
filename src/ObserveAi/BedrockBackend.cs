using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace ObserveAi;

/// <summary>The model's own most likely next token at the first sampled position.</summary>
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
/// The narrow surface BedrockBackend needs from a Bedrock runtime client: send a
/// request body to a model and get the raw response body back. Kept separate from
/// Amazon.BedrockRuntime.IAmazonBedrockRuntime so tests can supply a small fake
/// instead of the full AWS SDK client surface.
/// </summary>
public interface IBedrockInvoker
{
    Task<byte[]> InvokeModelAsync(string modelId, byte[] requestBody, CancellationToken cancellationToken = default);
}

/// <summary>Adapts the real AWSSDK.BedrockRuntime client to <see cref="IBedrockInvoker"/>.</summary>
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
/// Direct-decision readout sourced from Bedrock Custom Model Import log probabilities.
///
/// Mirrors src/observe_ai/bedrock_backend.py: sends an invoke_model request with
/// max_tokens=1 and logprobs enabled, reads the log probability of each declared
/// option letter (A, B, ...) at the first sampled position, and softmaxes the
/// recovered values.
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
        string api = "completion",
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
        if (api is not ("completion" or "chat"))
        {
            throw new RowValidationException("api must be 'completion' or 'chat'");
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

                if (api == "completion")
                {
                    var prompt = Semif.RenderQwen3Prompt(messages);
                    promptHash = Semif.Digest(prompt);
                    writer.WriteString("prompt", prompt);
                    // The Completions schema carries the candidate count in
                    // logprobs itself, as an integer. Sending a boolean here is
                    // accepted and coerces to 1, which returns only the sampled
                    // token and no distribution to read the option letters from.
                    writer.WriteNumber("logprobs", topLogprobs);
                }
                else
                {
                    promptHash = Semif.Digest(SerializeMessagesForHash(messages));
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();
                    foreach (var message in messages)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("role", message.Role);
                        writer.WriteString("content", message.Content);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    // The Chat Completions schema splits the same request across
                    // a boolean switch and a separate count.
                    writer.WriteBoolean("logprobs", true);
                    writer.WriteNumber("top_logprobs", topLogprobs);
                }

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
    /// Maps token to log probability for the first sampled position.
    ///
    /// The two Bedrock request shapes report logprobs differently. The chat shape
    /// nests a list of {token, logprob} objects under logprobs.content[0]. The
    /// completion shape follows the older OpenAI Completions schema, where
    /// logprobs.top_logprobs is a list holding one token-to-logprob mapping per
    /// position. Both normalise to the same ordered token/logprob list here,
    /// preserving first-occurrence order and letting a later entry for the same
    /// token overwrite its value in place, matching a Python dict comprehension.
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

    private static string SerializeMessagesForHash(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        for (var i = 0; i < messages.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }
            builder.Append("{\"role\": ");
            PythonJson.WriteString(builder, messages[i].Role);
            builder.Append(", \"content\": ");
            PythonJson.WriteString(builder, messages[i].Content);
            builder.Append('}');
        }
        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>
    /// A token to log-probability map that preserves first-occurrence order,
    /// with a later entry for the same token overwriting its value without
    /// moving position, matching Python dict semantics.
    /// </summary>
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
