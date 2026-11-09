using System.Text.Json.Serialization;

namespace Fadenstack.Models;

/// <summary>The kinds of grant (<see cref="AgentGrant.Kind"/>).</summary>
public static class GrantKinds
{
    /// <summary>One tool, for a while.</summary>
    public const string Time = "time";

    /// <summary>A stretch of work: reads and writes in named places, for a while.</summary>
    public const string Work = "work";

    /// <summary>
    /// Bypass, in one conversation: what its mode would ask about runs, destructive and unclassified calls too -- never
    /// an admin's rule or a tool that asks every time (sdk/09 §3.12).
    /// </summary>
    public const string Bypass = "bypass";
}

/// <summary>
/// What a person allowed on a grant card, as the device asks the server for it (<c>POST /v1/agents/{agent}/grants</c>).
/// The server may grant part of it (<see cref="AgentGrant.Narrowed"/>); the device relies only on what it answers.
/// </summary>
public sealed record GrantRequest
{
    /// <summary><see cref="GrantKinds.Time"/> or <see cref="GrantKinds.Work"/>.</summary>
    public string Kind { get; init; } = GrantKinds.Time;

    /// <summary>For a time: the one tool.</summary>
    public List<string> Tools { get; init; } = new();

    /// <summary>For a stretch of work: <c>read</c>, <c>write</c> or both.</summary>
    public List<string> Classes { get; init; } = new();

    /// <summary>For a stretch of work: the places.</summary>
    public List<GrantTarget> Targets { get; init; } = new();

    public int Minutes { get; init; } = 30;

    /// <summary>At most this many calls; null: no count.</summary>
    public int? MaxCalls { get; init; }

    /// <summary>A grant for a time only in the conversation it was made in. A stretch of work always is.</summary>
    public bool Conversation { get; init; }

    /// <summary>The SHA-256 (lower-case hex) of the text the card showed the person.</summary>
    [JsonPropertyName("shown_text_sha256")]
    public string? ShownTextSha256 { get; init; }

    /// <summary>The person's own words on why (Bypass), kept with the grant's record (L-59).</summary>
    public string? Reason { get; init; }

    /// <summary>The client as it names itself, e.g. <c>Fadenstack.Agents/0.3.0</c> (L-59).</summary>
    public string? ClientVersion { get; init; }
}

/// <summary>How and when the person signed in again to switch Bypass on, as the grant records it (sdk/41, L-59).</summary>
public sealed record GrantStepUp
{
    /// <summary><c>password</c>, <c>oidc</c>, <c>saml</c> or <c>ldap</c>.</summary>
    public string Method { get; init; } = "";

    /// <summary>The sign-in provider's key for the last three; null for the account's password.</summary>
    public string? Provider { get; init; }

    public DateTimeOffset? At { get; init; }
}

/// <summary>
/// A second sign-in started (<c>POST /api/agent-tokens/step-ups</c>, sdk/41), shaped as a device authorization (RFC 8628
/// §3.2): the app keeps <see cref="StepUpCode"/> to poll with and opens <see cref="VerificationUriComplete"/> in the
/// system browser, where the person signs in again and approves.
/// </summary>
public sealed record StepUpStarted
{
    /// <summary>The app's own code: only it collects the proof. Never shown, logged or stored.</summary>
    public string StepUpCode { get; init; } = "";

    /// <summary>The code in the link (<c>XXXX-XXXX</c>), shown so the person can compare it.</summary>
    public string UserCode { get; init; } = "";

    public string VerificationUri { get; init; } = "";

    /// <summary>The page with the code in its fragment, which no server sees.</summary>
    public string VerificationUriComplete { get; init; } = "";

    public int ExpiresIn { get; init; } = 600;

    /// <summary>How long to wait between polls, in seconds (RFC 8628 §3.5).</summary>
    public int Interval { get; init; } = 5;
}

/// <summary>
/// What the app collects once the person approved (sdk/41): the proof of their fresh sign-in, good for
/// <see cref="ExpiresIn"/> more seconds by the server's clock. Kept in memory only, sent with the Bypass grant request.
/// </summary>
public sealed record StepUpProof
{
    public string Proof { get; init; } = "";

    public int ExpiresIn { get; init; }

    public DateTimeOffset? SignedInAt { get; init; }

