using System.IO.Compression;
using System.Text;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Builds the gzipped base64 envelope in the test itself, exactly as CloudWatch
/// Logs would deliver it on Kinesis, rather than committing a binary fixture.
/// </summary>
public class LogEnvelopeTests
{
    private static string Envelope(string messageType, string logGroup, params string[] messages)
    {
        var events = string.Join(",", messages.Select((message, i) => $$"""{"id":"{{i}}","timestamp":1440442987000,"message":"{{message}}"}"""));
        var json = $$"""
            {"messageType":"{{messageType}}","owner":"123456789012","logGroup":"{{logGroup}}","logStream":"testStream","subscriptionFilters":["f"],"logEvents":[{{events}}]}
            """;

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return Convert.ToBase64String(output.ToArray());
    }

    [Fact]
    public void TwoLogEventsYieldTwoExecutionInputsWithTheRightLogGroupAndMessages()
    {
        var payload = Envelope("DATA_MESSAGE", "/aws/lambda/orders", "first error", "second error");

        var executions = LogEnvelope.Unpack(payload);

        Assert.Equal(2, executions.Count);
        Assert.All(executions, execution => Assert.Equal("/aws/lambda/orders", execution.LogGroup));
        Assert.Equal("first error", executions[0].Message);
        Assert.Equal("second error", executions[1].Message);
    }

    [Fact]
    public void AControlMessageEnvelopeYieldsNothing()
    {
        var payload = Envelope(
            "CONTROL_MESSAGE", "", "CWL CONTROL MESSAGE: Checking health of destination Kinesis stream.");

        var executions = LogEnvelope.Unpack(payload);

        Assert.Empty(executions);
    }
}
