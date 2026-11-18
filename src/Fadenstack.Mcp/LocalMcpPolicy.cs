using Fadenstack.Agents;
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

/// <summary>
/// What the machine policy's <c>mcp</c> key says about a server (sdk/51 F09): forbidden, allowed, or a list of allowed
/// <em>sources</em>. A source is the program (its full path or its file name, without <c>.exe</c> on Windows) or the address of
/// a server (its origin, or its full URL). The server's label is the person's own word and never a source.
/// </summary>
public static class McpSources
{
    public static LocalMcpDecision Decide(PolicyRule rule, LocalMcpServer server)
    {
        if (rule is null)
        {
            throw new ArgumentNullException(nameof(rule));
        }
        switch (rule.Access)
        {
            case PolicyAccess.Allowed:
                return new LocalMcpDecision(true, "This computer's policy allows local MCP servers.");
            case PolicyAccess.Listed when Names(server).Any(rule.Allows):
                return new LocalMcpDecision(true, "This computer's policy lists this server's source.");
            case PolicyAccess.Listed:
                return new LocalMcpDecision(false, "This computer's policy does not list this server's source.");
            default:
                return new LocalMcpDecision(false, "This computer's policy does not allow local MCP servers.");
        }
    }

    /// <summary>
    /// The names a policy item can match: the program, its file name (and without an extension), or the address. The program is the
    /// file that will really start (<see cref="LocalMcpServer.ResolvedProgram"/>) when the command was found, so an item naming a
    /// program matches that file and not only the word the person typed; when it was not found, the command as given.
    /// </summary>
    internal static IEnumerable<string> Names(LocalMcpServer server)
    {
        if ((server.ResolvedProgram ?? server.Command) is { Length: > 0 } command)
        {
            yield return command;
            var name = System.IO.Path.GetFileName(command.TrimEnd('/', '\\').Replace('\\', '/'));
            if (name.Length > 0)
            {
                yield return name;
                var dot = name.LastIndexOf('.');
                if (dot > 0)
                {
                    yield return name.Substring(0, dot);
                }
            }
        }
        if (server.Url is { } url)
        {
            yield return url.AbsoluteUri;
            yield return url.GetLeftPart(UriPartial.Authority);
            yield return url.AbsoluteUri.TrimEnd('/');
        }
    }
}
