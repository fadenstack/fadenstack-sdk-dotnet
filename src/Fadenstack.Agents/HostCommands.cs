using System.Text.Json;
using Fadenstack.Models;

namespace Fadenstack.Agents;

/// <summary>What goes with a host command (<see cref="ChatSession.InvokeToolAsync"/>).</summary>
public sealed record HostCommandOptions
{
    /// <summary><see cref="HostToolOrigin.UserCommand"/> (a button, a view) or <see cref="HostToolOrigin.UserHistory"/> (an undo, a restore).</summary>
    public HostToolOrigin Origin { get; init; } = HostToolOrigin.UserCommand;

    /// <summary>The longest display label, in characters.</summary>
    public const int MaxLabelLength = 200;

    /// <summary>
    /// Checks a display label -- one line, not blank, at most <see cref="MaxLabelLength"/> characters -- and returns it trimmed
    /// (null when there is none). An <see cref="ArgumentException"/> for a label that is not.
    /// </summary>
    public static string? CheckLabel(string? label)
    {
        if (label is null)
        {
            return null;
        }
        if (label.Trim().Length == 0 || label.Length > MaxLabelLength || label.Any(c => c < 0x20 || c == 0x7f))
        {
            throw new ArgumentException("A command label must be a single line of at most 200 characters.", nameof(label));
        }
        return label.Trim();
    }

    /// <summary>
    /// Single-line local display text (at most 200 characters): what the chat shows for the command and the transcript keeps.
    /// Never an argument, a grant or a message to the model.
    /// </summary>
    public string? Label { get; init; }
}

/// <summary>How a host command ended: the tool's <c>ok</c>, <c>error</c> or <c>denied</c>, and what it returned.</summary>
public sealed record HostCommandResult(string TurnId, string CallId, string Outcome, string? Result);

/// <summary>Rules of host commands that do not depend on a session.</summary>
internal static class HostCommands
{
    private static readonly System.Text.RegularExpressions.Regex ToolName = new("^[A-Za-z0-9_-]{1,64}$");

    public static void CheckName(string? name)
    {
        if (name is null || !ToolName.IsMatch(name))
        {
            throw new ArgumentException("A valid host tool name is required.", nameof(name));
        }
    }

    public static string Wire(HostToolOrigin origin) => origin switch
    {
        HostToolOrigin.UserHistory => "user_history",
        HostToolOrigin.ModelTool => "model_tool",
        _ => "user_command",
    };

    /// <summary>
    /// Whether the dispatch answer differs from the prepare answer in a way that stops the call: a denial, another profile,
    /// arguments or decision stamp, or another principal than the prepare answer's and the agent's own.
    /// </summary>
    public static bool Drifted(HostToolDecision prepared, HostToolDecision dispatched, AgentPrincipal? agent) =>
        dispatched.Decision == HostToolVerdict.Deny
        || dispatched.ProfileStamp != prepared.ProfileStamp
        || dispatched.ArgumentsStamp != prepared.ArgumentsStamp
        || dispatched.DecisionStamp != prepared.DecisionStamp
        || dispatched.Principal != prepared.Principal
        || dispatched.Principal != agent;

    /// <summary>
    /// The arguments against the tool's schema: required ones present, none unknown where the schema forbids it, the types
    /// and enums that are stated. A problem as one sentence; null when they fit.
    /// </summary>
    public static string? CheckArguments(JsonElement schema, JsonElement arguments)
    {
        var problems = new List<string>();
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!))
            {
                if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    problems.Add($"'{name}' is required");
                }
            }
        }
        var properties = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : (JsonElement?)null;
        var closed = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False;
        foreach (var argument in arguments.EnumerateObject())
        {
            if (properties is null || !properties.Value.TryGetProperty(argument.Name, out var property))
            {
                if (closed)
                {
                    problems.Add($"'{argument.Name}' is not a parameter");
                }
                continue;
            }
            if (argument.Value.ValueKind == JsonValueKind.Null || property.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (property.TryGetProperty("type", out var type))
            {
                var types = type.ValueKind == JsonValueKind.Array
                    ? type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList()
                    : type.ValueKind == JsonValueKind.String ? new List<string> { type.GetString()! } : new List<string>();
                if (types.Count > 0 && !types.Any(t => IsType(argument.Value, t)))
                {
                    problems.Add($"'{argument.Name}' must be {string.Join(" or ", types)}");
                }
            }
            if (property.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array
                && !allowed.EnumerateArray().Any(e => CommandJson.Of(e) == CommandJson.Of(argument.Value)))
            {
                problems.Add($"'{argument.Name}' must be one of {string.Join(", ", allowed.EnumerateArray().Select(e => CommandJson.Of(e)))}");
            }
        }
        return problems.Count == 0 ? null : $"Invalid arguments: {string.Join("; ", problems)}.";
    }

    private static bool IsType(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && (value.TryGetInt64(out _) || IsWhole(value)),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static bool IsWhole(JsonElement number) => number.TryGetDouble(out var d) && Math.Floor(d) == d && !double.IsInfinity(d);
}
