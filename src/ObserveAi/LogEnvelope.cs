using System.IO.Compression;
using System.Text.Json;

namespace ObserveAi;

/// <summary>
/// One raw log event out of a CloudWatch Logs subscription payload, before the
/// consumer decides whether it is worth an execution: the id CloudWatch itself
/// assigned it (the source for a stable execution name), the log group, and
/// the message.
/// </summary>
public sealed record LogCandidate(string Id, string LogGroup, string Message);

/// <summary>
/// Unpacks a CloudWatch Logs subscription payload as it arrives on Kinesis:
/// base64 over gzip over a `{messageType, logGroup, logStream, logEvents:
/// [{id, timestamp, message}]}` envelope. One Kinesis record batches many log
/// events, and each becomes a candidate the consumer decides on.
/// </summary>
public static class LogEnvelope
{
    private const string ControlMessage = "CONTROL_MESSAGE";

    /// <summary>
    /// Unpacks one record's payload into the candidates it carries. A
    /// CONTROL_MESSAGE envelope is CloudWatch's own subscription health check,
    /// carries no log data, and yields nothing.
    ///
    /// This deliberately does not try to reassemble a stack trace split
    /// across several log events. The subscription filter that selects what
    /// reaches this stream only matches lines mentioning an error (see
    /// infra/ingest.yaml's FilterPattern), so a candidate here has already
    /// lost whatever surrounding lines did not match, and only the log
    /// producer, which knows where one entry ends and the next begins, can
    /// put the pieces back together correctly. Guessing that boundary here
    /// from arrival order or timestamps would silently stitch unrelated
    /// lines together, which is worse than leaving each line to fingerprint
    /// on its own.
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