    public string Method { get; init; } = "";
}

/// <summary>What the gateway found comparing a Bypass turn's calls with its own records (<c>.../report</c>, L-61).</summary>
public sealed record GrantReport
{
    public int Matched { get; init; }

    /// <summary>Calls the device ran that the gateway never counted under the grant.</summary>
    public int DeviceOnly { get; init; }

    /// <summary>Calls of the app's tools the gateway counted that the device did not report.</summary>
    public int GatewayOnly { get; init; }
}

/// <summary>What a person added on the card that extends a running grant: only the difference.</summary>
public sealed record GrantExtension
{
    /// <summary>Moves the end this much later, at most to the grant's longest span from when it was made.</summary>
    public int Minutes { get; init; }

    /// <summary>Places a stretch of work gains, for what it already covers.</summary>
    public List<GrantTarget> Targets { get; init; } = new();

    [JsonPropertyName("shown_text_sha256")]
    public string? ShownTextSha256 { get; init; }
}

/// <summary>A place a grant for a stretch of work covers (RFC 9396 <c>authorization_details</c>).</summary>
public sealed record GrantTarget
{
    /// <summary><c>document</c>, <c>folder</c>, <c>site</c>, <c>app</c> or <c>desk</c>.</summary>
    public string Type { get; init; } = "";

    /// <summary>Documents, apps and desks by name or path; folders by path (and what lies under them); sites by origin.</summary>
    public List<string> Identifiers { get; init; } = new();

    /// <summary>What the agent may do there; null: every class the grant covers.</summary>
    public List<string>? Actions { get; init; }
}

/// <summary>What a grant covers (<see cref="AgentGrant.Scope"/>).</summary>
public sealed record GrantScope
{
    public List<string>? Tools { get; init; }
    public List<string>? Classes { get; init; }
    public List<GrantTarget>? Targets { get; init; }
}

/// <summary>A grant as the server holds it. Its times are the server's: see <see cref="ServerNow"/>.</summary>
public sealed record AgentGrant
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";

    /// <summary><c>active</c>, <c>paused</c>, <c>revoked</c>, <c>expired</c> or <c>spent</c>.</summary>
    public string Status { get; init; } = "";

    public string? AgentId { get; init; }
    public int? AgentVersion { get; init; }
    public string? DeviceId { get; init; }
    public string? ConversationId { get; init; }
    public GrantScope Scope { get; init; } = new();
    public DateTimeOffset? IssuedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The seconds left when the server answered.</summary>
    public int RemainingS { get; init; }

    public int? MaxCalls { get; init; }
    public int CallsUsed { get; init; }
    public DateTimeOffset? PausedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>
    /// <c>person</c>, <c>admin</c>, <c>agent</c>, <c>kill_switch</c> (Bypass turned off for the organisation) or
    /// <c>limit</c> (a narrower limit or list of the organisation's).
    /// </summary>
    public string? RevokedBy { get; init; }

    /// <summary>
    /// What the server keeps of each call the grant covers (sdk/09 L-60): <c>decisions</c> (which tool ran, when, where),
    /// <c>calls</c> (also its arguments, after the server's masking of personal data) or <c>results</c> (also a short
    /// summary of its result). Null from a server that does not say.
    /// </summary>
    public string? EvidenceLevel { get; init; }

    /// <summary>For Bypass: how and when the person signed in again to switch it on (sdk/41); null for other grants.</summary>
    public GrantStepUp? StepUp { get; init; }

    /// <summary>The server's clock when it answered: the marker counts down <see cref="ExpiresAt"/> against it.</summary>
    public DateTimeOffset? ServerNow { get; init; }

    /// <summary>What the server cut from the request (<c>minutes</c>, <c>classes</c>); empty when it granted all of it.</summary>
    public List<string> Narrowed { get; init; } = new();

    /// <summary>Whether the grant can still cover a call: active or paused, not revoked, expired or spent.</summary>
    [JsonIgnore]
    public bool IsLive => Status is "active" or "paused";
}

/// <summary><c>GET /v1/agents/{agent}/grants</c>: the device's live grants for the agent.</summary>
public sealed record GrantList
{
    public List<AgentGrant> Data { get; init; } = new();
    public DateTimeOffset? ServerNow { get; init; }
}
