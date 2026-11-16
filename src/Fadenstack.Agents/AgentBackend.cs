using Fadenstack.Models;
using Fadenstack.Resources;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

/// <summary>
/// How an agent is connected (<see cref="FadenstackAgent.Connection"/>): to an organisation's Fadenstack server, or to the
/// person's own model endpoint. Apps and the UI read it to show or hide the server's parts; nothing decides security by it.
/// </summary>
public enum AgentConnection
{
    /// <summary>A Fadenstack server: its profile, policy, grants and audit govern the agent.</summary>
    Governed,

    /// <summary>The person's own model endpoint, with no server.</summary>
    Standalone,
}

/// <summary>Where the agent's profile comes from.</summary>
internal interface IProfileSource
{
    Task<AgentProfile> GetProfileAsync(CancellationToken cancellationToken);
}

/// <summary>Where the agent tells what happened on the device (events) and what the app brings (the manifest).</summary>
internal interface IEventSink
{
    /// <summary>Whether events go anywhere at all.</summary>
    bool AcceptsEvents { get; }

    Task ReportAsync(IReadOnlyList<AgentClientEvent> events, CancellationToken cancellationToken);

    Task<ManifestReceipt> ReportManifestAsync(AppManifest manifest, CancellationToken cancellationToken);
}

/// <summary>Where the user's own settings are kept (the language they picked).</summary>
internal interface IUserSettings
{
    Task<string?> SetUserLanguageAsync(string? language, CancellationToken cancellationToken);
}

/// <summary>The skills the organisation's server offers.</summary>
internal interface IServerSkills
{
    Task<SkillList> ListSkillsAsync(string? query, int? limit, CancellationToken cancellationToken);
}

/// <summary>The tools of the organisation's MCP servers the server lists for the user and the agent.</summary>
internal interface IServerTools
{
    /// <summary>The list; null when the server does not list them (an older one).</summary>
    Task<IReadOnlyList<AgentToolInfo>?> ListToolsAsync(CancellationToken cancellationToken);
}

/// <summary>Answers a tool approval the gateway holds.</summary>
internal interface IGatewayApprovals
{
    Task AnswerApprovalAsync(string approvalId, bool approved, string? scope, string? argumentsSha256, CancellationToken cancellationToken);
}

/// <summary>The central policy for a host tool call: prepare, then check again at dispatch.</summary>
internal interface IHostToolChecks
{
    Task<HostToolDecision> CheckHostToolAsync(HostToolCheck check, CancellationToken cancellationToken);
}

/// <summary>The grants (a stretch of work, a time, Bypass) the server keeps.</summary>
internal interface IGrantService
{
    Task<AgentGrant> CreateGrantAsync(GrantRequest request, string? conversationId, string? stepUpProof, CancellationToken cancellationToken);

    Task<GrantReport> ReportGrantAsync(string grantId, IReadOnlyList<string> calls, CancellationToken cancellationToken);

    Task<AgentGrant> GetGrantAsync(string grantId, CancellationToken cancellationToken);

    Task<AgentGrant> ExtendGrantAsync(string grantId, GrantExtension extension, CancellationToken cancellationToken);

    Task<AgentGrant> RevokeGrantAsync(string grantId, CancellationToken cancellationToken);

    Task<AgentGrant> PauseGrantAsync(string grantId, CancellationToken cancellationToken);

    Task<AgentGrant> ResumeGrantAsync(string grantId, CancellationToken cancellationToken);

    Task<GrantList> ListGrantsAsync(CancellationToken cancellationToken);
}

/// <summary>The second sign-in Bypass needs (sdk/41).</summary>
internal interface IStepUp
{
    Task<StepUpStarted> StartStepUpAsync(int? longestMinutes, string? clientVersion, CancellationToken cancellationToken);

    Task<(StepUpProof? Proof, string? Error, int? Interval)> PollStepUpAsync(string stepUpCode, CancellationToken cancellationToken);
}

/// <summary>
/// The provenance routes of the agent's server: its marking policy, signed records and placement reports. Not part of
/// <see cref="IAgentBackend"/>: Fadenstack.Provenance lies above this assembly, so its public <c>ServerProvenanceBackend</c>
/// reads these through <see cref="FadenstackAgent.ProvenanceRoutes"/>; a backend without a server has none.
/// </summary>
internal interface IProvenanceRoutes
{
    Task<ProvenancePolicy> GetPolicyAsync(CancellationToken cancellationToken);

