using System.Text;
using Fadenstack.Tools;

namespace Fadenstack.Mcp;

/// <summary>
/// A name and a value the person typed for a server: an environment variable of a program, or a header of an address. The value
/// is a secret (tokens live here): it is kept encrypted, shown as dots once saved, and never written to a log or a report, so
/// <see cref="ToString"/> and the record's printing leave it out.
/// </summary>
public sealed record McpSecretPair(string Name, string Value)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Name = ").Append(Name);
        return true;
    }

    public override string ToString() => $"{Name}=***";
}

/// <summary>
/// One MCP server of the person, as kept on this computer (desktop-agent/31 §4.4): a program (command, arguments as a list, a
/// working folder, environment) or an address on this computer (URL, headers), switched on or off, with the longest a call may
/// take and where the entry came from. Which of the two it is, is whether <see cref="Command"/> is set.
/// </summary>
public sealed record McpServerEntry
{
    /// <summary>The label for entries typed in.</summary>
    public const string TypedOrigin = "typed";

    /// <summary>Lower case letters, digits and hyphens, at most 32 characters, unique among the person's servers.</summary>
    public string Label { get; init; } = "";

    public bool Enabled { get; init; } = true;

    /// <summary>The program, for a server started on this computer; null for an address.</summary>
    public string? Command { get; init; }

    /// <summary>The program's arguments, one list entry each, never one shell string.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The folder the program starts in; null: the app's.</summary>
    public string? WorkingFolder { get; init; }

    /// <summary>Environment variables added to the program's.</summary>
    public IReadOnlyList<McpSecretPair> Environment { get; init; } = [];

    /// <summary>The address, for a server that is already running on this computer.</summary>
    public string? Url { get; init; }

    /// <summary>Headers sent with every request to the address.</summary>
    public IReadOnlyList<McpSecretPair> Headers { get; init; } = [];

    /// <summary>The longest one call may take, in seconds; the SDK cuts it to 10 minutes.</summary>
    public int CallTimeoutSeconds { get; init; } = 120;

    /// <summary><see cref="TypedOrigin"/>, or the file it was imported from ("Claude Desktop", "VS Code", "Cursor", ".mcp.json").</summary>
    public string Origin { get; init; } = TypedOrigin;

    /// <summary>
    /// The person has marked this server trusted: only then does its <c>readOnlyHint</c> count for a tool's class (sdk/51 F05, H4).
    /// A server's own word is never enough. Imported servers arrive not trusted, like they arrive off.
    /// </summary>
    public bool Trusted { get; init; }

    /// <summary>
    /// The person's raise of single tools, by the server's own tool name: <see cref="ToolClass.Read"/> or <see cref="ToolClass.Write"/>
    /// (nothing else is kept). It never lowers a tool the server says is destructive. Empty: every tool asks, as it arrives.
    /// </summary>
    public IReadOnlyDictionary<string, ToolClass> ToolClasses { get; init; } = new Dictionary<string, ToolClass>();

    /// <summary>
    /// The person's switch under Advanced, "Allow plain http on the local network" (sdk/51 F05), for an address over plain http:
    /// then it may be on the local network (not only this computer), never with a header, and only where the agent and this
    /// computer's policy allow it. Off by default; imported servers arrive with it off.
    /// </summary>
    public bool PlainHttpInLan { get; init; }

    public bool IsProgram => Command is not null;

    /// <summary>The entry has a secret value to keep or show masked.</summary>
    public bool HasSecrets => Environment.Count > 0 || Headers.Count > 0;

    /// <summary>What a Tools page shows of where it is: the program's name or the address, never arguments, environment or headers.</summary>
    public string Where => IsProgram ? Path.GetFileName(Command!.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : Command! : Url ?? "";

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Label = ").Append(Label).Append(", Enabled = ").Append(Enabled);
        return true;
    }

    public override string ToString() => $"McpServerEntry {{ Label = {Label}, Enabled = {Enabled} }}";
}
