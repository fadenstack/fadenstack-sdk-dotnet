using Fadenstack.Client.Tests;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;
using static Fadenstack.Agents.Tests.ApprovalTests;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// Bypass held by the server (sdk/09 §3.12, sdk/40 A3): offered only where the profile says so, switched on only through
/// the confirmation and the server's grant, covering what the gateway covers while the turn runs in Bypass, never an
/// admin's rule; the turn's calls are reported, Stop ends it, and the app-coded <see cref="PermissionMode.Bypass"/> goes
/// through the same grant.
/// </summary>
public class BypassTests
{
    private const string Offered =
        """{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"],"bypass":{"allowed":true,"longest_minutes":60,"evidence":"calls"}}""";

    private static (Gateway Gateway, FakeGrants Grants) Server(string approvals = Offered, string max = "\"bypass\"")
    {
        var gateway = new Gateway { MaxLevel = max };
        var grants = gateway.UseGrants(approvals);
        return (gateway, grants);
    }

    /// <summary>The gateway says the conversation's Bypass grant covers the call, then the model calls the tool.</summary>
    private static Gateway Covered(Gateway gateway, string callId, string tool, string arguments, string grantId) =>
        gateway.Then(Gateway.Custom("faden.grant_used", new { toolCallId = callId, toolCallName = tool, grantId }),
            Gateway.ToolCalls((callId, tool, arguments)), Gateway.Finish("tool_calls"), FakeServer.Done);

    private sealed class Card : IApprovalHandler, IGrantHandler
    {
        private readonly bool _allow;

        public Card(bool allow = true) => _allow = allow;

        public List<GrantOffer> Offers { get; } = new();

        public List<ApprovalRequest> Asked { get; } = new();

        public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return Task.FromResult(ApprovalDecision.Deny());
        }

