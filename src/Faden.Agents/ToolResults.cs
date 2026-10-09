using Faden.Chat;
using Microsoft.Extensions.AI;

namespace Faden.Agents;

/// <summary>
/// A tool's return value as the text the transcript, the UI and the server's events keep, and the pictures it
/// carries for the model (sdk/10 §3). A tool returns pictures as <see cref="DataContent"/> (<c>image/*</c>), alone
/// or with <see cref="TextContent"/>; any other value is text as before.
/// </summary>
internal static class ToolResults
{
    public static (string Text, IReadOnlyList<DataContent> Pictures) Split(string callId, object? value)
    {
        var contents = value switch
        {
            AIContent one => new[] { one },
            IEnumerable<AIContent> many => many.ToArray(),
            _ => null,
        };
        var pictures = contents?.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("image")).ToList();
        if (contents is null || pictures!.Count == 0)
        {
            // Without a picture a result stays exactly as before (an MCP tool's content list as its JSON).
            return (ChatWire.ResultText(new FunctionResultContent(callId, value)), Array.Empty<DataContent>());
        }
        var text = string.Join("\n", contents.OfType<TextContent>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));
        return (text, pictures);
    }
}
