using System.Text;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace ObserveAi;

/// <summary>
/// The pipeline's one generative call: from a trace and the source files it names, write the root cause
/// that opens the filed issue. It runs only after triage and the rate caps allow a filing. It uses Converse
/// on a managed chat model, separate from the readout, which needs raw InvokeModel bytes from an imported model.
/// </summary>
public static class Diagnosis
{
    public delegate Task<string> Converse(string modelId, string system, string user, CancellationToken cancellationToken);

    /// <summary>Per-file character cap, since a trace can name a generated file of any size.</summary>
    public const int MaxCharsPerFile = 40_000;

    private const int MaxOutputTokens = 600;

    public const string System =
        "You are reading a stack trace and the source files it names, from a service whose owners " +
        "have already decided the failure is a defect in their own code. State the root cause in at " +
        "most three sentences: which line is wrong, what input reaches it, and why the code does not " +
        "handle that input. Quote the offending line. If the source does not show the cause, say what " +
        "it does show and what is missing. No fixes, no restating the trace, no headings, no lists.";

    /// <summary>Converse against a Bedrock runtime client at temperature zero, so one defect reads the same way twice.</summary>
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
    /// The user turn: the raw trace, then each fetched file in full under its path. Whole files are sent
    /// because the cause usually sits a few lines above the throw.
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
