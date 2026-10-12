using System.ComponentModel;
using System.Reflection;
using System.Text;
using Faden.Tools;
using Microsoft.Extensions.AI;

namespace Faden.Agents;

/// <summary>
/// Marks a method as a host tool and says what it does: <c>[AgentTool(ToolClass.Read)]</c>. The description
/// comes from <see cref="DescriptionAttribute"/>; the name is the method's in snake_case (<c>ReadPageAsync</c> →
/// <c>read_page</c>) unless <see cref="Name"/> says otherwise. A tool without a class is
/// <see cref="ToolClass.Unknown"/>: it always asks.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AgentToolAttribute : Attribute
{
    public AgentToolAttribute(ToolClass toolClass = ToolClass.Unknown)
    {
        Class = toolClass;
    }

    public ToolClass Class { get; }

    public string? Name { get; set; }

    /// <summary>Reaches outside the organisation (recorded and shown; no mode acts on it yet).</summary>
    public bool OpenWorld { get; set; }
}

/// <summary>A tool the agent can run: the function, what it does, and where it runs.</summary>
public sealed class AgentTool
{
    public AgentTool(AIFunction function, ToolClass toolClass = ToolClass.Unknown, string source = "host",
        string? server = null, bool? openWorld = null)
    {
        Function = function ?? throw new ArgumentNullException(nameof(function));
        Class = toolClass;
        Source = source;
        Server = server;
        OpenWorld = openWorld;
    }

    public AIFunction Function { get; }

    public string Name => Function.Name;

    /// <summary>What the integrator says the tool does; the agent's contract may make it stricter.</summary>
    public ToolClass Class { get; }

    /// <summary><c>host</c> (the host application) or <c>mcp</c> (a local MCP server).</summary>
    public string Source { get; }

    /// <summary>The MCP server's label, for an MCP tool.</summary>
    public string? Server { get; }

    public bool? OpenWorld { get; }

    /// <summary>The host tools of <paramref name="target"/>: its public methods marked <see cref="AgentToolAttribute"/>.</summary>
    public static IReadOnlyList<AgentTool> From(object target)
    {
        var type = target is Type t ? t : target.GetType();
        var instance = target is Type ? null : target;
        var flags = BindingFlags.Public | BindingFlags.Static | (instance is null ? 0 : BindingFlags.Instance);
        var tools = new List<AgentTool>();
        foreach (var method in type.GetMethods(flags))
        {
            var marker = method.GetCustomAttribute<AgentToolAttribute>();
            if (marker is null)
            {
                continue;
            }
            var function = AIFunctionFactory.Create(method, method.IsStatic ? null : instance, new AIFunctionFactoryOptions
            {
                Name = marker.Name ?? SnakeCase(method.Name),
                Description = method.GetCustomAttribute<DescriptionAttribute>()?.Description,
            });
            tools.Add(new AgentTool(function, marker.Class, openWorld: marker.OpenWorld ? true : null));
        }
        if (tools.Count == 0)
        {
            throw new ArgumentException($"{type.Name} has no public methods marked [AgentTool].", nameof(target));
        }
        return tools;
    }

    internal static string SnakeCase(string name)
    {
        if (name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5)
        {
            name = name.Substring(0, name.Length - 5);
        }
        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    /// <summary>
    /// The class the agent runs the tool under: the stricter of the integrator's and the agent contract's
    /// (<c>risk</c>). The client may be stricter than the server, never looser.
    /// </summary>
    internal static ToolClass Effective(ToolClass declared, ToolClass? contract)
    {
        if (contract is null || contract == ToolClass.Unknown)
        {
            return declared;
        }
        if (declared == ToolClass.Unknown)
        {
            return contract.Value;
        }
        return Rank(declared) >= Rank(contract.Value) ? declared : contract.Value;
    }

    private static int Rank(ToolClass c) => c switch
    {
        ToolClass.Read => 0,
        ToolClass.Write => 1,
        _ => 2,
    };
}
