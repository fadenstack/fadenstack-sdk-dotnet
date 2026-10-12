using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Agents;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;

namespace Fadenstack.Workspaces;

/// <summary>
/// A tool's typed access to the workspace it works on, e.g. <c>Workspace&lt;Word.Document&gt;</c>. The framework
/// resolves which workspace, takes the lease and passes this in; the tool only does its work.
/// </summary>
public sealed class Workspace<T> where T : class
{
    private readonly IWorkspaceBinding _binding;

    public Workspace(IWorkspaceBinding binding)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    public WorkspaceInfo Info => _binding.Workspace;

    /// <summary>Runs <paramref name="work"/> on the application's thread with its object.</summary>
    public Task<TResult> RunAsync<TResult>(Func<T, TResult> work, CancellationToken cancellationToken = default) =>
        _binding.RunAsync(target => work((T)target), cancellationToken);
}

public sealed class WorkspaceToolOptions
{
    /// <summary>Who leases are taken for: the agent's slug.</summary>
    public string Holder { get; set; } = "agent";

    /// <summary>How long a write lease lasts if never released (a crashed tool).</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a write tool waits for another agent's lease before it reports the document busy.</summary>
    public TimeSpan LeaseWait { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Tools that work on a workspace. A method marked <see cref="AgentToolAttribute"/> with a
/// <see cref="Workspace{T}"/> parameter gets an optional <c>workspace</c> argument instead of that parameter:
/// empty for the active document, a name, a path, or <c>name@word#2</c>. Each call then:
/// resolves the workspace (asking back, never guessing, when several fit); takes the document lease for
/// anything but a read; binds through the application's provider; runs the method; releases the lease.
/// Methods without the parameter become ordinary tools.
/// </summary>
public static class WorkspaceTools
{
    public const string ArgumentName = "workspace";

    public static IReadOnlyList<AgentTool> From(object target, string app, WorkspaceDirectory directory, WorkspaceToolOptions? options = null)
    {
        if (directory.Provider(app) is not { } provider)
        {
            throw new ArgumentException($"No provider for '{app}' in the directory.", nameof(app));
        }
        options ??= new WorkspaceToolOptions();
        var type = target is Type t ? t : target.GetType();
        var instance = target is Type ? null : target;
        var flags = BindingFlags.Public | BindingFlags.Static | (instance is null ? 0 : BindingFlags.Instance);
        var tools = new List<AgentTool>();
        foreach (var method in type.GetMethods(flags))
        {
            if (method.GetCustomAttribute<AgentToolAttribute>() is not { } marker)
            {
                continue;
            }
            var bound = method.GetParameters().Any(p => IsWorkspace(p.ParameterType));
            var function = AIFunctionFactory.Create(method, method.IsStatic ? null : instance, new AIFunctionFactoryOptions
            {
                Name = marker.Name ?? AgentTool.SnakeCase(method.Name),
                Description = method.GetCustomAttribute<DescriptionAttribute>()?.Description,
                ConfigureParameterBinding = p => IsWorkspace(p.ParameterType)
                    ? new AIFunctionFactoryOptions.ParameterBindingOptions { ExcludeFromSchema = true, BindParameter = BindWorkspace }
                    : default,
            });
            if (bound)
            {
                function = new WorkspaceFunction(function, provider.Info, marker.Class, directory, options);
            }
            tools.Add(new AgentTool(function, marker.Class, openWorld: marker.OpenWorld ? true : null));
        }
        if (tools.Count == 0)
        {
            throw new ArgumentException($"{type.Name} has no public methods marked [AgentTool].", nameof(target));
        }
        return tools;
    }

    internal static bool IsWorkspace(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Workspace<>);

    private static object? BindWorkspace(ParameterInfo parameter, AIFunctionArguments arguments)
    {
        if (arguments.Context is null || !arguments.Context.TryGetValue(WorkspaceFunction.BindingKey, out var value)
            || value is not IWorkspaceBinding binding)
        {
            throw new InvalidOperationException($"'{parameter.Name}' needs a workspace; create the tool with WorkspaceTools.From.");
        }
        return Activator.CreateInstance(parameter.ParameterType, binding);
    }
}

internal sealed class WorkspaceFunction : DelegatingAIFunction
{
    internal static readonly object BindingKey = new();

    private readonly ApplicationInfo _app;
    private readonly ToolClass _class;
    private readonly WorkspaceDirectory _directory;
    private readonly WorkspaceToolOptions _options;
    private readonly JsonElement _schema;

    public WorkspaceFunction(AIFunction inner, ApplicationInfo app, ToolClass toolClass, WorkspaceDirectory directory, WorkspaceToolOptions options)
        : base(inner)
    {
        _app = app;
        _class = toolClass;
        _directory = directory;
        _options = options;
        _schema = WithWorkspaceArgument(inner.JsonSchema, app);
    }

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        arguments.TryGetValue(WorkspaceTools.ArgumentName, out var raw);
        arguments.Remove(WorkspaceTools.ArgumentName);
        var reference = raw switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            _ => raw.ToString(),
        };

        var resolution = await _directory.ResolveAsync(_app.Id, reference, cancellationToken).ConfigureAwait(false);
        if (resolution.Workspace is not { } workspace)
        {
            return resolution.Message;
        }

        DocumentLease? lease = null;
        if (_class != ToolClass.Read)
        {
            lease = await AcquireAsync(workspace, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                var holder = _directory.Leases.Peek(workspace.Ref)?.Holder ?? "another agent";
                return $"{workspace.Title} is being changed by {holder} right now. Try again in a moment, or ask the user.";
            }
        }
        try
        {
            using var binding = await _directory.Provider(_app.Id)!.BindAsync(workspace, cancellationToken).ConfigureAwait(false);
            arguments.Context ??= new Dictionary<object, object?>();
            arguments.Context[BindingKey] = binding;
            return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async Task<DocumentLease?> AcquireAsync(WorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.LeaseWait;
        while (true)
        {
            if (_directory.Leases.TryAcquire(workspace.Ref, _options.Holder, _options.LeaseDuration) is { } lease)
            {
                return lease;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    // The workspace argument goes first, so the model reads it before the tool's own arguments.
    private static JsonElement WithWorkspaceArgument(JsonElement schema, ApplicationInfo app)
    {
        var source = JsonNode.Parse(schema.GetRawText()) as JsonObject ?? new JsonObject { ["type"] = "object" };
        var properties = new JsonObject
        {
            [WorkspaceTools.ArgumentName] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = $"Which {app.Name} document: empty for the active one; its name or path; "
                    + $"name@{app.Id}#2 when it is open in more than one instance (see list_open_workspaces).",
            },
        };
        if (source["properties"] is JsonObject existing)
        {
            foreach (var pair in existing.ToList())
            {
                existing.Remove(pair.Key);
                properties[pair.Key] = pair.Value;
            }
        }
        source["properties"] = properties;
        using var document = JsonDocument.Parse(source.ToJsonString());
        return document.RootElement.Clone();
    }
}
