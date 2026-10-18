using System.ComponentModel;
using System.Reflection;
using System.Text;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

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

    /// <summary>
    /// Reaches outside the organisation (a web search, opening a page): recorded, and an approval card says that what it
    /// is given leaves the organisation (<see cref="ApprovalRequest.OpenWorld"/>); no mode acts on it yet.
    /// </summary>
    public bool OpenWorld { get; set; }
}

/// <summary>
/// Says that a tool puts content a model wrote where people read it (a document, a mail, a web page): which argument
/// carries it, its media type, and what kind of edit it makes. <c>[AgentTool(ToolClass.Write), Emits("text",
/// "text/markdown", EditKind.Generated)]</c>. Metadata only: with <c>Fadenstack.Provenance</c> the tool places the
/// content through <c>IProvenance.PlaceAsync</c>, which marks it, and a call that places nothing is recorded as
/// unmarked. Once per argument.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class EmitsAttribute : Attribute
{
    public EmitsAttribute(string argument, string mediaType, EditKind editKind = EditKind.Generated)
    {
        Argument = argument;
        MediaType = mediaType;
        EditKind = editKind;
    }

    /// <summary>The parameter that carries the content, by its name in the method.</summary>
    public string Argument { get; }

    /// <summary><c>text/plain</c>, <c>text/markdown</c>, <c>text/html</c>, <c>image/png</c>, ...</summary>
    public string MediaType { get; }

    /// <summary>The kind of edit the tool makes, unless <see cref="EditKindFrom"/> decides per call.</summary>
    public EditKind EditKind { get; }

    /// <summary>
    /// A method of the same class that decides the edit kind from a call's arguments:
    /// <c>EditKind Name(IReadOnlyDictionary&lt;string, object?&gt; arguments)</c>, static or not.
    /// </summary>
    public string? EditKindFrom { get; set; }
}

/// <summary>
/// Marks a type (or a parameter) that a tool gets from its call's tool services rather than from the model: a tool
/// method's parameter of such a type is bound from <see cref="AIFunctionArguments.Services"/> and left out of the
/// tool's JSON schema. Modules add the services with <see cref="AgentBuilder.AddToolService{T}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Parameter, Inherited = false)]
public sealed class ToolServiceAttribute : Attribute
{
}

/// <summary>Content for people a tool writes: the argument that carries it, its media type, and the kind of edit.</summary>
public sealed record ToolOutput
{
    public ToolOutput(string argument, string mediaType, EditKind editKind = EditKind.Generated)
    {
        Argument = string.IsNullOrWhiteSpace(argument) ? throw new ArgumentException("An output names its argument.", nameof(argument)) : argument;
        MediaType = string.IsNullOrWhiteSpace(mediaType) ? throw new ArgumentException("An output has a media type.", nameof(mediaType)) : mediaType;
        EditKind = editKind;
    }

    public string Argument { get; }

    public string MediaType { get; }

    /// <summary>The kind of edit, unless <see cref="EditKindOf"/> decides per call.</summary>
    public EditKind EditKind { get; }

    /// <summary>Decides the edit kind from a call's arguments (an "improve" tool that only corrects for light changes).</summary>
    public Func<IReadOnlyDictionary<string, object?>, EditKind>? EditKindOf { get; init; }

    /// <summary>The edit kind of one call.</summary>
    public EditKind EditKindFor(IReadOnlyDictionary<string, object?> arguments) => EditKindOf?.Invoke(arguments) ?? EditKind;
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

    /// <summary>Reaches outside the organisation (a web search, opening a page); null when nobody said.</summary>
    public bool? OpenWorld { get; }

    /// <summary>
    /// What a call acts on, in the user's words (<c>button "Send" on mail.example.com</c>), for the approval card
    /// (<see cref="ApprovalRequest.Target"/>): called only when the user is asked, so they see what the call will touch,
    /// not only its arguments. Null, empty, a failure or no answer within 3 seconds shows the arguments alone.
    /// </summary>
    public Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<string?>>? Describe { get; init; }

