using System.Text.Json;

namespace ObserveAi.Tests;

/// <summary>Loads eval/logs.jsonl and eval/labels.json so trace-parsing tests run against the real fixture instead of hand-typed traces.</summary>
public static class EvalFixture
{
    public sealed record Row(string Id, string Runtime, string StackTrace)
    {
        public override string ToString() => Id;
    }

    public static IReadOnlyList<Row> Load()
    {
        var logsPath = Path.Combine(AppContext.BaseDirectory, "eval", "logs.jsonl");
        var labelsPath = Path.Combine(AppContext.BaseDirectory, "eval", "labels.json");

        using var labelsDoc = JsonDocument.Parse(File.ReadAllText(labelsPath));
        var labels = labelsDoc.RootElement;

        var rows = new List<Row>();
        foreach (var line in File.ReadAllLines(logsPath))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString()!;
            var stackTrace = root.GetProperty("state").GetProperty("stack_trace").GetString()!;
            var runtime = labels.GetProperty(id).GetProperty("runtime").GetString()!;
            rows.Add(new Row(id, runtime, stackTrace));
        }
        return rows;
    }
}
