using System.IO.Compression;
using System.Text.Json;

namespace ObserveAi;

/// <summary>One log event from a subscription payload. Id is CloudWatch's event id, the source of the execution name.</summary>
public sealed record LogCandidate(string Id, string LogGroup, string Message);

/// <summary>
/// Unpacks a CloudWatch Logs subscription payload from Kinesis: base64 over gzip over
/// `{messageType, logGroup, logStream, logEvents: [{id, timestamp, message}]}`. One record carries many log events.
/// </summary>
public static class LogEnvelope
{
    private const string ControlMessage = "CONTROL_MESSAGE";

    /// <summary>
    /// Unpacks one record into its log events. A CONTROL_MESSAGE envelope is CloudWatch's subscription health
    /// check and yields nothing. A stack trace split across log events stays split: the subscription filter
    /// (infra/ingest.yaml's FilterPattern) passes only lines mentioning an error, so only the log producer can
    /// rejoin an entry.
    /// </summary>
    public static IReadOnlyList<LogCandidate> Unpack(string base64Payload)
    {
        var compressed = Convert.FromBase64String(base64Payload);
        using var buffer = new MemoryStream(compressed);
        using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip);
        var root = document.RootElement;

        if (root.TryGetProperty("messageType", out var messageType) && messageType.GetString() == ControlMessage)
        {
            return [];
        }

        var logGroup = root.GetProperty("logGroup").GetString()!;
        var candidates = new List<LogCandidate>();
        foreach (var logEvent in root.GetProperty("logEvents").EnumerateArray())
        {
            candidates.Add(new LogCandidate(
                logEvent.GetProperty("id").GetString()!, logGroup, logEvent.GetProperty("message").GetString()!));
        }
        return candidates;
    }
}
