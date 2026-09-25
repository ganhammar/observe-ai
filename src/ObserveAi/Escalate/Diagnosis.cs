using System.Text;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace ObserveAi;

/// <summary>
/// The one generative call in the pipeline: given a trace and the source files
/// it names, write the root cause a human reads first in the filed issue.
///
/// This runs only after the tree has decided the failure is ours and the rate
/// caps have allowed a filing, so it is the expensive step and the rare one.
/// The model is a managed Bedrock model reached through Converse, which is why
/// it is a separate delegate from the logit readout: the readout needs raw
/// InvokeModel bytes from an imported model, this needs text from a chat model.
/// </summary>
public static class Diagnosis
{
    public delegate Task<string> Converse(string modelId, string system, string user, CancellationToken cancellationToken);

    /// <summary>Per-file cap on what is sent, since a trace can name a generated file of any size.</summary>
    public const int MaxCharsPerFile = 40_000;

    private const int MaxOutputTokens = 600;

    public const string System =
        "You are reading a stack trace and the source files it names, from a service whose owners " +
        "have already decided the failure is a defect in their own code. State the root cause in at " +
        "most three sentences: which line is wrong, what input reaches it, and why the code does not " +
        "handle that input. Quote the offending line. If the source does not show the cause, say what " +
        "it does show and what is missing. No fixes, no restating the trace, no headings, no lists.";

    /// <summary>The real Converse against a Bedrock runtime client, at temperature zero so the same defect reads the same way twice.</summary>
    public static Converse Against(IAmazonBedrockRuntime client) => async (modelId, system, user, cancellationToken) =>
    {
        var request = new ConverseRequest
        {
            ModelId = modelId,
            System = [new SystemContentBlock { Text = system }],
            Messages = [new Message { Role = ConversationRole.User, Content = [new ContentBlock { Text = user }] }],
            InferenceConfig = new InferenceConfiguration { MaxTokens = MaxOutputTokens, Temperature = 0f },
        };
        var response = await client.ConverseAsync(request, cancellationToken).ConfigureAwait(false);
        return string.Concat(response.Output.Message.Content.Select(block => block.Text)).Trim();
    };

    /// <summary>
    /// The user turn: the raw trace, then each fetched file in full under its
    /// path. Files are sent whole rather than as a span around the failing line,
    /// because the cause is usually a few lines above the throw, outside any
    /// window chosen in advance.
    /// </summary>
    public static string Prompt(string rawTrace, IReadOnlyDictionary<string, string> sources)
    {
        var prompt = new StringBuilder();
        prompt.Append("Stack trace:\n\n").Append(rawTrace.Trim()).Append("\n\n");
        if (sources.Count == 0)
        {
            prompt.Append("No source file named by the trace could be fetched.\n");
            return prompt.ToString();
        }
        foreach (var (path, content) in sources)
        {
            var body = content.Length > MaxCharsPerFile ? content[..MaxCharsPerFile] + "\n[truncated]" : content;
            prompt.Append("File ").Append(path).Append(":\n\n").Append(body.TrimEnd()).Append("\n\n");
        }
        return prompt.ToString();
    }

    public static Task<string> DiagnoseAsync(
        Converse converse, string modelId, string rawTrace, IReadOnlyDictionary<string, string> sources,
        CancellationToken cancellationToken = default) =>
        converse(modelId, System, Prompt(rawTrace, sources), cancellationToken);
}
