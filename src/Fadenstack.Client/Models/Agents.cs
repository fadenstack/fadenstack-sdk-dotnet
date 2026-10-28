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
    /// <summary>What a session starts in: <c>ask</c>.</summary>
    public string Default { get; init; } = "ask";

    /// <summary>The most this user may pick for this agent, if the server sets a limit.</summary>
    public string? Ceiling { get; init; }

    public bool OpenWorldEnforced { get; init; }
}

/// <summary><c>GET /v1/features</c>: the server's edition and features.</summary>
public sealed record ServerFeatures
{
    public string Edition { get; init; } = "community";
    public List<string> Features { get; init; } = new();
}
