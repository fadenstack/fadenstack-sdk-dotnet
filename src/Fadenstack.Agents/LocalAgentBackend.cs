using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fadenstack.Endpoints;
using Fadenstack.Models;
using Fadenstack.Storage;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

/// <summary>What the person set for a standalone agent (sdk/51 F02 §3); the machine policy narrows it (F09).</summary>
public sealed record LocalProfileOptions
{
    /// <summary>Whether the person allows local MCP servers for this agent (off by default).</summary>
    public bool LocalMcp { get; init; }

    /// <summary>Whether local skills are on (on by default: the person's own device, their own skills).</summary>
    public bool LocalSkills { get; init; } = true;

    /// <summary>Whether the model sees pictures, when the person says so; else the endpoint check's word, else unknown.</summary>
    public bool? Vision { get; init; }
}

/// <summary>
/// The standalone backend (sdk/51 F02 §3, I02 commit 14): no server at all. The profile comes from the app's agent definition
/// (F03) and the endpoint check; there are no events, no grants, no Bypass, no step-up and no server checks; the user's language
/// stays on the device. The data goes under the standalone principal (<c>users/standalone/</c>), apart from any governed
/// person's.
/// </summary>
internal sealed class LocalAgentBackend : IAgentBackend
{
    /// <summary>The standalone principal's tenant and user: its data lives under <c>users/standalone/</c>.</summary>
    internal const string StandaloneId = "standalone";

    private readonly AgentDefinition _definition;
    private readonly IChatClient _chat;
    private readonly bool _ownsChat;
    private readonly ModelEndpoint? _endpoint;
    private readonly EndpointReport? _report;
    private readonly LocalProfileOptions _options;
    private readonly object _languageLock = new();