    Task<SignedRecord> SignRecordAsync(RecordRequest request, CancellationToken cancellationToken);

    Task ReportPlacementAsync(PlacementReport report, CancellationToken cancellationToken);
}

/// <summary>
/// What the agent talks to, behind one seam (sdk/51 F02, sdk/52 §8): a Fadenstack server (<see cref="ServerAgentBackend"/>)
/// or, later, the person's own model endpoint. The agent, its sessions and the UI see the same contracts in both modes.
/// A backend passes the server's exceptions through unchanged: the grant lifecycle, Bypass and the step-up catch them by
/// type. It owns what it connects with and disposes it with the agent.
/// </summary>
internal interface IAgentBackend : IProfileSource, IEventSink, IUserSettings, IServerSkills, IServerTools, IGatewayApprovals,
    IHostToolChecks, IGrantService, IStepUp, IDisposable
{
    AgentConnection Connection { get; }

    /// <summary>The Fadenstack client, when there is a server; null otherwise.</summary>
    FadenstackClient? ServerClient { get; }

    /// <summary>The model, as an <see cref="IChatClient"/>: the base the chat stages wrap.</summary>
    IChatClient ChatClient { get; }
}

/// <summary>
/// What the governed backend asks of its agent for the off-switch (core 0.7.1): whether the organisation turned the agent off, and
/// the place to say a route just answered <c>agent_disabled</c>.
/// </summary>
internal interface IAgentSwitch
{
    bool TurnedOff { get; }

    void MarkTurnedOff();
}

/// <summary>The governed backend: every call goes to the agent's routes on the Fadenstack server, as it always did.</summary>
internal sealed class ServerAgentBackend : IAgentBackend, IProvenanceRoutes
{
    private readonly FadenstackClient _client;
    private readonly bool _ownsClient;
    private volatile IAgentSwitch? _switch;

    /// <param name="client">The agent's client.</param>
    /// <param name="ownsClient">The builder made it, so the agent disposes it; a host's own client stays the host's.</param>
    public ServerAgentBackend(FadenstackClient client, bool ownsClient)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

    /// <summary>The agent this backend serves, for the off-switch: while it is off no agent route is asked (but the profile, events and manifest).</summary>
    internal void Attach(IAgentSwitch agent) => _switch = agent;

    /// <summary>
    /// A route of the agent: refused here, without a request, while the organisation has the agent off; and an <c>agent_disabled</c>
    /// answer turns the agent off for everything else (no retry anywhere). <paramref name="whileOff"/>: the profile, the events and the
    /// manifest, which the server still answers.
    /// </summary>
    private async Task<T> Route<T>(Func<Task<T>> call, bool whileOff = false)
    {
        if (!whileOff)
        {
            RefuseWhileOff();
        }
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (FadenstackApiException e) when (e.IsAgentDisabled && !whileOff)
        {
            _switch?.MarkTurnedOff();
            throw;
        }
    }

    private async Task Route(Func<Task> call, bool whileOff = false) =>
        await Route<bool>(async () =>
        {
            await call().ConfigureAwait(false);
            return true;
        }, whileOff).ConfigureAwait(false);

    private void RefuseWhileOff()
    {
        if (_switch is { TurnedOff: true })
        {
            throw new FadenstackPermissionException(System.Net.HttpStatusCode.Forbidden, FadenstackAgent.TurnedOffText, ErrorCodes.AgentDisabled,
                "permission_error");
        }
    }

    private AgentResource Agent => _client.Agent!;

    public AgentConnection Connection => AgentConnection.Governed;

    public FadenstackClient ServerClient => _client;

    public IChatClient ChatClient => _client.Chat;

    public bool AcceptsEvents => _client.Agent is not null;

    public Task<AgentProfile> GetProfileAsync(CancellationToken cancellationToken) => Route(() => Agent.GetProfileAsync(cancellationToken), whileOff: true);

