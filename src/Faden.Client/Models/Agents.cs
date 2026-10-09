using System.Text.Json;
using System.Text.Json.Serialization;

namespace Faden.Models;

/// <summary>What the SDK caches about its agent (<c>GET /v1/agents/{agent}/profile</c>): never its instructions.</summary>
public sealed record AgentProfile
{
    public string Id { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public int? Version { get; init; }

    /// <summary>A maintainer's draft, not the published version.</summary>
    public bool Draft { get; init; }

    public string? Model { get; init; }

    /// <summary>Whether the agent's model sees pictures: true, false, or null when nobody said (sdk/10 §4).</summary>
    public bool? Vision { get; init; }

    /// <summary>Whether the server has an image model the agent's tools can use (sdk/11).</summary>
    public bool Images { get; init; }

    public string? InstructionsSha256 { get; init; }

    /// <summary>The tools the host application may offer; any other tool is refused.</summary>
    public List<ToolContractEntry> HostToolContract { get; init; } = new();

    public List<string>? ServerTools { get; init; }
    public LocalMcpSettings LocalMcp { get; init; } = new();
    public ToolModeSettings ToolModes { get; init; } = new();
    public Dictionary<string, JsonElement>? Ui { get; init; }
    public Dictionary<string, JsonElement>? Extensions { get; init; }

    /// <summary>SDK modules the agent needs (e.g. <c>pii.local</c>, EE); the SDK refuses to run without them.</summary>
    public List<string> RequiredClientModules { get; init; } = new();

    public string Edition { get; init; } = "community";
    public List<string> Features { get; init; } = new();
}

/// <summary>One tool of an agent's host tool contract.</summary>
public sealed record ToolContractEntry
{
    public string Name { get; init; } = "";

    /// <summary><c>read</c>, <c>write</c> or <c>destructive</c>: the tool's class (sdk/07). Absent: unknown.</summary>
    public string? Risk { get; init; }

    [JsonPropertyName("schema_sha256")]
    public string? SchemaSha256 { get; init; }

    public string? Approval { get; init; }
}

public sealed record LocalMcpSettings
{
    /// <summary><c>disable</c> (default) or <c>allow</c>; EE adds <c>filter</c>.</summary>
    public string Mode { get; init; } = "disable";

    /// <summary>The user's developer mode: any local MCP server, for themselves.</summary>
    public bool DeveloperMode { get; init; }
}

public sealed record ToolModeSettings
{
    /// <summary>What a session starts in: <c>ask</c>.</summary>
    public string Default { get; init; } = "ask";

    /// <summary>The most this user may pick for this agent (EE), or none.</summary>
    public string? Ceiling { get; init; }

    public bool OpenWorldEnforced { get; init; }
}

/// <summary><c>GET /v1/features</c>: the server's edition and features.</summary>
public sealed record ServerFeatures
{
    public string Edition { get; init; } = "community";
    public List<string> Features { get; init; } = new();
}
