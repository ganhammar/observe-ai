using System.Text.Json;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Exercises the source-generated JsonSerializerContext the NativeAOT publish
/// depends on (reflection-based serialization is unavailable once trimmed),
/// checking that a successful row and a failed row serialise into the two
/// distinct shapes Python's handler.py returns, and that -Infinity in
/// option_logprobs (every option letter missing) round-trips the way Python's
/// default json.dumps(allow_nan=True) would emit it.
/// </summary>
public class LambdaJsonContextTests
{
    [Fact]
    public void SuccessfulRowOmitsErrorAndSerialisesAllFields()
    {
        var score = new ScoreResult(
            "row-1",
            ["bug", "downstream"],
            [0.7, 0.3],
            [-0.1, -1.2],
            0.9,
            [],
            false,
            new TopToken("A", 0.9),
            41,
            0.01,
            "abc123",
            BedrockBackend.PromptVersion);

        var json = JsonSerializer.Serialize(RowResultDto.FromScore(score), LambdaJsonContext.Default.RowResultDto);
        var element = JsonDocument.Parse(json).RootElement;

        Assert.Equal("row-1", element.GetProperty("id").GetString());
        Assert.False(element.TryGetProperty("error", out _));
        Assert.Equal("bug", element.GetProperty("option_ids")[0].GetString());
        Assert.Equal(0.9, element.GetProperty("declared_mass").GetDouble());
        Assert.Equal("A", element.GetProperty("top_token").GetProperty("token").GetString());
    }

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
        Assert.False(element.TryGetProperty("declared_mass", out _));
        Assert.False(element.TryGetProperty("abstained", out _));
    }

    [Fact]
    public void AbstainedRowSerialisesNegativeInfinityLogprobs()
    {
        var score = new ScoreResult(
            "row-3",
            ["bug", "downstream"],
            [0.0, 0.0],
            [double.NegativeInfinity, double.NegativeInfinity],
            0.0,
            ["bug", "downstream"],
            true,
            new TopToken("Based", 0.9),
            41,
            0.01,
            "abc123",
            BedrockBackend.PromptVersion);

        var json = JsonSerializer.Serialize(RowResultDto.FromScore(score), LambdaJsonContext.Default.RowResultDto);

        // Matches Python's default json.dumps(allow_nan=True) rendering of float('-inf').
        Assert.Contains("-Infinity", json);

        var roundTripped = JsonSerializer.Deserialize(json, LambdaJsonContext.Default.RowResultDto)!;
        Assert.Equal(double.NegativeInfinity, roundTripped.OptionLogprobs![0]);
    }

    [Fact]
    public void TriageRowSerialisesFallbackAndSignals()
    {
        var dto = RowResultDto.FromTriage(
            "row-4",
            new TriageResult(
                new CombineResult(0.7, 0.3, true),
                [new TriageAnswer("baseline", new Dictionary<string, double> { ["bug"] = 0.7, ["downstream"] = 0.3 }, null)]),
            QuestionTree.LoadEmbedded());

        var json = JsonSerializer.Serialize(dto, LambdaJsonContext.Default.RowResultDto);
        var element = JsonDocument.Parse(json).RootElement;

        Assert.Equal("row-4", element.GetProperty("id").GetString());
        Assert.Equal(["bug", "downstream"], element.GetProperty("option_ids").EnumerateArray().Select(e => e.GetString()));
        Assert.True(element.GetProperty("fallback").GetBoolean());
        Assert.Equal(7, element.GetProperty("signals").EnumerateObject().Count());
        Assert.False(element.TryGetProperty("declared_mass", out _));
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
