using System.Text.Json;
using System.Text.RegularExpressions;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Agents;

/// <summary>
/// An agent's definition as an app ships it (its <c>contracts/*.json</c>, core's templates): the body the server's
/// <c>POST /api/agents</c> takes. In standalone mode it stands in for the server's profile (sdk/51 F03): the slug and name,
/// the instructions, the host tool contract, the local MCP and skills modes and the greeting. <c>model_alias</c> is ignored;
/// every other field is kept as it was (<see cref="Json"/>), so a definition from a newer app reads and writes back whole.
/// </summary>
/// <remarks>
/// Fail closed: a tool whose <c>risk</c> is missing or unknown is unclassified, so it asks every time; a mode other than
/// <c>allow</c> is <c>disable</c>. Reading never throws on such a value, only on a body that has no valid slug.
/// </remarks>
public sealed class AgentDefinition
{
    /// <summary>The server's rule for a slug (core <c>SLUG_PATTERN</c>): lower case, digits and inner hyphens, 1 to 64.</summary>
    /// <remarks><c>\z</c>, not <c>$</c>: in .NET <c>$</c> also matches before a final newline, which would let a slug with one through.</remarks>
    private static readonly Regex SlugPattern = new(@"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\z", RegexOptions.CultureInvariant);

    private AgentDefinition(JsonElement json, string slug)
    {
        Json = json;
        Slug = slug;
        Name = Text(json, "name") ?? slug;
        Description = Text(json, "description");
        var version = json.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        Instructions = Text(version, "instructions") ?? "";
        HostToolContract = Contract(version);
        ServerTools = Strings(version, "server_tools");
        LocalMcpMode = Mode(version, "local_mcp_mode");
        LocalSkillsMode = Mode(version, "local_skills_mode");
        Ui = Object(version, "ui");
    }

    /// <summary>The agent's slug: its data and sessions are kept under it.</summary>
    public string Slug { get; }

    /// <summary>The agent's name; its slug when the definition gives none.</summary>
    public string Name { get; }

    public string? Description { get; }

    /// <summary>The agent's own instructions (<c>version.instructions</c>); empty when it has none.</summary>
    public string Instructions { get; }

    /// <summary>
    /// The host tools the agent may offer, each with its class (<c>risk</c>) as written: one that is missing or unknown counts as
    /// unclassified (<see cref="ToolClass.Unknown"/>), so the tool asks.
    /// </summary>
    public IReadOnlyList<ToolContractEntry> HostToolContract { get; }

    /// <summary>The server's own tools the agent uses (<c>version.server_tools</c>); a server's only, so unused in standalone.</summary>
    public IReadOnlyList<string>? ServerTools { get; }

    /// <summary><c>allow</c> or <c>disable</c> (anything else, or nothing, is <c>disable</c>).</summary>
    public string LocalMcpMode { get; }

    /// <summary><c>allow</c> or <c>disable</c> (anything else, or nothing, is <c>disable</c>).</summary>
    public string LocalSkillsMode { get; }

    /// <summary>The greeting, suggestions, languages and the rest of <c>version.ui</c>, as written; null when it has none.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Ui { get; }

    /// <summary>The whole definition as it was read, unknown fields included.</summary>
    public JsonElement Json { get; }

    /// <summary>The definition as JSON, exactly as it was read (unknown fields included).</summary>
    public string ToJson() => Json.GetRawText();

    /// <summary>Reads a definition. Throws a <see cref="FormatException"/> when it is not a JSON object with a valid slug.</summary>
    public static AgentDefinition FromJson(string json)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            return FromJson(document.RootElement);
        }
        catch (JsonException e)
        {
            throw new FormatException("The agent definition is not valid JSON.", e);
        }
    }

    /// <summary>Reads a definition. Throws a <see cref="FormatException"/> when it is not a JSON object with a valid slug.</summary>
    public static AgentDefinition FromJson(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The agent definition must be a JSON object.");
        }
        var slug = Text(json, "slug");
        if (slug is null || !SlugPattern.IsMatch(slug))
        {
            throw new FormatException(
                "The agent definition needs a slug: lower-case letters, digits and inner hyphens, at most 64 characters.");
        }
        return new AgentDefinition(json.Clone(), slug);
    }

    private static List<ToolContractEntry> Contract(JsonElement version)
    {
        var entries = new List<ToolContractEntry>();
        if (version.ValueKind != JsonValueKind.Object || !version.TryGetProperty("host_tool_contract", out var contract)
            || contract.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }
        foreach (var item in contract.EnumerateArray())
        {
            if (Text(item, "name") is not { Length: > 0 } name)
            {
                continue; // an entry without a name names no tool
            }
            entries.Add(new ToolContractEntry
            {
                Name = name,
                // Kept as written: ToolModes.ParseClass reads anything it does not know as Unknown, so the tool asks.
                Risk = Text(item, "risk"),
                SchemaSha256 = Text(item, "schema_sha256"),
                Approval = Text(item, "approval"),
                TargetArgument = Text(item, "target_argument"),
                TargetType = Text(item, "target_type"),
            });
        }
        return entries;
    }

    private static string Mode(JsonElement version, string name) =>
        string.Equals(Text(version, name)?.Trim(), "allow", StringComparison.OrdinalIgnoreCase) ? "allow" : "disable";

    private static List<string>? Strings(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
    }

    private static Dictionary<string, JsonElement>? Object(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
