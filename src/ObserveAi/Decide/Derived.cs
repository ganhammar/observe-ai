using System.Text.Json;

namespace ObserveAi;

/// <summary>
/// States the numeric comparisons in the evidence so the model does not have to.
///
/// The model reliably answers what a piece of evidence means and reliably fails to
/// compare two numbers: asked directly whether 3600 is larger than 900, it answers
/// no. So the arithmetic happens here instead. This walks the evidence, finds
/// numeric relations worth naming, and renders each as a plain sentence added to
/// the state as derived_facts. The model is then asked what the stated comparison
/// means, which is the kind of question it answers well.
///
/// The four producers that find those relations live in DerivedProducers.cs.
/// Nothing here decides bug versus downstream; it only makes the magnitudes
/// legible. Mirrors eval/derived.py exactly, including its sentence wording,
/// since the wording is part of what was measured.
/// </summary>
public static class Derived
{
    /// <summary>Returns the plain-sentence statements of the numeric relations in the evidence.</summary>
    public static IReadOnlyList<string> Derive(JsonElement state)
    {
        if (!state.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var numbers = Numbers(evidence);
        var facts = new List<string>();
        foreach (var fact in DerivedProducers.AcquireRelease(numbers)
                     .Concat(DerivedProducers.CurrentVersusBaseline(numbers))
                     .Concat(DerivedProducers.Repetition(numbers))
                     .Concat(DerivedProducers.Durations(numbers)))
        {
            if (!facts.Contains(fact))
            {
                facts.Add(fact);
            }
        }
        return facts;
    }

    /// <summary>Copies the state with a derived_facts array added when anything was found.</summary>
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
}