    /// <summary>
    /// The content for people the tool writes (<see cref="EmitsAttribute"/>): which argument, its media type, the kind of
    /// edit. Empty for a tool that writes none.
    /// </summary>
    public IReadOnlyList<ToolOutput> Outputs { get; init; } = Array.Empty<ToolOutput>();

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
                ConfigureParameterBinding = ToolServiceBinding,
            });
            tools.Add(new AgentTool(function, marker.Class, openWorld: marker.OpenWorld ? true : null)
            {
                Outputs = OutputsOf(method, instance),
            });
        }
        if (tools.Count == 0)
        {
            throw new ArgumentException($"{type.Name} has no public methods marked [AgentTool].", nameof(target));
        }
        return tools;
    }

    /// <summary>
    /// For <see cref="AIFunctionFactoryOptions.ConfigureParameterBinding"/>: a parameter whose type (or itself) is marked
    /// <see cref="ToolServiceAttribute"/> is bound from the call's tool services and left out of the JSON schema; any
    /// other parameter is bound as usual. A parameter with a default gets it when no module added the service.
    /// </summary>
    public static AIFunctionFactoryOptions.ParameterBindingOptions ToolServiceBinding(ParameterInfo parameter) =>
        IsToolService(parameter)
            ? new AIFunctionFactoryOptions.ParameterBindingOptions { ExcludeFromSchema = true, BindParameter = BindToolService }
            : default;

    internal static bool IsToolService(ParameterInfo parameter) =>
        parameter.IsDefined(typeof(ToolServiceAttribute), false) || parameter.ParameterType.IsDefined(typeof(ToolServiceAttribute), false);

    private static object? BindToolService(ParameterInfo parameter, AIFunctionArguments arguments)
    {
        if (arguments.Services?.GetService(parameter.ParameterType) is { } service)
        {
            return service;
        }
        if (parameter.HasDefaultValue)
        {
            return parameter.DefaultValue;
        }
        throw new InvalidOperationException(
            $"'{parameter.Name}' needs {parameter.ParameterType.Name}: add the module that provides it to the agent's builder.");
    }

    /// <summary>The method's <see cref="EmitsAttribute"/>s as outputs; each must name one of its parameters.</summary>
    internal static IReadOnlyList<ToolOutput> OutputsOf(MethodInfo method, object? instance)
    {
        var emits = method.GetCustomAttributes<EmitsAttribute>().ToList();
        if (emits.Count == 0)
        {
            return Array.Empty<ToolOutput>();
        }
        var parameters = method.GetParameters().Select(p => p.Name).ToList();
        var outputs = new List<ToolOutput>();
        foreach (var e in emits)
        {
            if (!parameters.Contains(e.Argument))
            {
                throw new ArgumentException($"{method.Name} emits '{e.Argument}', which is not one of its parameters.");
            }
            if (outputs.Any(o => o.Argument == e.Argument))
            {
                throw new ArgumentException($"{method.Name} emits '{e.Argument}' twice.");
            }
            outputs.Add(new ToolOutput(e.Argument, e.MediaType, e.EditKind) { EditKindOf = Decider(method, instance, e.EditKindFrom) });
        }
        return outputs;
    }

    private static Func<IReadOnlyDictionary<string, object?>, EditKind>? Decider(MethodInfo tool, object? instance, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var method = tool.DeclaringType!.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.ReturnType == typeof(EditKind)
            && m.GetParameters() is { Length: 1 } p && p[0].ParameterType == typeof(IReadOnlyDictionary<string, object?>));
        if (method is null || (!method.IsStatic && instance is null))
        {
            throw new ArgumentException(
                $"{tool.Name}: EditKindFrom names '{name}', which is not an 'EditKind {name}(IReadOnlyDictionary<string, object?>)' it can call.");
        }
        var target = method.IsStatic ? null : instance;
        return arguments => (EditKind)method.Invoke(target, new object[] { arguments })!;
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