    public Task ReportAsync(IReadOnlyList<AgentClientEvent> events, CancellationToken cancellationToken) =>
        Route(() => Agent.ReportAsync(events, cancellationToken), whileOff: true);

    public Task<ManifestReceipt> ReportManifestAsync(AppManifest manifest, CancellationToken cancellationToken) =>
        Route(() => Agent.ReportManifestAsync(manifest, cancellationToken), whileOff: true);

    public Task<string?> SetUserLanguageAsync(string? language, CancellationToken cancellationToken) =>
        Route(() => Agent.SetUserLanguageAsync(language, cancellationToken));

    public Task<SkillList> ListSkillsAsync(string? query, int? limit, CancellationToken cancellationToken) =>
        Route(() => Agent.ListSkillsAsync(query, limit, cancellationToken));

    public Task<IReadOnlyList<AgentToolInfo>?> ListToolsAsync(CancellationToken cancellationToken) =>
        Route(() => Agent.ListToolsAsync(cancellationToken));

    public Task AnswerApprovalAsync(string approvalId, bool approved, string? scope, string? argumentsSha256, CancellationToken cancellationToken) =>
        Route(() => Agent.AnswerApprovalAsync(approvalId, approved, scope, argumentsSha256, cancellationToken));

    public Task<HostToolDecision> CheckHostToolAsync(HostToolCheck check, CancellationToken cancellationToken) =>
        Route(() => Agent.CheckHostToolAsync(check, cancellationToken));

    public Task<AgentGrant> CreateGrantAsync(GrantRequest request, string? conversationId, string? stepUpProof,
        CancellationToken cancellationToken) =>
        Route(() => Agent.CreateGrantAsync(request, conversationId, stepUpProof, cancellationToken));

    public Task<GrantReport> ReportGrantAsync(string grantId, IReadOnlyList<string> calls, CancellationToken cancellationToken) =>
        Route(() => Agent.ReportGrantAsync(grantId, calls, cancellationToken));

    public Task<AgentGrant> GetGrantAsync(string grantId, CancellationToken cancellationToken) =>
        Route(() => Agent.GetGrantAsync(grantId, cancellationToken));

    public Task<AgentGrant> ExtendGrantAsync(string grantId, GrantExtension extension, CancellationToken cancellationToken) =>
        Route(() => Agent.ExtendGrantAsync(grantId, extension, cancellationToken));

    public Task<AgentGrant> RevokeGrantAsync(string grantId, CancellationToken cancellationToken) =>
        Route(() => Agent.RevokeGrantAsync(grantId, cancellationToken));

    public Task<AgentGrant> PauseGrantAsync(string grantId, CancellationToken cancellationToken) =>
        Route(() => Agent.PauseGrantAsync(grantId, cancellationToken));

    public Task<AgentGrant> ResumeGrantAsync(string grantId, CancellationToken cancellationToken) =>
        Route(() => Agent.ResumeGrantAsync(grantId, cancellationToken));

    public Task<GrantList> ListGrantsAsync(CancellationToken cancellationToken) => Route(() => Agent.ListGrantsAsync(cancellationToken));

    public Task<StepUpStarted> StartStepUpAsync(int? longestMinutes, string? clientVersion, CancellationToken cancellationToken) =>
        Agent.StartStepUpAsync(longestMinutes, clientVersion, cancellationToken);

    public Task<(StepUpProof? Proof, string? Error, int? Interval)> PollStepUpAsync(string stepUpCode, CancellationToken cancellationToken) =>
        Agent.PollStepUpAsync(stepUpCode, cancellationToken);

    private AgentProvenanceResource Provenance =>
        _client.Agent?.Provenance ?? throw new InvalidOperationException("The agent's client has no agent.");

    public Task<ProvenancePolicy> GetPolicyAsync(CancellationToken cancellationToken) => Route(() => Provenance.GetPolicyAsync(cancellationToken));

    public Task<SignedRecord> SignRecordAsync(RecordRequest request, CancellationToken cancellationToken) =>
        Route(() => Provenance.SignRecordAsync(request, cancellationToken));

    public Task ReportPlacementAsync(PlacementReport report, CancellationToken cancellationToken) =>
        Route(() => Provenance.ReportPlacementAsync(report, cancellationToken));

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
