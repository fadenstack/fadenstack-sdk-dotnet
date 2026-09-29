using Faden.Models;

namespace Faden.Mcp;

/// <summary>Whether a local server may be attached, and why (shown to the user when it is not).</summary>
public sealed record LocalMcpDecision(bool Allowed, string Reason);

/// <summary>
/// Decides which local MCP servers an agent may attach. Core: the agent's mode (<c>disable</c> by default,
/// <c>allow</c>) and the user's developer mode. <c>Faden.Enterprise</c> replaces it for Filter and the
/// per-agent lock. Local configuration can only tighten this, never widen it.
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
            "filter" => new LocalMcpDecision(false, "The agent allows listed local MCP servers only (needs Faden.Enterprise)."),
            _ => new LocalMcpDecision(false, "The agent does not allow local MCP servers."),
        };
    }
}
