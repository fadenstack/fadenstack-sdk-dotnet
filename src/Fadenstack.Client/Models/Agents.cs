using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fadenstack.Models;

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

    /// <summary>Whether the agent's model sees pictures: true, false, or null when nobody said.</summary>
    public bool? Vision { get; init; }

    /// <summary>Whether the server has an image model the agent's tools can use.</summary>
    public bool Images { get; init; }

    /// <summary>
    /// Whether the user can pick skills for a message (<see cref="Resources.AgentResource.ListSkillsAsync"/>): the server
    /// runs the skills module.
    /// </summary>
    public bool Skills { get; init; }

    public string? InstructionsSha256 { get; init; }

    /// <summary>The tools the host application may offer; any other tool is refused.</summary>
    public List<ToolContractEntry> HostToolContract { get; init; } = new();

    public List<string>? ServerTools { get; init; }
    public LocalMcpSettings LocalMcp { get; init; } = new();
    public ToolModeSettings ToolModes { get; init; } = new();

    /// <summary>
    /// What the agent's approval card may offer: its standing approvals, the longest grant, the classes a grant may
    /// cover. Null from a server without grants (no <c>approval_scopes.grant</c> in <see cref="Features"/>).
    /// </summary>
    public ApprovalSettings? Approvals { get; init; }

    public Dictionary<string, JsonElement>? Ui { get; init; }
    public Dictionary<string, JsonElement>? Extensions { get; init; }

    /// <summary>SDK modules the agent needs (e.g. <c>pii.local</c>, from an extension); the SDK refuses to run without them.</summary>
    public List<string> RequiredClientModules { get; init; } = new();

    public string Edition { get; init; } = "community";
    public List<string> Features { get; init; } = new();

    /// <summary>The signed-in user's own settings, shared by all their clients and the console.</summary>
    public UserSettings User { get; init; } = new();

    /// <summary>
    /// Who the agent's token is for. Null from an older gateway, or when it names no tenant, user and agent: the user is
    /// never inferred from the token.
    /// </summary>
    [JsonConverter(typeof(AgentPrincipalConverter))]
    public AgentPrincipal? Principal { get; init; }
}

/// <summary>The tenant, user and agent a token stands for (<c>principal</c> in the profile), and the device it was issued to.</summary>
public sealed record AgentPrincipal
{
    public string TenantId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string AgentId { get; init; } = "";
    public string? DeviceId { get; init; }
}

/// <summary>Reads <c>principal</c>: null unless tenant, user and agent are all non-empty strings.</summary>
internal sealed class AgentPrincipalConverter : JsonConverter<AgentPrincipal?>
{
    public override AgentPrincipal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string? Text(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
        var (tenant, user, agent) = (Text("tenant_id"), Text("user_id"), Text("agent_id"));
        return tenant is null || user is null || agent is null
            ? null
            : new AgentPrincipal { TenantId = tenant, UserId = user, AgentId = agent, DeviceId = Text("device_id") };
    }

    public override void Write(Utf8JsonWriter writer, AgentPrincipal? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("tenant_id", value.TenantId);
        writer.WriteString("user_id", value.UserId);
        writer.WriteString("agent_id", value.AgentId);
        writer.WriteString("device_id", value.DeviceId);
        writer.WriteEndObject();
    }
}

/// <summary>
/// What every client of a user shares (<c>user</c> in the profile; <see cref="Resources.AgentResource.SetUserLanguageAsync"/>
/// changes it).
/// </summary>
public sealed record UserSettings
{
    /// <summary>The language the user picked, in any client or the console (a BCP 47 tag); null: each client follows its host.</summary>
    public string? Language { get; init; }
}

/// <summary>One tool of an agent's host tool contract.</summary>
public sealed record ToolContractEntry
{
    public string Name { get; init; } = "";

    /// <summary><c>read</c>, <c>write</c> or <c>destructive</c>: the tool's class. Absent: unknown.</summary>
    public string? Risk { get; init; }

    [JsonPropertyName("schema_sha256")]
    public string? SchemaSha256 { get; init; }

    public string? Approval { get; init; }

    /// <summary>
    /// The JSON pointer (RFC 6901, e.g. <c>/path</c>) to the argument that names where a call acts; with
    /// <see cref="TargetType"/>. A grant for a stretch of work covers a tool's calls only in the places it names.
    /// </summary>
    public string? TargetArgument { get; init; }

