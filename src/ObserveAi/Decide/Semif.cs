using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ObserveAi;

/// <summary>Raised for a row that fails validation or cannot be scored, as ValueError is in the Python original.</summary>
public sealed class RowValidationException(string message) : Exception(message);

/// <summary>A single chat message in the direct-decision prompt.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// The SemIf direct-decision contract: validation, prompts and softmax. Ported from SemIf,
/// https://github.com/TheoLeeCJ/SemIf-OpenJev, Copyright (c) 2026 TheoLeeCJ, MIT License (see upstream
/// LICENSE). Matches src/observe_ai/semif.py so prompt text and prompt_sha256 stay comparable with SemIf's
/// published results.
/// </summary>
public static class Semif
{
    public const string Letters = "ABCDEFGHIJKLMNOP";

    public const string DirectSystem =
        "Apply the supplied criterion to the supplied evidence. Choose exactly one listed option. " +
        "Respond with only its uppercase letter, with no explanation or reasoning.";

    // Qwen3 ChatML control tokens. Qwen3's packaged template ends at "<|im_start|>assistant\n", so the first
    // sampled token can open a <think> block. This suffix closes an empty reasoning block, which puts the answer
    // letter at the first sampled position. Upstream SemIf reaches the same string via apply_chat_template(enable_thinking=False).
    public const string QwenThinkSuppressedSuffix = "<|im_start|>assistant\n<think>\n\n</think>\n\n";

    private static readonly string[] RequiredFields = ["id", "options", "question", "state"];

    public static void ValidateRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new RowValidationException("Row must be a JSON object");
        }

        var missing = RequiredFields.Where(key => !row.TryGetProperty(key, out _)).ToList();
        if (missing.Count > 0)
        {
            throw new RowValidationException(
                $"Row is missing fields: [{string.Join(", ", missing.Select(key => $"'{key}'"))}]");
        }

        if (!IsNonEmptyString(row, "id") || !IsNonEmptyString(row, "question"))
        {
            throw new RowValidationException("id and question must be nonempty strings");
        }

        var state = row.GetProperty("state");
        if (!HasNonEmptyStateShape(state))
        {
            throw new RowValidationException("state must be a nonempty string, object, or array");
        }
        if (!IsFiniteJson(state))
        {
            throw new RowValidationException("state must be finite JSON-compatible data");
        }

        var options = row.GetProperty("options");
        if (options.ValueKind != JsonValueKind.Array
            || options.GetArrayLength() < 2
            || options.GetArrayLength() > Letters.Length)
        {
            throw new RowValidationException("options must contain 2-16 entries");
        }

        var ids = new List<string>();
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object
                || !option.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
                || !option.TryGetProperty("description", out var descriptionValue)
                || descriptionValue.ValueKind != JsonValueKind.String)
            {
                throw new RowValidationException("Each option needs string id and description fields");
            }
            ids.Add(idValue.GetString()!);
        }
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new RowValidationException("Option IDs must be unique");
        }
    }

    public static IReadOnlyList<ChatMessage> DirectMessages(JsonElement row)
    {
        ValidateRow(row);
        var state = row.GetProperty("state");
        var question = row.GetProperty("question").GetString()!;
        var options = row.GetProperty("options");

        var content = new StringBuilder();
        content.Append("{\"evidence\": ");
        PythonJson.Write(content, state);
        content.Append(", \"criterion\": ");
        PythonJson.WriteString(content, question);
        content.Append(", \"options\": [");
        var index = 0;
        foreach (var option in options.EnumerateArray())
        {
            if (index > 0)
            {
                content.Append(", ");
            }
            content.Append("{\"letter\": ");
            PythonJson.WriteString(content, Letters[index].ToString());
            content.Append(", \"description\": ");
            PythonJson.WriteString(content, option.GetProperty("description").GetString()!);
            content.Append('}');
            index++;
        }
        content.Append("]}");

        return
        [
            new ChatMessage("system", DirectSystem),
            new ChatMessage("user", content.ToString()),
        ];
    }

    public static double[] Softmax(IReadOnlyList<double> values)
    {
        if (values.Count < 2 || values.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Need at least two finite scores");
        }
        var maximum = values.Max();
        var weights = values.Select(value => Math.Exp(value - maximum)).ToArray();
        var total = weights.Sum();
        return weights.Select(weight => weight / total).ToArray();
    }

    public static string Digest(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }

    public static string RenderQwen3Prompt(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append("<|im_start|>").Append(message.Role).Append('\n')
                .Append(message.Content).Append("<|im_end|>\n");
        }
        builder.Append(QwenThinkSuppressedSuffix);
        return builder.ToString();
    }

    private static bool IsNonEmptyString(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 };

    private static bool HasNonEmptyStateShape(JsonElement state) => state.ValueKind switch
    {
        JsonValueKind.String => state.GetString() is { Length: > 0 },
        JsonValueKind.Object => state.EnumerateObject().Any(),
        JsonValueKind.Array => state.GetArrayLength() > 0,
        _ => false,
    };

    private static bool IsFiniteJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => !element.TryGetDouble(out var value) || double.IsFinite(value),
        JsonValueKind.Object => element.EnumerateObject().All(property => IsFiniteJson(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().All(IsFiniteJson),
        _ => true,
    };
}

/// <summary>
/// Serialises a JsonElement as Python's json.dumps(value, ensure_ascii=False) does: ", " and ": " separators,
/// minimal escaping, and non-ASCII characters left as they are. Keeps prompt bytes identical to the Python
/// implementation.
/// </summary>
internal static class PythonJson
{
    public static void Write(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (!firstProperty)
                    {
                        builder.Append(", ");
                    }
                    firstProperty = false;
                    WriteString(builder, property.Name);
                    builder.Append(": ");
                    Write(builder, property.Value);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(", ");
                    }
                    firstItem = false;
                    Write(builder, item);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(builder, element.GetString()!);
                break;
            case JsonValueKind.Number:
                builder.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    public static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        builder.Append('"');
    }
}
