namespace Fadenstack.Mcp;

/// <summary>Where a local server is, for a host's Tools page.</summary>
public enum McpServerState
{
    /// <summary>Switched off by the person. Nothing runs.</summary>
    Off,

    /// <summary>Starting (or starting again after a crash).</summary>
    Starting,

    /// <summary>Connected; its tools are known.</summary>
    Running,

    /// <summary>Could not start, or stopped for good; <see cref="McpServerStatus.Detail"/> is the last error.</summary>
    Failed,

    /// <summary>The agent's policy does not allow it; <see cref="McpServerStatus.Detail"/> is the policy's reason. Never started.</summary>
    NotAllowed,
}

/// <summary>
/// What a server says about one of its tools (annotations). A server's word about itself: shown to the person, never
/// used for the tool's class.
/// </summary>
public sealed record McpToolHints(string? Title, bool? ReadOnly, bool? Destructive, bool? Idempotent, bool? OpenWorld);

/// <summary>A tool a server brought, for a host's Tools page. The description is cleaned of hidden characters and capped.</summary>
public sealed record McpToolInfo(string Name, string Description, McpToolHints Hints);

/// <summary>A local server as a host shows it.</summary>
public sealed record McpServerStatus(string Label, McpServerState State)
{
    /// <summary>The last error (<see cref="McpServerState.Failed"/>) or the policy's reason (<see cref="McpServerState.NotAllowed"/>).</summary>
    public string? Detail { get; init; }

    /// <summary>The first line of the server's instructions: at most 80 characters, hidden characters removed. Null when it has none.</summary>
    public string? Instructions { get; init; }

    public IReadOnlyList<McpToolInfo> Tools { get; init; } = Array.Empty<McpToolInfo>();

    /// <summary>The server changed its tools after it was attached (<c>notifications/tools/list_changed</c>), and the new list differs.</summary>
    public bool ToolsChanged { get; init; }

    /// <summary>How often it was started again after a crash.</summary>
    public int Reconnects { get; init; }
}

/// <summary>Raised when a server's status changed, or it was added or removed.</summary>
public sealed class McpServerStatusEventArgs : EventArgs
{
    public McpServerStatusEventArgs(string label, McpServerStatus? status)
    {
        Label = label;
        Status = status;
    }

    public string Label { get; }

    /// <summary>The new status; null when the server was removed.</summary>
    public McpServerStatus? Status { get; }
}

/// <summary>A folder (or other root) the host tells servers they may work in. A coordination hint, not a fence.</summary>
public sealed record McpRoot(string Uri, string? Name = null);

/// <summary>The kinds of field a server's question can have.</summary>
public enum McpFieldKind
{
    Text,
    Number,
    Integer,
    Boolean,
    Choice,
}

/// <summary>One option of a <see cref="McpFieldKind.Choice"/> field.</summary>
public sealed record McpChoice(string Value, string Title);

/// <summary>One field of a server's question. All texts are the server's words, cleaned of hidden characters and capped.</summary>
public sealed record McpQuestionField(string Name, string? Title, string? Description, McpFieldKind Kind, bool Required,
    IReadOnlyList<McpChoice> Choices);

/// <summary>
/// A question an MCP server asks the person (elicitation, form mode). <see cref="Server"/> is the label the person gave the
/// server; <see cref="Message"/> and the fields are the server's words. The session and turn are those of the call in flight
/// (empty when the server asks outside a call).
/// </summary>
public sealed record McpQuestion(string SessionId, string TurnId, string Server, string Message, IReadOnlyList<McpQuestionField> Fields);

/// <summary>How the person answered.</summary>
public enum McpAnswerKind
{
    /// <summary>They filled the form in; <see cref="McpAnswer.Values"/> are the values.</summary>
    Accept,

    /// <summary>They said no.</summary>
    Decline,

    /// <summary>They dismissed it without choosing.</summary>
    Cancel,
}

/// <summary>The person's answer to an <see cref="McpQuestion"/>. Values: strings, numbers, booleans, by field name.</summary>
public sealed record McpAnswer(McpAnswerKind Kind, IReadOnlyDictionary<string, object?>? Values = null)
{
    public static McpAnswer Accept(IReadOnlyDictionary<string, object?> values) => new(McpAnswerKind.Accept, values);

    public static McpAnswer Decline() => new(McpAnswerKind.Decline);

    public static McpAnswer Cancel() => new(McpAnswerKind.Cancel);
}

/// <summary>Progress of a running call, for the turn's step line. <see cref="Message"/> is the server's word.</summary>
public sealed record McpProgress(string SessionId, string TurnId, string Server, string Tool, string? Message, float Progress, float? Total);
