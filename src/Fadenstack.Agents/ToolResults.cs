using System.Text.Json;
using Fadenstack.Chat;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

/// <summary>
/// A tool's return value as the text the transcript, the UI and the server's events keep, and the pictures it
/// carries for the model. A tool returns pictures as <see cref="DataContent"/> (<c>image/*</c>), alone
/// or with <see cref="TextContent"/>; any other value is text as before. Nothing (null, void) is "": the model gets
/// <see cref="Sessions.ContextBuilder.NoToolResult"/> for it.
/// </summary>
internal static class ToolResults
{
    public static (string Text, IReadOnlyList<DataContent> Pictures) Split(string callId, object? value)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            // A method that returned null comes back as JSON null: nothing, as from a void one (the TypeScript SDK's null).
            return ("", Array.Empty<DataContent>());
        }
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
