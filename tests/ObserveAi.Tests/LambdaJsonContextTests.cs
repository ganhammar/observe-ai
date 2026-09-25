using System.Text.Json;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Exercises the source-generated JsonSerializerContext the NativeAOT publish
/// depends on (reflection-based serialization is unavailable once trimmed),
/// checking that a triaged row and a failed row serialise into distinct shapes.
/// </summary>
public class LambdaJsonContextTests
{
    [Fact]
    public void FailedRowOnlyHasIdAndError()
    {
        var json = JsonSerializer.Serialize(
            RowResultDto.FromError("row-2", "RowValidationException: options must contain 2-16 entries"),
            LambdaJsonContext.Default.RowResultDto);
        var element = JsonDocument.Parse(json).RootElement;

        Assert.Equal("row-2", element.GetProperty("id").GetString());
        Assert.Contains("2-16", element.GetProperty("error").GetString());
        Assert.False(element.TryGetProperty("option_ids", out _));
        Assert.False(element.TryGetProperty("signals", out _));
    }

    [Fact]
    public void TriageRowSerialisesFallbackAndSignals()
    {
        var dto = RowResultDto.FromTriage(
            "row-4",
            new TriageResult(
                new CombineResult(0.7, 0.3, true),
                [
                    new TriageAnswer("baseline", new Dictionary<string, double> { ["bug"] = 0.7, ["downstream"] = 0.3 }, null),
                    new TriageAnswer("external_change", new Dictionary<string, double> { ["yes"] = 0.2, ["no"] = 0.8 }, null),
                    new TriageAnswer("repeated_work", null, "AmazonServiceException: not ready"),
                ],
                DeclaredMass: 0.98),
            QuestionTree.LoadEmbedded());

        var json = JsonSerializer.Serialize(dto, LambdaJsonContext.Default.RowResultDto);
        var element = JsonDocument.Parse(json).RootElement;

        Assert.Equal("row-4", element.GetProperty("id").GetString());
        Assert.Equal(["bug", "downstream"], element.GetProperty("option_ids").EnumerateArray().Select(e => e.GetString()));
        Assert.True(element.GetProperty("fallback").GetBoolean());
        // Only answered signals are reported; the baseline is not a signal.
        var signal = Assert.Single(element.GetProperty("signals").EnumerateObject());
        Assert.Equal("external_change", signal.Name);
        Assert.Equal(0.2, signal.Value.GetDouble());
    }

    [Fact]
    public void LambdaResponseWrapsResultsList()
    {
        var response = new LambdaResponse
        {
            Results = [RowResultDto.FromError(null, "ValueError: Row must be a JSON object, got str")],
        };

        var json = JsonSerializer.Serialize(response, LambdaJsonContext.Default.LambdaResponse);
        var element = JsonDocument.Parse(json).RootElement;

        Assert.Equal(1, element.GetProperty("results").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, element.GetProperty("results")[0].GetProperty("id").ValueKind);
    }
}
