using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ObserveAi;

/// <summary>
/// Hashes a JSON value's keys and coarse shape, ignoring values, so a repeat with a new request id or a
/// larger count still matches and a different set of signals does not. Feeds SeenStore.RecordAsync.
/// </summary>
public static class EvidenceSignature
{
    public static string Compute(JsonElement state)
    {
        var shape = new StringBuilder();
        Append(state, shape);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(shape.ToString()));
        return Convert.ToHexString(hash)[..16];
    }

    private static void Append(JsonElement element, StringBuilder shape)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                shape.Append('{');
                foreach (var name in element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))
                {
                    shape.Append(name).Append(':');
                    Append(element.GetProperty(name), shape);
                    shape.Append(',');
                }
                shape.Append('}');
                break;
            // Array length tracks how long an incident has run, so only the first element's shape counts.
            case JsonValueKind.Array:
                shape.Append("array<");
                if (element.GetArrayLength() > 0)
                {
                    Append(element.EnumerateArray().First(), shape);
                }
                else
                {
                    shape.Append("empty");
                }
                shape.Append('>');
                break;
            case JsonValueKind.True or JsonValueKind.False:
                shape.Append("bool");
                break;
            case JsonValueKind.Null or JsonValueKind.Undefined:
                shape.Append("null");
                break;
            default:
                shape.Append(element.ValueKind);
                break;
        }
    }
}
