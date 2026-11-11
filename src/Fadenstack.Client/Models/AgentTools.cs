namespace Fadenstack.Models;

/// <summary>
/// A tool of one of the organisation's MCP servers, as the agent's server lists it for the signed-in user
/// (<see cref="Resources.AgentResource.ListToolsAsync"/>): names and descriptions only, never how the server is reached.
/// </summary>
public sealed record AgentToolInfo
{
    /// <summary>The name the model calls it by, such as <c>mcp.github.search</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>The MCP server that brings it.</summary>
    public string Server { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>Whether this agent's tool rules let its requests be offered and run the tool.</summary>
    public bool Allowed { get; init; }

    /// <summary>Whether the tool is switched on (an administrator may have switched it off).</summary>
    public bool Enabled { get; init; }
}

/// <summary><c>GET /v1/agents/{agent}/tools</c> as the gateway sends it: <c>{"tools":[...]}</c>.</summary>
internal sealed record AgentToolListWire
{
    public List<AgentToolInfo>? Tools { get; init; }
}
