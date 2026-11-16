using Fadenstack.Tools;

namespace Fadenstack.Agents;

/// <summary>
/// The approval floor, "the never-skip list": what no tool mode skips, not <c>Auto</c> and not <c>Bypass</c>. Bypass skips
/// questions, never rules (sdk/38 §4.3; sdk/51 F08 §2, owner, 16 Nov). This records the rule the SDK and the gateway already
/// follow; it changes no behaviour. Each id names one item, and <see cref="NeverSkipped"/> lists them in the order of
/// sdk/38 §4.3. Tests per level (Ask, Auto, Bypass) hold each item to it (<c>ApprovalFloorTests</c>).
/// </summary>
/// <remarks>
/// OWASP ASVS 5.0 V8 (authorization in a trusted layer) and OWASP Top 10 for LLM Applications 2025, LLM06 (excessive agency):
/// a rule or a human approval for high-impact actions holds whatever the person's chosen level.
/// </remarks>
public static class ApprovalFloor
{
    /// <summary>
    /// Deny rules, and hidden or refused tools (sdk/38 §4.3, "Hidden and refused tools, deny rules"). They stay hidden or refused
    /// at every level: the call does not run and nobody is asked. Bypass skips questions, never rules.
    /// </summary>
    public const string Refused = "refused";

    /// <summary>
    /// An administrator's "Ask first" rule (sdk/38 §4.3, "Admin rules"). The gateway marks the call <c>rule: true</c>
    /// (<see cref="ApprovalRequest.RequiredByRule"/>); the person is asked at every level, whatever they allowed for the chat or
    /// always, and under a Bypass grant.
    /// </summary>
    public const string AskFirstRule = "ask_first_rule";

    /// <summary>
    /// The author's <c>approval: always</c> in the tool contract (sdk/38 §4.3, "The agent's own ask rule"; <see cref="AgentTool.Approval"/>).
    /// The call asks every time, at every level, like an administrator's rule.
    /// </summary>
    public const string AuthorAlways = "author_always";

    /// <summary>
    /// A tool that acts in the physical world (sdk/38 §4.3, <c>physical</c>): it asks per call, unless an EE administrator allows it
    /// for that agent. Not marked yet: see <see cref="NotMarkedYet"/>.
    /// </summary>
    public const string Physical = "physical";

    /// <summary>
    /// An open-world tool in a turn that read untrusted content (sdk/38 §4.3, <c>open_world</c> in a tainted turn). Not marked
    /// yet: see <see cref="NotMarkedYet"/>.
    /// </summary>
    public const string OpenWorldTainted = "open_world_tainted";

    /// <summary>
    /// A question an MCP server asks the person itself (elicitation; sdk/51 F08 §2). It goes to the app's question handler
    /// (<c>LocalMcpModule.Ask</c>) and the SDK never answers it itself, at any level.
    /// </summary>
    public const string McpServerQuestion = "mcp_server_question";

    /// <summary>
    /// A call the gateway holds for the person, a gateway tool's approval (<c>faden.approval_required</c> with an approval id;
    /// sdk/51 F08 §2). The person is asked at every level; Bypass never answers the gateway, only the server may skip its own question.
    /// </summary>
    public const string GatewayHeld = "gateway_held";

    /// <summary>
    /// Destructive and unclassified calls. Not on <see cref="NeverSkipped"/>: they ask at every level except <c>Bypass</c>,
    /// which covers them only where the tenant allows it, after the second sign-in (sdk/41; sdk/38 §4.3, "Grants additionally
    /// never cover"). A grant for a time or for a stretch of work never covers them.
    /// </summary>
    public const string DestructiveOrUnclassified = "destructive_or_unclassified";

    /// <summary>
    /// The items the SDK and the gateway have no markers for yet: <see cref="Physical"/> and <see cref="OpenWorldTainted"/>
    /// (desktop 16 F-10, intent IM-09). Until then such a tool needs <c>approval: always</c> in its contract or an administrator's
    /// "Ask first" rule to be held to the floor (sdk/38 §4.3, "Built").
    /// </summary>
    public static IReadOnlyList<string> NotMarkedYet { get; } = new[] { Physical, OpenWorldTainted };

    /// <summary>What no level skips, in the order of sdk/38 §4.3.</summary>
    public static IReadOnlyList<string> NeverSkipped { get; } = new[]
    {
        Refused, AskFirstRule, AuthorAlways, Physical, OpenWorldTainted, McpServerQuestion, GatewayHeld,
    };
}
