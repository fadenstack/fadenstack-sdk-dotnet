using Fadenstack.Agents;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>The agent on the standard .NET host: the same modules, registered as services.</summary>
public static class FadenstackAgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="FadenstackAgent"/> as a singleton, built on first use. <paramref name="configure"/> gets the
    /// service provider, so tools, approval handlers and stores can come from the container.
    /// </summary>
    public static IServiceCollection AddFadenstackAgent(this IServiceCollection services, Action<AgentBuilder, IServiceProvider> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }
        services.TryAddSingleton(provider =>
        {
            var builder = FadenstackAgent.CreateBuilder();
            configure(builder, provider);
            return builder.Build();
        });
        return services;
    }

    public static IServiceCollection AddFadenstackAgent(this IServiceCollection services, Action<AgentBuilder> configure) =>
        services.AddFadenstackAgent((builder, _) => configure(builder));
}
