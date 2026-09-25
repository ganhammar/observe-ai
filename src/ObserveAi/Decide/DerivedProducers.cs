using System.Text.RegularExpressions;

namespace ObserveAi;

/// <summary>
/// The four producers behind Derived.Derive, each pairing fields by name convention: acquired far more
/// than released, a reading far from its baseline, work repeated within one operation, and an interval
/// longer than the lifetime it should stay inside. Mirrors eval/derived.py's four private functions.
/// </summary>
internal static class DerivedProducers
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

    public static IEnumerable<string> AcquireRelease(Dictionary<string, double> numbers)
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

    public static IEnumerable<string> CurrentVersusBaseline(Dictionary<string, double> numbers)
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

    public static IEnumerable<string> Repetition(Dictionary<string, double> numbers)
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

    public static IEnumerable<string> Durations(Dictionary<string, double> numbers)
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
