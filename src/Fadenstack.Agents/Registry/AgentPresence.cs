namespace Fadenstack.Agents.Registry;

public enum AgentStatus
{
    Idle,
    Busy,
}

/// <summary>
/// A Fadenstack agent active on this device, as it registered itself (<see cref="DeviceAgentRegistry"/>). The
/// agent's loop may run in the instance itself (an add-in) or in a service next to it (a thin host), so the
/// instance and the host process are separate.
/// </summary>
public sealed record AgentPresence
{
    /// <summary>The server agent's slug: <c>word-assistant</c>.</summary>
    public string Agent { get; init; } = "";

    /// <summary>The instance it works in (<c>InstanceKey</c> text); empty for the desktop agent.</summary>
    public string Instance { get; init; } = "";

    /// <summary>The host's window key, for hosts with an agent per window (Office panes).</summary>
    public string? Window { get; init; }

    /// <summary>Locations of the workspaces it is attached to.</summary>
    public IReadOnlyList<string> Workspaces { get; init; } = [];

    public AgentStatus Status { get; init; }

    /// <summary>A pipe name, when the agent accepts delegation or lends tools; null otherwise.</summary>
    public string? Bridge { get; init; }

    /// <summary>
    /// How to hand this agent a task, when it accepts delegation (<c>Fadenstack.Agents.Delegation.DelegationHost</c>);
    /// null otherwise. Holds the launch's secret: the registry folder is the OS user's own (owner-only).
    /// </summary>
    public AgentDelegationEndpoint? Delegation { get; init; }

    /// <summary>The process the agent's loop runs in, and its start time (to tell a restarted process).</summary>
    public int HostProcessId { get; init; }

    public long HostStartTicks { get; init; }

    public DateTime HeartbeatUtc { get; init; }
}

/// <summary>Where and how an agent takes delegated tasks: a local pipe, the secret of this launch, and its card.</summary>
public sealed record AgentDelegationEndpoint
{
    /// <summary>The local pipe the agent listens on (a name, not a path).</summary>
    public string Pipe { get; init; } = "";

    /// <summary>Random per launch; the first message on the pipe must carry it.</summary>
    public string Secret { get; init; } = "";

    /// <summary>What the agent says about itself, for a caller that picks among agents.</summary>
    public AgentCardSummary Card { get; init; } = new();
}

/// <summary>A short Agent Card: who the agent is and what it can do, in lines a model can choose by.</summary>
public sealed record AgentCardSummary
{
    /// <summary>The server agent's slug.</summary>
    public string Slug { get; init; } = "";

    /// <summary>The name people know it by: <c>Word</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>One line on what it is for.</summary>
    public string Description { get; init; } = "";

    public IReadOnlyList<AgentCardSkill> Skills { get; init; } = [];
}

/// <summary>One thing an agent can do: a short name and a line about it.</summary>
public sealed record AgentCardSkill(string Name, string Description);
