using System.IO.Compression;
using System.Text;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>Builds the gzipped, base64-encoded envelope that a CloudWatch Logs subscription delivers on Kinesis.</summary>
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
    public void TwoLogEventsYieldTwoCandidatesWithTheRightIdLogGroupAndMessage()
    {
        var payload = Envelope("DATA_MESSAGE", "/aws/lambda/orders", "first error", "second error");

        var candidates = LogEnvelope.Unpack(payload);

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal("/aws/lambda/orders", candidate.LogGroup));
        Assert.Equal("first error", candidates[0].Message);
        Assert.Equal("second error", candidates[1].Message);
        Assert.Equal("0", candidates[0].Id);
        Assert.Equal("1", candidates[1].Id);
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
