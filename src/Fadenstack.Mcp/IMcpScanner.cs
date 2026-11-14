namespace Fadenstack.Mcp;

/// <summary>A range of loopback ports to look for MCP servers on, both ends included.</summary>
public readonly record struct McpPortRange(int From, int To);

/// <summary>What a scan looks at (desktop-agent/32 §3); the person chooses on the scan sheet.</summary>
public sealed record McpScanOptions(bool OtherApps = true, bool ThisComputer = true, IReadOnlyList<McpPortRange>? Ports = null)
{
    /// <summary>The ports looked at unless the person names others: the ranges local MCP servers use most.</summary>
    public static IReadOnlyList<McpPortRange> DefaultPorts { get; } = [new(3000, 3999), new(8000, 8999)];
}

/// <summary>What the person can do with a finding.</summary>
public enum McpFindingState
{
    /// <summary>Not among the person's servers: it can be ticked and added.</summary>
    New,

    /// <summary>The person has a server that starts the same program with the same arguments, or has the same address.</summary>
    AlreadyAdded,

    /// <summary>An address that is not on this computer: "Online servers come in a later version" (31 §9 decision 1).</summary>
    NetworkLater,

    /// <summary>It can be added, but a value it needs is empty (VS Code <c>${input:...}</c>); it is added switched off.</summary>
    NeedsValue,

    /// <summary>The entry could not be read; shown with the reason, not addable.</summary>
    NotUnderstood,
}

/// <summary>
/// One MCP server a scan found: in another app's settings (<see cref="Source"/> set) or answering on a loopback port
/// (<see cref="Source"/> null). Never carries a secret's value in what a view shows: <see cref="SecretNames"/> are names only;
/// the values travel inside <see cref="Entry"/>, which prints none of them.
/// </summary>
public sealed record McpFinding(
    string Label,
    string FoundIn,
    McpImportSource? Source,
    McpServerEntry? Entry,
    McpFindingState State,
    string Where,
    IReadOnlyList<string> SecretNames,
    string? ServerName = null,
    int? ToolCount = null,
    string? Detail = null);

/// <summary>One app the scan looked in: whether it is on this computer, and how many servers it had.</summary>
public sealed record McpScanApp(McpImportSource Source, string Name, bool Installed, int Found);

/// <summary>What a scan found. <paramref name="CutShort"/>: it hit the 30 s limit, so the findings are those found by then.</summary>
public sealed record McpScanResult(IReadOnlyList<McpScanApp> Apps, int PortsChecked, IReadOnlyList<McpFinding> Findings, TimeSpan Took, bool CutShort = false);

/// <summary>How far a scan is: <see cref="Done"/> of <see cref="Total"/> steps (apps read, then ports checked).</summary>
public sealed record McpScanProgress(int Done, int Total);

/// <summary>
/// Looks for MCP servers on this computer (desktop-agent/32): in the settings of other apps that use MCP, and on loopback
/// ports with an MCP handshake. Runs only when the person asks; reads and probes, never starts a program, never sends
/// anything off this computer.
/// </summary>
public interface IMcpScanner
{
    /// <summary>The apps it can look in on this operating system, in the order the sheet lists them.</summary>
    IReadOnlyList<McpImportSource> Apps { get; }

    /// <param name="options">What to look at.</param>
    /// <param name="current">The person's servers, to mark findings already added.</param>
    /// <param name="progress">Steps done, for the sheet's progress.</param>
    /// <param name="cancellationToken">The sheet's Cancel.</param>
    Task<McpScanResult> ScanAsync(McpScanOptions options, IReadOnlyList<McpServerEntry> current, IProgress<McpScanProgress>? progress,
        CancellationToken cancellationToken);
}
