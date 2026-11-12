using System.Text;
using System.Text.Json;
using Fadenstack.Http;
using Fadenstack.Tools;

namespace Fadenstack.Models;

/// <summary>Where a host tool call comes from, as the server's <c>origin</c> says.</summary>
public enum HostToolOrigin
{
    /// <summary>The person started it (a button, a view of a module).</summary>
    UserCommand,

    /// <summary>The person started it from the history (an undo, a restore).</summary>
    UserHistory,

    /// <summary>The model asked for it; <see cref="HostToolCheck.CallId"/> is required.</summary>
    ModelTool,
}

/// <summary>Which of the two checks around a host call this is.</summary>
public enum HostToolPhase
{
    /// <summary>Before the person is asked.</summary>
    Prepare,

    /// <summary>Right before the tool runs.</summary>
    Dispatch,
}

/// <summary>What the server decided for a host call. An answer this SDK does not know is <see cref="Deny"/>.</summary>
public enum HostToolVerdict
{
    Deny,
    Ask,
    Run,
}

/// <summary>The host-tool dispatch check: the feature name in the profile's and the server's feature lists.</summary>
public static class HostToolFeatures
{
    /// <summary>The server checks host tool calls of the model again right before they run (<c>host_tools.dispatch_check</c>).</summary>
    public const string DispatchCheck = "host_tools.dispatch_check";
}

/// <summary>
/// One check of a host call by the server (<c>POST /v1/agents/{agent}/tools/check</c>). It never runs a tool or a model.
/// </summary>
public sealed record HostToolCheck
{
    public string Name { get; init; } = "";

    /// <summary>The call's arguments, a JSON object.</summary>
    public JsonElement Arguments { get; init; }

    /// <summary>The session's level; Bypass is checked as Auto by the server (a command is not a call a grant covers).</summary>
    public ToolMode ToolMode { get; init; }

    public HostToolOrigin Origin { get; init; } = HostToolOrigin.UserCommand;

    /// <summary>Required for <see cref="HostToolOrigin.ModelTool"/>: binds the audit record across both phases.</summary>
    public string? CallId { get; init; }

    public HostToolPhase Phase { get; init; } = HostToolPhase.Prepare;

    /// <summary>SHA-256, hex, of the tool's parameter schema as the app reports it in its manifest.</summary>
    public string SchemaSha256 { get; init; } = "";

    /// <summary>For <see cref="HostToolPhase.Dispatch"/>: the answer the prepare check gave, whose three stamps must still hold.</summary>
    public HostToolDecision? Prepared { get; init; }

    public string ConversationId { get; init; } = "";

    public string TurnId { get; init; } = "";
}

/// <summary>
/// The server's answer to a <see cref="HostToolCheck"/>. The three stamps are comparison values, not signed permits: a
/// dispatch check that answers with another stamp, another principal or a denial stops the call.
/// </summary>
public sealed record HostToolDecision
{
    public HostToolVerdict Decision { get; init; } = HostToolVerdict.Deny;

    /// <summary>The class the server holds the tool to; one this SDK does not know is <see cref="ToolClass.Unknown"/>.</summary>
    public ToolClass ToolClass { get; init; } = ToolClass.Unknown;

    /// <summary>The level the server applied; one this SDK does not know is <see cref="ToolMode.Off"/>, the narrowest.</summary>
    public ToolMode EffectiveToolMode { get; init; } = ToolMode.Off;

    /// <summary>An administrator's rule asks about this call: the person is asked for this call alone.</summary>
    public bool RequiredByRule { get; init; } = true;

    public string Message { get; init; } = "";

    public string ProfileStamp { get; init; } = "";

    public string ArgumentsStamp { get; init; } = "";

    public string DecisionStamp { get; init; } = "";

    /// <summary>Who the server answered for.</summary>
    public AgentPrincipal Principal { get; init; } = new();
}

/// <summary>
/// Strict JSON for host commands: sorted keys, no spaces, only JSON values -- the same text for the same value, as the server
/// stamps it (sorted keys, compact, no escaping of non-ASCII), so a copy taken before a review can be compared after it.
/// </summary>
internal static class CommandJson
{
    /// <summary>The canonical text of any value the JSON serializer knows; a NaN, an infinity or a cycle throws.</summary>
    public static string Of(object? value)
    {
        var element = value is JsonElement e ? e : JsonSerializer.SerializeToElement(value, FadenstackJson.Options);
        var text = new StringBuilder();
        Write(text, element);
        return text.ToString();
    }

    /// <summary>The canonical text of a parsed value.</summary>
    public static string Of(JsonElement element)
    {
        var text = new StringBuilder();
        Write(text, element);
        return text.ToString();
    }

    /// <summary>The SHA-256 of a tool's parameter schema, hex: the server's <c>schema_sha256</c> of the same schema.</summary>
    public static string SchemaHash(JsonElement schema) => ContentHash.BytesSha256(Encoding.UTF8.GetBytes(Of(schema)));

    private static void Write(StringBuilder text, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                text.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        text.Append(',');
                    }
                    first = false;
                    WriteString(text, property.Name);
                    text.Append(':');
                    Write(text, property.Value);
                }
                text.Append('}');
                break;
            case JsonValueKind.Array:
                text.Append('[');
                var comma = false;
                foreach (var item in element.EnumerateArray())
                {
                    if (comma)
                    {
                        text.Append(',');
                    }
                    comma = true;
                    Write(text, item);
                }
                text.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(text, element.GetString()!);
                break;
            case JsonValueKind.Number:
                text.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                text.Append("true");
                break;
            case JsonValueKind.False:
                text.Append("false");
                break;
            case JsonValueKind.Null:
                text.Append("null");
                break;
            default:
                throw new ArgumentException("Command arguments must contain only JSON values.");
        }
    }

    private static void WriteString(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        text.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(c);
                    }
                    break;
            }
        }
        text.Append('"');
    }
}
