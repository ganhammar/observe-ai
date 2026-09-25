using System.Text.Json;
using System.Text.RegularExpressions;

namespace ObserveAi;

/// <summary>
/// States the numeric comparisons in the evidence as sentences under derived_facts, so the model never
/// compares two numbers itself. Asked directly whether 3600 is larger than 900, the model answers no.
/// Mirrors eval/derived.py, including its sentence wording, which is part of what was measured. Four
/// producers pair fields by name convention: acquired far more than released, a reading far from its
/// baseline, work repeated within one operation, and an interval longer than the lifetime it should
/// stay inside.
/// </summary>
public static class Derived
{
    private const double RatioThreshold = 5.0;

    // Field-name pairs that mean "this happened" against "this undid it".
    private static readonly (string Up, string Down)[] AcquireReleasePairs =
    [
        ("opened", "disposed"), ("opened", "closed"), ("acquired", "released"), ("created", "destroyed"),
    ];

    // Suffixes marking a value as the historical reference for a current reading.
    private static readonly string[] BaselineMarkers = ["_7d_avg", "_avg", "_average", "_baseline", "prior_", "_7d_max"];

    private static readonly Regex RepetitionCountPattern = new("count|_calls|queries|attempts", RegexOptions.Compiled);

    public static IReadOnlyList<string> Derive(JsonElement state)
    {
        if (!state.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var numbers = Numbers(evidence);
        var facts = new List<string>();
        foreach (var fact in AcquireRelease(numbers)
                     .Concat(CurrentVersusBaseline(numbers))
                     .Concat(Repetition(numbers))
                     .Concat(Durations(numbers)))
        {
            if (!facts.Contains(fact))
            {
                facts.Add(fact);
            }
        }
        return facts;
    }

    /// <summary>Returns state with derived_facts appended, or state unchanged when there are none.</summary>
    public static JsonElement WithDerived(JsonElement state)
    {
        var facts = Derive(state);
        if (facts.Count == 0)
        {
            return state;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in state.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteStartArray("derived_facts");
            foreach (var fact in facts)
            {
                writer.WriteStringValue(fact);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>The evidence's numeric fields, keyed by field name. Booleans are not numbers.</summary>
    private static Dictionary<string, double> Numbers(JsonElement evidence)
    {
        var numbers = new Dictionary<string, double>();
        foreach (var property in evidence.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                numbers[property.Name] = property.Value.GetDouble();
            }
        }
        return numbers;
    }

    private static IEnumerable<string> AcquireRelease(Dictionary<string, double> numbers)
    {
        foreach (var (up, down) in AcquireReleasePairs)
        {
            var highs = numbers.Keys.Where(key => key.Contains(up)).ToList();
            var lows = numbers.Keys.Where(key => key.Contains(down)).ToList();
            foreach (var highName in highs)
            {
                foreach (var lowName in lows)
                {
                    var sentence = RatioSentence(highName, numbers[highName], lowName, numbers[lowName]);
                    if (sentence is not null)
                    {
                        yield return sentence;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> CurrentVersusBaseline(Dictionary<string, double> numbers)
    {
        var baselineKeys = numbers.Keys.Where(key => BaselineMarkers.Any(key.Contains)).ToList();
        var readingKeys = numbers.Keys.Where(key => !baselineKeys.Contains(key)).ToList();
        foreach (var other in baselineKeys)
        {
            var stem = StripMarkers(other);
            if (stem.Length == 0)
            {
                continue;
            }
            foreach (var name in readingKeys)
            {
                // A reading matches a baseline when either name contains the other's stem, wherever the markers sit.
                if (!name.Contains(stem) && !stem.Contains(StripMarkers(name)))
                {
                    continue;
                }
                var sentence = RatioSentence(name, numbers[name], other, numbers[other]);
                if (sentence is not null)
                {
                    yield return sentence;
                }
            }
        }
    }

    private static IEnumerable<string> Repetition(Dictionary<string, double> numbers)
    {
        var counts = numbers.Keys.Where(key => RepetitionCountPattern.IsMatch(key)).ToList();
        var distincts = numbers.Keys.Where(key => key.Contains("distinct")).ToList();
        foreach (var count in counts)
        {
            foreach (var distinct in distincts)
            {
                var sentence = RatioSentence(count, numbers[count], distinct, numbers[distinct]);
                if (sentence is not null)
                {
                    yield return $"{sentence} The same work is repeated within one operation.";
                }
            }
        }
    }

    private static IEnumerable<string> Durations(Dictionary<string, double> numbers)
    {
        var lifetimes = numbers.Where(pair => pair.Key.Contains("expires") || pair.Key.Contains("lifetime")).ToList();
        var intervals = numbers.Where(pair => pair.Key.Contains("interval") || pair.Key.Contains("refresh")).ToList();
        foreach (var (intervalName, intervalValue) in intervals)
        {
            foreach (var (lifetimeName, lifetimeValue) in lifetimes)
            {
                if (intervalValue > lifetimeValue)
                {
                    yield return $"{intervalName} ({PythonFloat.FormatG(intervalValue)}) is longer than " +
                        $"{lifetimeName} ({PythonFloat.FormatG(lifetimeValue)}), so the value is stale for part of every cycle.";
                }
            }
        }
    }

    private static string? RatioSentence(string highName, double high, string lowName, double low)
    {
        if (low == 0)
        {
            return $"{highName} is {PythonFloat.FormatG(high)} while {lowName} is 0.";
        }
        var ratio = high / low;
        if (ratio < RatioThreshold)
        {
            return null;
        }
        return $"{highName} is {PythonFloat.FormatCount(ratio)} times {lowName} " +
            $"({PythonFloat.FormatG(high)} against {PythonFloat.FormatG(low)}).";
    }

    /// <summary>Reduces a baseline field name to the reading it is a baseline for.</summary>
    private static string StripMarkers(string name)
    {
        var stem = name;
        foreach (var marker in BaselineMarkers)
        {
            stem = stem.Replace(marker, "_");
        }
        return Regex.Replace(stem, "_+", "_").Trim('_');
    }
}