    public LocalAgentBackend(AgentDefinition definition, IChatClient chat, bool ownsChat, ModelEndpoint? endpoint,
        EndpointReport? report, LocalProfileOptions? options)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _ownsChat = ownsChat;
        _endpoint = endpoint;
        _report = report;
        _options = options ?? new LocalProfileOptions();
    }

    public AgentConnection Connection => AgentConnection.Standalone;

    public FadenstackClient? ServerClient => null;

    public IChatClient ChatClient => _chat;

    /// <summary>The endpoint's base, when the agent talks to one (not for a host's own chat client).</summary>
    public Uri? EndpointUrl => _endpoint?.BaseUrl;

    /// <summary>
    /// Whether the connection marks text (F03 item 8): the check's word when the app gave its report, else the sourced table by
    /// the endpoint's host and dialect; unknown for a host's own chat client.
    /// </summary>
    /// <summary>The kind of server: the check's word, else by the endpoint's host and dialect.</summary>
    public EndpointServer Server => _report?.Server is { } known && known != EndpointServer.Unknown ? known
        : _endpoint is null ? EndpointServer.Unknown : EndpointCheck.ServerOf(_endpoint);

    public EndpointWatermarkStatus Watermark => _report?.Watermark
        ?? (_endpoint is null ? new EndpointWatermarkStatus { Status = TextWatermark.Unknown } : EndpointCheck.WatermarkOf(_endpoint));

    public bool AcceptsEvents => false;

    public async Task<AgentProfile> GetProfileAsync(CancellationToken cancellationToken)
    {
        var ui = _definition.Ui?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? new Dictionary<string, JsonElement>();
        // Whether the model runs in this network: the desktop's screen pictures read it. A host's own chat client: unknown,
        // so external (fail closed).
        var residency = _endpoint is null ? AddressScope.Public
            : await NetworkAddress.OfHostAsync(_endpoint.BaseUrl.Host, cancellationToken).ConfigureAwait(false);
        ui["model_residency"] = JsonSerializer.SerializeToElement(residency == AddressScope.Public ? "external" : "private");
        return new AgentProfile
        {
            Id = _definition.Slug,
            Slug = _definition.Slug,
            Name = _definition.Name,
            Version = null,
            Model = _endpoint?.Model is { Length: > 0 } model ? model : _report?.Model,
            Vision = _options.Vision ?? _report?.Vision,
            Images = false, // pictures in standalone wait for D17 (owner, 14 Nov)
            Skills = false, // no server list; local skills come with F04
            InstructionsSha256 = Sha256(_definition.Instructions),
            HostToolContract = _definition.HostToolContract.ToList(),
            ServerTools = null,
            // Standalone: the person decides per server; this computer's policy can still forbid it (FadenstackAgent narrows).
            LocalMcp = new LocalMcpSettings { Mode = _options.LocalMcp ? "allow" : "disable", PlainHttp = "allow" },
            LocalSkills = new LocalSkillsSettings { Mode = _options.LocalSkills ? "allow" : "disable" },
            // Ask to start, Auto at most: no local Bypass (D4 later).
            ToolModes = new ToolModeSettings { Default = "ask", Ceiling = "auto", Max = "auto" },
            Approvals = null, // no grants, so no Bypass and no grant card
            Ui = ui,
            Edition = StandaloneId,
            Features = new List<string>(), // no grants, no Bypass, no dispatch check
            User = new UserSettings { Language = ReadLanguage() },
            Principal = new AgentPrincipal { TenantId = StandaloneId, UserId = StandaloneId, AgentId = _definition.Slug },
        };
    }

    private static string Sha256(string text)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
    }

    // ---- the user's language: on the device, in the standalone folder ----

    private static string LanguageFile => Path.Combine(FadenstackPaths.User(StandaloneId), "language");

    private string? ReadLanguage()
    {
        lock (_languageLock)
        {
            try
            {
                return File.Exists(LanguageFile) && File.ReadAllText(LanguageFile).Trim() is { Length: > 0 and <= 35 } tag ? tag : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public Task<string?> SetUserLanguageAsync(string? language, CancellationToken cancellationToken)
    {
        lock (_languageLock)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                File.Delete(LanguageFile);
                return Task.FromResult<string?>(null);
            }
            var tag = language!.Trim();
            if (tag.Length > 35 || tag.Any(c => !(char.IsLetterOrDigit(c) || c == '-')))
            {
                throw new ArgumentException("A language is a BCP 47 tag, such as de or zh-Hans.", nameof(language));
            }
            File.WriteAllText(LanguageFile, tag);
            return Task.FromResult<string?>(tag);
        }
    }

    // ---- what a server does and standalone has not ----

    public Task ReportAsync(IReadOnlyList<AgentClientEvent> events, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ManifestReceipt> ReportManifestAsync(AppManifest manifest, CancellationToken cancellationToken) =>
        Task.FromResult(new ManifestReceipt());

    public Task<SkillList> ListSkillsAsync(string? query, int? limit, CancellationToken cancellationToken) =>
        Task.FromResult(new SkillList());

    public Task<IReadOnlyList<AgentToolInfo>?> ListToolsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentToolInfo>?>(Array.Empty<AgentToolInfo>());

    public Task AnswerApprovalAsync(string approvalId, bool approved, string? scope, CancellationToken cancellationToken) =>
        throw NoServer("a gateway approval");

    /// <summary>Never a blanket allow: without a server there is no central word, and the local class and floor decide.</summary>
    public Task<HostToolDecision> CheckHostToolAsync(HostToolCheck check, CancellationToken cancellationToken) =>
        Task.FromResult(new HostToolDecision
        {
            Decision = HostToolVerdict.Deny,
            Message = "There is no server to check host tools with.",
        });

    public Task<AgentGrant> CreateGrantAsync(GrantRequest request, string? conversationId, string? stepUpProof,
        CancellationToken cancellationToken) => throw NoServer("grants");

    public Task<GrantReport> ReportGrantAsync(string grantId, IReadOnlyList<string> calls, CancellationToken cancellationToken) =>
        throw NoServer("grants");

    public Task<AgentGrant> GetGrantAsync(string grantId, CancellationToken cancellationToken) => throw NoServer("grants");

    public Task<AgentGrant> ExtendGrantAsync(string grantId, GrantExtension extension, CancellationToken cancellationToken) =>
        throw NoServer("grants");

    public Task<AgentGrant> RevokeGrantAsync(string grantId, CancellationToken cancellationToken) => throw NoServer("grants");

    public Task<AgentGrant> PauseGrantAsync(string grantId, CancellationToken cancellationToken) => throw NoServer("grants");

    public Task<AgentGrant> ResumeGrantAsync(string grantId, CancellationToken cancellationToken) => throw NoServer("grants");

    public Task<GrantList> ListGrantsAsync(CancellationToken cancellationToken) => throw NoServer("grants");

    public Task<StepUpStarted> StartStepUpAsync(int? longestMinutes, string? clientVersion, CancellationToken cancellationToken) =>
        throw NoServer("a second sign-in");

    public Task<(StepUpProof? Proof, string? Error, int? Interval)> PollStepUpAsync(string stepUpCode, CancellationToken cancellationToken) =>
        throw NoServer("a second sign-in");

    private static InvalidOperationException NoServer(string what) =>
        new($"This agent runs on the person's own endpoint, without a Fadenstack server: it has no {what}.");

    public void Dispose()
    {
        if (_ownsChat)
        {
            _chat.Dispose();
        }
    }
}
