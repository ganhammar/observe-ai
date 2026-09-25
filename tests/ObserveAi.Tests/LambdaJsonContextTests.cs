using System.Text.Json;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Exercises the source-generated JsonSerializerContext the NativeAOT publish
/// depends on (reflection-based serialization is unavailable once trimmed),
/// checking that a triage verdict serialises at the top level of the response
/// where the state machine reads it.
/// </summary>
public class LambdaJsonContextTests
{
    [Fact]
    public void TriageVerdictSerialisesAtTheTopLevel()
    {
        var response = LambdaResponse.FromTriage(
            new TriageResult(
                new CombineResult(0.7, 0.3, true),
                [
                    new TriageAnswer("baseline", new Dictionary<string, double> { ["bug"] = 0.7, ["downstream"] = 0.3 }, null),
                    new TriageAnswer("external_change", new Dictionary<string, double> { ["yes"] = 0.2, ["no"] = 0.8 }, null),
                    new TriageAnswer("repeated_work", null, "AmazonServiceException: not ready"),
                ],
                DeclaredMass: 0.98),
            QuestionTree.LoadEmbedded());

        var json = JsonSerializer.Serialize(response, LambdaJsonContext.Default.LambdaResponse);
        var element = JsonDocument.Parse(json).RootElement;

        Assert.Equal(["bug", "downstream"], element.GetProperty("option_ids").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0.7, element.GetProperty("probabilities")[0].GetDouble());
        Assert.True(element.GetProperty("fallback").GetBoolean());
        Assert.Equal(0.98, element.GetProperty("declared_mass").GetDouble());
        // Only answered signals are reported; the baseline is not a signal.
        var signal = Assert.Single(element.GetProperty("signals").EnumerateObject());
        Assert.Equal("external_change", signal.Name);
        Assert.Equal(0.2, signal.Value.GetDouble());
        Assert.False(element.TryGetProperty("escalate", out _));
    }
}
