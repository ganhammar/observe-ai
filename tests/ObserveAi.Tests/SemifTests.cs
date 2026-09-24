using System.Text.Json;
using System.Text.Json.Nodes;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>Ports the intent of tests/test_semif.py against the C# port.</summary>
public class SemifTests
{
    private static JsonObject BaseRow() => new()
    {
        ["id"] = "x",
        ["state"] = "owned evidence",
        ["question"] = "Which answer follows?",
        ["options"] = new JsonArray(
            new JsonObject { ["id"] = "yes", ["description"] = "Yes." },
            new JsonObject { ["id"] = "no", ["description"] = "No." }),
    };

    private static JsonElement ToElement(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement;

    [Fact]
    public void DirectMessagesExcludesExtraFields()
    {
        var row = BaseRow();
        row["label"] = "yes";
        row["provenance"] = new JsonObject { ["secret"] = "do not leak" };

        var rendered = string.Join(
            " ", Semif.DirectMessages(ToElement(row)).Select(message => message.Role + " " + message.Content));

        Assert.Contains("owned evidence", rendered);
        Assert.DoesNotContain("secret", rendered);
        Assert.DoesNotContain("label", rendered);
    }

    [Fact]
    public void SoftmaxIsFiniteAndNormalized()
    {
        var values = Semif.Softmax([1000.0, 999.0, -1000.0]);

        Assert.All(values, value => Assert.True(double.IsFinite(value)));
        Assert.Equal(1.0, values.Sum(), precision: 9);
        Assert.True(values[0] > values[1]);
        Assert.True(values[1] > values[2]);
    }

    [Fact]
    public void SoftmaxRejectsFewerThanTwoScores()
    {
        var error = Assert.Throws<ArgumentException>(() => Semif.Softmax([1.0]));
        Assert.Contains("two finite", error.Message);
    }

    [Fact]
    public void SoftmaxRejectsNonfiniteScores()
    {
        var error = Assert.Throws<ArgumentException>(() => Semif.Softmax([1.0, double.NegativeInfinity]));
        Assert.Contains("two finite", error.Message);
    }

    [Fact]
    public void DuplicateOptionsRejected()
    {
        var row = BaseRow();
        row["options"] = new JsonArray(
            new JsonObject { ["id"] = "yes", ["description"] = "Yes." },
            new JsonObject { ["id"] = "yes", ["description"] = "Yes." });

        var error = Assert.Throws<RowValidationException>(() => Semif.ValidateRow(ToElement(row)));
        Assert.Contains("unique", error.Message);
    }

    [Fact]
    public void TooFewOptionsRejected()
    {
        var row = BaseRow();
        row["options"] = new JsonArray(new JsonObject { ["id"] = "yes", ["description"] = "Yes." });

        var error = Assert.Throws<RowValidationException>(() => Semif.ValidateRow(ToElement(row)));
        Assert.Contains("2-16", error.Message);
    }

    [Fact]
    public void TooManyOptionsRejected()
    {
        var row = BaseRow();
        row["options"] = new JsonArray(
            Enumerable.Range(0, 17)
                .Select(i => (JsonNode)new JsonObject { ["id"] = i.ToString(), ["description"] = "d" })
                .ToArray());

        var error = Assert.Throws<RowValidationException>(() => Semif.ValidateRow(ToElement(row)));
        Assert.Contains("2-16", error.Message);
    }

    [Fact]
    public void MissingRequiredFieldRejected()
    {
        var row = BaseRow();
        row.Remove("question");

        var error = Assert.Throws<RowValidationException>(() => Semif.ValidateRow(ToElement(row)));
        Assert.Contains("missing fields", error.Message);
    }

    [Fact]
    public void EmptyIdRejected()
    {
        var row = BaseRow();
        row["id"] = "";

        var error = Assert.Throws<RowValidationException>(() => Semif.ValidateRow(ToElement(row)));
        Assert.Contains("nonempty strings", error.Message);
    }

    [Fact]
    public void OptionMissingDescriptionRejected()
    {
        var row = BaseRow();
        row["options"] = new JsonArray(
            new JsonObject { ["id"] = "yes" },
            new JsonObject { ["id"] = "no", ["description"] = "No." });

        var error = Assert.Throws<RowValidationException>(() => Semif.ValidateRow(ToElement(row)));
        Assert.Contains("id and description", error.Message);
    }

    [Fact]
    public void StructuredJsonStateIsSupported()
    {
        var row = BaseRow();
        row["state"] = new JsonObject
        {
            ["policy"] = "Never request passwords",
            ["candidate"] = new JsonArray("invoice id"),
        };

        Semif.ValidateRow(ToElement(row));
        var content = Semif.DirectMessages(ToElement(row))[1].Content;
        Assert.Contains("\"policy\"", content);
    }

    [Fact]
    public void EmptyStateRejected()
    {
        var row = BaseRow();
        row["state"] = "";

        var error = Assert.Throws<RowValidationException>(() => Semif.ValidateRow(ToElement(row)));
        Assert.Contains("nonempty string, object, or array", error.Message);
    }

    [Fact]
    public void RenderQwen3PromptEndsWithThinkSuppressedSuffix()
    {
        var messages = Semif.DirectMessages(ToElement(BaseRow()));
        var prompt = Semif.RenderQwen3Prompt(messages);

        Assert.EndsWith("<|im_start|>assistant\n<think>\n\n</think>\n\n", prompt, StringComparison.Ordinal);
        Assert.StartsWith($"<|im_start|>system\n{Semif.DirectSystem}<|im_end|>\n", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void DigestIsSha256HexOfUtf8Bytes()
    {
        var digest = Semif.Digest("hello");
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", digest);
    }
}
