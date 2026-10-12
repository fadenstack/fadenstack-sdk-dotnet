using Faden.Agents;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>The agent on the standard .NET host: the same modules, registered as services.</summary>
public static class FadenAgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="FadenAgent"/> as a singleton, built on first use. <paramref name="configure"/> gets the
    /// service provider, so tools, approval handlers and stores can come from the container.
    /// </summary>
    public static IServiceCollection AddFadenAgent(this IServiceCollection services, Action<AgentBuilder, IServiceProvider> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }
        services.TryAddSingleton(provider =>
        {
            var builder = FadenAgent.CreateBuilder();
            configure(builder, provider);
            return builder.Build();
        });
        return services;
    }

    public static IServiceCollection AddFadenAgent(this IServiceCollection services, Action<AgentBuilder> configure) =>
        services.AddFadenAgent((builder, _) => configure(builder));
}
