using System.Text.Json;
using ObserveAi;

namespace ObserveAi.Tests;

public class ParityCheckTests
{
    [Fact]
    public void PromptMatchesPythonByteForByte()
    {
        var row = JsonDocument.Parse("""
        {
          "id": "row-1",
          "state": {"service": "checkout", "stack_trace": "boom", "nested": ["a", 1, true, null], "unicode": "café ☃"},
          "question": "Bug or dependency?",
          "options": [
            {"id": "bug", "description": "Our code."},
            {"id": "downstream", "description": "Their service \"quoted\" and \\ backslash."}
          ]
        }
        """).RootElement;

        var messages = Semif.DirectMessages(row);
        var prompt = Semif.RenderQwen3Prompt(messages);

        const string expected = "<|im_start|>system\nApply the supplied criterion to the supplied evidence. Choose exactly one listed option. Respond with only its uppercase letter, with no explanation or reasoning.<|im_end|>\n<|im_start|>user\n{\"evidence\": {\"service\": \"checkout\", \"stack_trace\": \"boom\", \"nested\": [\"a\", 1, true, null], \"unicode\": \"café ☃\"}, \"criterion\": \"Bug or dependency?\", \"options\": [{\"letter\": \"A\", \"description\": \"Our code.\"}, {\"letter\": \"B\", \"description\": \"Their service \\\"quoted\\\" and \\\\ backslash.\"}]}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";

        Assert.Equal(expected, prompt);
        Assert.Equal("fedea23a3c29aac999344a8c135de0315c4a8538a6a2dfab933fa3baf9563711", Semif.Digest(prompt));
    }
}
