namespace Fadenstack.Agents.Delegation;

/// <summary>Starts an agent's app on demand, for a delegation whose target is not open.</summary>
public interface IAgentLauncher
{
    /// <summary>Whether this launcher can start the agent (in the document, when given) on this device.</summary>
    bool CanLaunch(string agent, string? document);

    /// <summary>
    /// Starts the agent's app (opening the document when given) and asks it to get its agent ready. Returns once asked,
    /// not once ready.
    /// </summary>
    Task<AgentLaunch> LaunchAsync(string agent, string? document, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IAgentLauncher.LaunchAsync"/> did: <see cref="Problem"/> says why not, when it did not start.</summary>
public sealed record AgentLaunch(bool Started, string? Problem);

/// <summary>What a <see cref="LocalAgentDelegate"/> does besides sending tasks.</summary>
public sealed class LocalAgentDelegateOptions
{
    /// <summary>Starts an agent's app when no instance is open; null: a task for an agent that is not open is not run.</summary>
    public IAgentLauncher? Launcher { get; set; }

    /// <summary>How long a launched agent has to appear in the registry (default 90 seconds).</summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>How often the registry is looked at while waiting (default 500 milliseconds).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
}
