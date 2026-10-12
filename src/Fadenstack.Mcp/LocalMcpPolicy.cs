using Fadenstack.Models;

namespace Fadenstack.Mcp;

/// <summary>Whether a local server may be attached, and why (shown to the user when it is not).</summary>
public sealed record LocalMcpDecision(bool Allowed, string Reason);

/// <summary>
/// Decides which local MCP servers an agent may attach. The core policy knows the agent's modes <c>disable</c>
/// (the default) and <c>allow</c>, and the user's developer mode. An extension that brings further modes (a list
/// of allowed servers, a lock) replaces it with its own. Local configuration can only tighten this, never widen it.
/// </summary>
public interface ILocalMcpPolicy
{
    LocalMcpDecision Decide(LocalMcpServer server, AgentProfile profile);
}

public sealed class CoreLocalMcpPolicy : ILocalMcpPolicy
{
    public static CoreLocalMcpPolicy Instance { get; } = new();

    public LocalMcpDecision Decide(LocalMcpServer server, AgentProfile profile)
    {
        var settings = profile.LocalMcp;
        return settings.Mode switch
        {
            "allow" => new LocalMcpDecision(true, "The agent allows local MCP servers."),
            _ when settings.DeveloperMode => new LocalMcpDecision(true, "Developer mode."),
            null or "" or "disable" => new LocalMcpDecision(false, "The agent does not allow local MCP servers."),
            var other => new LocalMcpDecision(false, $"The agent's local MCP mode '{other}' needs a policy this app does not have."),
        };
    }
}
