using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObserveAi;

/// <summary>
/// One candidate pipeline execution, shaped as infra/pipeline.asl.json's
/// Identify step reads its raw input: $.logGroup and $.message.
/// </summary>
public sealed record ExecutionInput(
    [property: JsonPropertyName("logGroup")] string LogGroup,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Unpacks a CloudWatch Logs subscription payload as it arrives on Kinesis:
/// base64 over gzip over a `{messageType, logGroup, logStream, logEvents:
/// [{id, timestamp, message}]}` envelope. One Kinesis record batches many log
/// events, and each becomes a candidate pipeline execution.
/// </summary>
public static class LogEnvelope
{
    private const string ControlMessage = "CONTROL_MESSAGE";

    /// <summary>
    /// Unpacks one record's payload into the executions it starts. A
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
    public static IReadOnlyList<ExecutionInput> Unpack(string base64Payload)
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
        var executions = new List<ExecutionInput>();
        foreach (var logEvent in root.GetProperty("logEvents").EnumerateArray())
        {
            executions.Add(new ExecutionInput(logGroup, logEvent.GetProperty("message").GetString()!));
        }
        return executions;
    }
}
