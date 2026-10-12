using System.Globalization;
using System.Runtime.InteropServices;

namespace Fadenstack.Workspaces;

/// <summary>
/// One running instance of an application for its whole life: the application, the process id and the process
/// start time. Process ids are reused; with the start time a key never points at a different process, so two
/// instances of a multi-instance application (two Excels, two Words) are never confused.
/// </summary>
public readonly record struct InstanceKey(string App, int ProcessId, long StartTicks)
{
    public static InstanceKey For(string app, int processId, DateTime started) =>
        new(app, processId, started.ToUniversalTime().Ticks);

    public override string ToString() =>
        App + ":" + ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + StartTicks.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out InstanceKey key)
    {
        key = default;
        var parts = text?.Split(':');
        if (parts is not { Length: 3 } || parts[0].Length == 0
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
        {
            return false;
        }
        key = new InstanceKey(parts[0], pid, ticks);
        return true;
    }
}

/// <summary>What the framework knows about an application before it talks to it.</summary>
public sealed record ApplicationInfo
{
    /// <summary>Short id used in tool arguments and workspace references: <c>word</c>, <c>excel</c>.</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>Process names without extension (<c>WINWORD</c>).</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];

    /// <summary>File types the application opens (<c>.docx</c>, <c>.ap19</c>).</summary>
    public IReadOnlyList<string> FileTypes { get; init; } = [];

    /// <summary>Whether several instances can run at once (Word, Excel: yes; PowerPoint: no).</summary>
    public bool MultiInstance { get; init; } = true;
}

/// <summary>A running instance and the Fadenstack agents active in it.</summary>
public sealed record AppInstance
{
    public InstanceKey Key { get; init; }

    /// <summary>Short, stable while the directory lives, unique: <c>word#2</c>. What the model sees and passes back.</summary>
    public string Handle { get; init; } = "";

    public string App => Key.App;

    public int ProcessId => Key.ProcessId;

    public DateTime StartedUtc => new(Key.StartTicks, DateTimeKind.Utc);

    public string? WindowTitle { get; init; }

    public IReadOnlyList<AgentPresence> Agents { get; init; } = [];
}

/// <summary>
/// What an application works on: a file or project, or a resource that is not a file (a mailbox). Most
/// applications with an SDK have one -- the document, the project, the workspace. <see cref="Location"/> is a
/// full path for files, <c>unsaved:Document2</c> for a document not yet saved, or the provider's own form.
/// <see cref="Instance"/> is set when it matters which instance has it open.
/// </summary>
public readonly record struct WorkspaceRef(string App, string Location, InstanceKey? Instance = null)
{
    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public bool IsUnsaved => Location.StartsWith("unsaved:", StringComparison.Ordinal);

    /// <summary>The file name or the unsaved document's name.</summary>
    public string Name => IsUnsaved ? Location.Substring("unsaved:".Length) : Path.GetFileName(Location);

    /// <summary>Same application and location, whichever instance.</summary>
    public bool SameLocation(WorkspaceRef other) =>
        string.Equals(App, other.App, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Normalize(Location), Normalize(other.Location), PathComparison);

    /// <summary>A file path made absolute and tidy; other locations unchanged.</summary>
    public static string Normalize(string location)
    {
        if (location.StartsWith("unsaved:", StringComparison.Ordinal) || !LooksLikePath(location))
        {
            return location;
        }
        try
        {
            return Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return location;
        }
    }

    internal static bool LooksLikePath(string location) =>
        location.IndexOf('\\') >= 0 || location.IndexOf('/') >= 0 || (location.Length > 1 && location[1] == ':');

    internal string Key => App.ToLowerInvariant() + "|" + Normalize(Location);
}

/// <summary>A workspace that is open somewhere, with who is working on it.</summary>
public sealed record WorkspaceInfo
{
    public WorkspaceRef Ref { get; init; }

    /// <summary>The handle of the instance it is open in (<c>word#2</c>).</summary>
    public string InstanceHandle { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>The instance's active document.</summary>
    public bool IsActive { get; init; }

    /// <summary>Unsaved changes; null when the provider cannot tell.</summary>
    public bool? IsDirty { get; init; }

    public bool IsReadOnly { get; init; }

    /// <summary>Who holds the write lease right now, if anyone.</summary>
    public LeaseInfo? Lease { get; init; }

    /// <summary>The Fadenstack agents attached to this workspace in this instance.</summary>
    public IReadOnlyList<AgentPresence> Agents { get; init; } = [];
}

public enum AgentStatus
{
    Idle,
    Busy,
}

/// <summary>
/// A Fadenstack agent active on this device, as it registered itself (<see cref="DeviceAgentRegistry"/>). The
/// agent's loop may run in the instance itself (an add-in) or in a service next to it (a thin host), so the
/// instance and the host process are separate.
/// </summary>
public sealed record AgentPresence
{
    /// <summary>The server agent's slug: <c>word-assistant</c>.</summary>
    public string Agent { get; init; } = "";

    /// <summary>The instance it works in (<see cref="InstanceKey"/> text); empty for the desktop agent.</summary>
    public string Instance { get; init; } = "";

    /// <summary>The host's window key, for hosts with an agent per window (Office panes).</summary>
    public string? Window { get; init; }

    /// <summary>Locations of the workspaces it is attached to.</summary>
    public IReadOnlyList<string> Workspaces { get; init; } = [];

    public AgentStatus Status { get; init; }

    /// <summary>A pipe name, when the agent accepts delegation or lends tools; null otherwise.</summary>
    public string? Bridge { get; init; }

    /// <summary>The process the agent's loop runs in, and its start time (to tell a restarted process).</summary>
    public int HostProcessId { get; init; }

    public long HostStartTicks { get; init; }

    public DateTime HeartbeatUtc { get; init; }
}

/// <summary>A write lease on a workspace (<see cref="DocumentLeases"/>).</summary>
public sealed record LeaseInfo
{
    public string Id { get; init; } = "";

    /// <summary>Who: an agent slug, or a tool name for leases taken outside an agent.</summary>
    public string Holder { get; init; } = "";

    public int HolderProcessId { get; init; }

    public long HolderStartTicks { get; init; }

    public DateTime ExpiresUtc { get; init; }
}
