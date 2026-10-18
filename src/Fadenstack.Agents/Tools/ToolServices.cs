namespace Fadenstack.Agents;

/// <summary>
/// The tool services of one call (<see cref="AgentBuilder.AddToolService{T}"/>): each made on first use, then the same
/// instance for the tool and its stages; disposed when the call ends.
/// </summary>
internal sealed class ToolServices : IServiceProvider, IDisposable
{
    private readonly IReadOnlyDictionary<Type, Func<ToolInvocation, object>> _factories;
    private readonly Dictionary<Type, object> _made = new();
    private ToolInvocation? _invocation;

    public ToolServices(IReadOnlyDictionary<Type, Func<ToolInvocation, object>> factories)
    {
        _factories = factories;
    }

    /// <summary>The call the services are for; set once the invocation exists, since it carries these services.</summary>
    public void Bind(ToolInvocation invocation) => _invocation = invocation;

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceProvider))
        {
            return this;
        }
        lock (_made)
        {
            if (_made.TryGetValue(serviceType, out var made))
            {
                return made;
            }
            if (_invocation is null || !_factories.TryGetValue(serviceType, out var factory))
            {
                return null;
            }
            made = factory(_invocation);
            _made[serviceType] = made;
            return made;
        }
    }

    public void Dispose()
    {
        List<object> made;
        lock (_made)
        {
            made = _made.Values.ToList();
            _made.Clear();
        }
        foreach (var service in made.OfType<IDisposable>())
        {
            service.Dispose();
        }
    }
}