        public Task<GrantAnswer> RequestGrantAsync(GrantOffer offer, CancellationToken cancellationToken)
        {
            Offers.Add(offer);
            return Task.FromResult(_allow
                ? new GrantAnswer { Allowed = true, Duration = offer.Preselected, ShownText = "Bypass for 30 minutes." }
                : GrantAnswer.NotNow);
        }
    }

    [Fact]
    public async Task Bypass_is_reachable_only_where_the_server_offers_it()
    {
        var (gateway, _) = Server();
        using var agent = gateway.Builder().UseApprovals(new Card()).Build();
        await agent.StartAsync();
        Assert.Equal((ToolMode.Bypass, true), (agent.Ceiling, agent.BypassOffered));

        var (older, _) = Server(approvals: """{"standing":null,"longest_grant_minutes":120,"grant_classes":["read","write"]}""");
        using var without = older.Builder().UseApprovals(new Card()).Build();
        await without.StartAsync();
        Assert.Equal((ToolMode.Auto, false), (without.Ceiling, without.BypassOffered));

        var (upToAuto, _) = Server(max: "\"auto\"");
        using var capped = upToAuto.Builder().UseApprovals(new Card()).Build();
        await capped.StartAsync();
        Assert.Equal(ToolMode.Auto, capped.Ceiling);
    }

    [Fact]
    public async Task Without_its_grant_a_chat_set_to_bypass_runs_in_auto()
    {
        var (gateway, _) = Server();
        using var agent = gateway.Builder().UseApprovals(new Card()).Build();
        var session = await agent.StartSessionAsync(ToolMode.Bypass);

        Assert.Equal((ToolMode.Bypass, ToolMode.Auto), (session.ToolMode, session.EffectiveToolMode));
        Assert.Null(session.Bypass);
    }

    [Fact]
    public async Task The_confirmation_comes_first_and_bypass_holds_only_once_the_server_made_its_grant()
    {
        var (gateway, grants) = Server();
        var declining = new Card(allow: false);
        using var agent = gateway.Builder().UseApprovals(declining).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        Assert.Null(await session.StartBypassAsync());
        var offer = declining.Offers.Single();
        Assert.True(offer.Bypass);
        Assert.Equal((TimeSpan.FromMinutes(30), 3, 0), (offer.Preselected, offer.Durations.Count, offer.Places.Count));
        Assert.Empty(grants.Made);
        Assert.Equal(ToolMode.Ask, session.ToolMode);

        using var allowing = gateway.Builder().UseApprovals(new Card()).Build();
        var other = await allowing.StartSessionAsync(ToolMode.Ask);
        var grant = await other.StartBypassAsync("Renaming 400 drawings");

        var made = grants.Made.Single();
        Assert.Equal(("bypass", 30, other.Id), (made.Json.GetProperty("kind").GetString(), made.Json.GetProperty("minutes").GetInt32(),
            made.Header("x-faden-conversation-id")));
        Assert.Equal("Renaming 400 drawings", made.Json.GetProperty("reason").GetString());
        Assert.StartsWith("Fadenstack.Agents/", made.Json.GetProperty("client_version").GetString());
        Assert.Equal(64, made.Json.GetProperty("shown_text_sha256").GetString()!.Length);
        Assert.Equal((grant!.Id, ToolMode.Bypass, ToolMode.Bypass), (other.Bypass?.Id, other.ToolMode, other.EffectiveToolMode));
    }

    [Fact]
    public async Task Under_bypass_a_destructive_call_the_gateway_covers_runs_unasked_and_the_turn_is_reported()
    {
        var (gateway, grants) = Server();
        var tools = new PageTools();
        var card = new Card();
        using var agent = gateway.Builder().AddTools(tools).UseApprovals(card).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        var grant = (await session.StartBypassAsync())!;
        Covered(gateway, "call-1", "delete_page", """{"page":1}""", grant.Id).Says("Deleted.");

        await session.SendAsync("Delete page 1.");

        Assert.Equal(new[] { "delete_page 1" }, tools.Ran);
        Assert.Empty(card.Asked);
        var entry = Assert.Single(Decisions(session));
        Assert.Equal(("grant", grant.Id), (Str(entry, "scope"), Str(entry, "grant_id")));
        var report = grants.Reports.Single();
        Assert.Equal((grant.Id, "call-1"), (report.Grant, Assert.Single(report.Calls)));
        Assert.Contains(gateway.Server.Requests, r => r.Path.EndsWith("/chat/completions") && r.Header("x-faden-tool-mode") == "bypass");
    }

    [Fact]
    public async Task Under_bypass_a_call_the_gateway_did_not_cover_still_asks_and_so_does_an_admins_rule()
    {
        var (gateway, _) = Server();
        var tools = new PageTools();
        var card = new Card();
        using var agent = gateway.Builder().AddTools(tools).UseApprovals(card).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        var grant = (await session.StartBypassAsync())!;
        gateway.Calls(("call-1", "delete_page", """{"page":1}""")).Says("Not deleted.");
        MarkedCall(gateway, "call-2", "add_comment", """{"page":1,"text":"a"}""",
            new { toolCallId = "call-2", toolCallName = "add_comment", message = "Comments need approval.", rule = true }).Says("Ok.");

        await session.SendAsync("Delete page 1.");
        await session.SendAsync("Comment.");

        Assert.Equal(2, card.Asked.Count);
        Assert.True(card.Asked[1].RequiredByRule);
        Assert.Empty(tools.Ran);
        Assert.NotNull(grant);
    }

    [Fact]
    public async Task Another_conversations_bypass_covers_nothing()
    {
        var (gateway, grants) = Server();
        var tools = new PageTools();
        var card = new Card();
        using var agent = gateway.Builder().AddTools(tools).UseApprovals(card).Build();
        var first = await agent.StartSessionAsync(ToolMode.Ask);
        var grant = (await first.StartBypassAsync())!;
        var second = await agent.StartSessionAsync(ToolMode.Bypass);
        Covered(gateway, "call-1", "delete_page", """{"page":1}""", grant.Id).Says("No.");

        await second.SendAsync("Delete page 1.");

        Assert.Empty(tools.Ran);
        Assert.Single(card.Asked);
        Assert.Empty(grants.Reports);
    }

    [Fact]
    public async Task Stop_ends_the_grant_and_the_chat_goes_back_to_its_mode_and_leaving_bypass_ends_it_too()
    {
        var (gateway, grants) = Server();
        using var agent = gateway.Builder().UseApprovals(new Card()).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        var grant = (await session.StartBypassAsync())!;

        await session.StopBypassAsync();

        Assert.Equal(("revoked", ToolMode.Ask, (AgentGrant?)null), (grants.Status(grant.Id), session.ToolMode, session.Bypass));

        var again = (await session.StartBypassAsync())!;
        session.ToolMode = ToolMode.Auto;
        gateway.Says("Fine.");
        await session.SendAsync("Go on.");
        Assert.Equal("revoked", grants.Status(again.Id));
    }

    [Fact]
    public async Task An_app_set_to_bypass_makes_the_same_grant_without_a_card_never_from_read_only()
    {
        var (gateway, grants) = Server();
        var card = new Card();
        using var agent = gateway.Builder().UsePermissionMode(PermissionMode.Bypass).UseApprovals(card).Build();
        var readOnly = await agent.StartSessionAsync(ToolMode.ReadOnly);
        gateway.Says("Hello.");
        await readOnly.SendAsync("Hi.");
        Assert.Empty(grants.Made);

        var session = await agent.StartSessionAsync(ToolMode.Ask);
        gateway.Says("Hello.");
        await session.SendAsync("Hi.");

        Assert.Empty(card.Offers);
        var made = grants.Made.Single();
        Assert.Equal(("bypass", 60, "PermissionMode.Bypass"), (made.Json.GetProperty("kind").GetString(),
            made.Json.GetProperty("minutes").GetInt32(), made.Json.GetProperty("reason").GetString()));
        Assert.Equal(ToolMode.Bypass, session.EffectiveToolMode);
    }

    [Fact]
    public async Task Every_request_says_the_sdk_supports_bypass()
    {
        var (gateway, _) = Server();
        using var agent = gateway.Builder().UseApprovals(new Card()).Build();
        await agent.StartAsync();

        var profile = gateway.Server.Requests.First(r => r.Path == "/v1/agents/docs-assistant/profile");
        Assert.Equal("tool_mode.bypass", profile.Header("x-faden-client-supports"));
    }
}