    /// <summary><c>document</c>, <c>folder</c>, <c>site</c>, <c>app</c> or <c>desk</c>: what kind of place it names.</summary>
    public string? TargetType { get; init; }
}

/// <summary>What the agent's approval card may offer (the profile's <c>approvals</c>).</summary>
public sealed record ApprovalSettings
{
    /// <summary>
    /// The standing approvals the agent allows -- <c>chat</c>, <c>time</c>, <c>grant</c>, <c>always</c> -- or null:
    /// any of them.
    /// </summary>
    public List<string>? Standing { get; init; }

    /// <summary>The longest grant the agent allows, in minutes.</summary>
    public int LongestGrantMinutes { get; init; } = 120;

    /// <summary>The classes a grant may cover: <c>read</c>, <c>write</c>, or both.</summary>
    public List<string> GrantClasses { get; init; } = new() { "read", "write" };

    /// <summary>Whether the agent allows the standing approval <paramref name="kind"/> (<c>chat</c>, <c>time</c>...).</summary>
    public bool Allows(string kind) => Standing is null || Standing.Contains(kind);

    /// <summary>
    /// Bypass, as the server offers it to this client (sdk/40 A3): only to a client that announced it
    /// (<c>X-Faden-Client-Supports</c>); null from a server without Bypass.
    /// </summary>
    public BypassSettings? Bypass { get; init; }
}

/// <summary>Whether a person may choose Bypass with the agent now, and what its confirmation says (sdk/09 L-57, L-58).</summary>
public sealed record BypassSettings
{
    /// <summary>The organisation allows it, the agent's version goes up to it, and no ceiling is lower.</summary>
    public bool Allowed { get; init; }

    /// <summary>The longest a Bypass may run, in minutes.</summary>
    public int LongestMinutes { get; init; } = 120;

    /// <summary>
    /// What the server records of every call under it, as an administrator may set it (sdk/09 L-60): <c>decisions</c>
    /// (which tool ran, when, where), <c>calls</c> (also the arguments, after the PII masking; the default) or
    /// <c>results</c> (also a short summary of each result). The confirmation says it in plain words.
    /// </summary>
    public string Evidence { get; init; } = "calls";

    /// <summary>
    /// Switching it on needs a fresh sign-in in the browser (sdk/41): offered only to a client that announces
    /// <c>bypass.step_up</c>, so a server that sets <see cref="Allowed"/> sends this too.
    /// </summary>
    public StepUpSettings? StepUp { get; init; }
}

/// <summary>The second sign-in before Bypass (sdk/41; OWASP ASVS 5.0 7.5.3), as the server describes it.</summary>
public sealed record StepUpSettings
{
    /// <summary>Always true: no setting switches it off.</summary>
    public bool Required { get; init; } = true;

    /// <summary>How old the sign-in may be when the gateway makes the grant, in seconds (300).</summary>
    public int MaxAge { get; init; } = 300;

    /// <summary>Where the app starts one (the backend's <c>/api/agent-tokens/step-ups</c>).</summary>
    public string Start { get; init; } = "/api/agent-tokens/step-ups";
}

public sealed record LocalMcpSettings
{
    /// <summary><c>disable</c> (default) or <c>allow</c>; another mode needs a policy that knows it (<c>ILocalMcpPolicy</c> in Fadenstack.Mcp).</summary>
    public string Mode { get; init; } = "disable";

    /// <summary>The user's developer mode: any local MCP server, for themselves.</summary>
    public bool DeveloperMode { get; init; }
}

public sealed record ToolModeSettings
{
    /// <summary>What a session starts in: the agent's default level (<c>ask</c> unless its author set another).</summary>
    public string Default { get; init; } = "ask";

    /// <summary>
    /// The most this user may pick for this agent, if the server sets a limit: the narrowest of the agent's highest level
    /// and the organisation's ceilings.
    /// </summary>
    public string? Ceiling { get; init; }

    /// <summary>The agent's highest level, as its author set it (<c>auto</c> unless set); null from an older server.</summary>
    public string? Max { get; init; }

    public bool OpenWorldEnforced { get; init; }
}

/// <summary><c>GET /v1/features</c>: the server's edition and features.</summary>
public sealed record ServerFeatures
{
    public string Edition { get; init; } = "community";
    public List<string> Features { get; init; } = new();
}
