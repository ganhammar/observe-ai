using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace ObserveAi;

/// <summary>
/// One scored row: option ids with their probabilities, and the probability mass the model put on the
/// option letters at all. A low DeclaredMass means the letters were a thin tail of the distribution.
/// </summary>
public sealed record ScoreResult(IReadOnlyList<string> OptionIds, IReadOnlyList<double> Probabilities, double DeclaredMass);

/// <summary>
/// Direct-decision readout from Bedrock Custom Model Import log probabilities, mirroring
/// src/observe_ai/bedrock_backend.py. Requests one token with logprobs, reads each option letter's log
/// probability at the first sampled position, and softmaxes them.
/// </summary>
public static class BedrockBackend
{
    /// <summary>Sends a request body to a model and returns the raw response body.</summary>
    public delegate Task<byte[]> Invoke(string modelId, byte[] body, CancellationToken cancellationToken);

    public static Invoke Against(IAmazonBedrockRuntime client) => async (modelId, body, cancellationToken) =>
    {
        using var bodyStream = new MemoryStream(body);
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
    };

    // Candidates per position. Semif.ValidateRow caps a row at 16 options, so every letter can appear.
    private const int TopLogprobs = 20;

    public static async Task<ScoreResult> ScoreAsync(
        Invoke invoke, string modelArn, JsonElement row, CancellationToken cancellationToken = default)
    {
        var messages = Semif.DirectMessages(row);
        var options = row.GetProperty("options");
        var rowId = row.GetProperty("id").GetString()!;
        var letters = Semif.Letters[..options.GetArrayLength()];

        byte[] requestBody;
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("max_tokens", 1);
                writer.WriteNumber("temperature", 0);
                writer.WriteString("prompt", Semif.RenderQwen3Prompt(messages));
                // logprobs is the candidate count as an integer. A boolean is accepted but coerces to 1, returning no distribution.
                writer.WriteNumber("logprobs", TopLogprobs);
                writer.WriteEndObject();
            }
            requestBody = stream.ToArray();
        }

        var responseBytes = await invoke(modelArn, requestBody, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(responseBytes);

        var byLetter = FirstPositionLogprobs(document.RootElement);
        if (byLetter.Count == 0)
        {
            throw new RowValidationException($"Row {rowId}: response returned no candidate tokens in top_logprobs");
        }

        var optionIds = new List<string>();
        var optionLogprobs = new List<double>();
        var index = 0;
        foreach (var option in options.EnumerateArray())
        {
            optionIds.Add(option.GetProperty("id").GetString()!);
            optionLogprobs.Add(byLetter.TryGetValue(letters[index].ToString(), out var logprob) ? logprob : double.NegativeInfinity);
            index++;
        }

        var declaredMass = optionLogprobs.Where(double.IsFinite).Sum(Math.Exp);
        return new ScoreResult(optionIds, SoftmaxAllowMissing(optionLogprobs), declaredMass);
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
    /// Maps token to log probability at the first sampled position. The response follows the OpenAI
    /// Completions schema: logprobs.top_logprobs holds one token-to-logprob map per position.
    /// </summary>
    private static Dictionary<string, double> FirstPositionLogprobs(JsonElement payload)
    {
        var choice = payload.GetProperty("choices")[0];
        if (!choice.TryGetProperty("logprobs", out var logprobs) || logprobs.ValueKind != JsonValueKind.Object)
        {
            throw new RowValidationException(
                "Response carries no logprobs; confirm logprobs and top_logprobs were accepted");
        }

        if (logprobs.TryGetProperty("top_logprobs", out var positions)
            && positions.ValueKind == JsonValueKind.Array && positions.GetArrayLength() > 0)
        {
            var byToken = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var property in positions[0].EnumerateObject())
            {
                byToken[property.Name] = property.Value.GetDouble();
            }
            return byToken;
        }

        var keys = string.Join(", ", logprobs.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal));
        throw new RowValidationException($"Unrecognised logprobs shape: [{keys}]");
    }
}
