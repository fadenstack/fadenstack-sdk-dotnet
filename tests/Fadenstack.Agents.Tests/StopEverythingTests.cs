using System.Net;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// "Stop everything" (release plan 8.11) and the agent off-switch (8.3): the emergency stop ends every running turn and every grant
/// the agent holds, also offline; a refused sign-in ends a turn at once; and an agent the organisation turned off makes no request but its
/// events and manifest, whatever route finds it out. TypeScript: <c>stopEverything.test.ts</c>.
/// </summary>
public class StopEverythingTests
{
    private static readonly string[] OnlyThese = { "/v1/agents/docs-assistant/profile", "/v1/agents/docs-assistant/events", "/v1/agents/docs-assistant/manifest" };

    /// <summary>Answers a call of <c>delete_page</c> with a card that waits; whatever else is asked is allowed for the chat.</summary>
    private sealed class Cards
    {
        public SemaphoreSlim Asked { get; } = new(0);

        public List<ApprovalRequest> Requests { get; } = new();

        public Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.ToolName != "delete_page")
            {
                return Task.FromResult(ApprovalDecision.ApproveForSession());
            }
            var waiting = new TaskCompletionSource<ApprovalDecision>();
            cancellationToken.Register(() => waiting.TrySetCanceled(cancellationToken));
            Asked.Release();
            return waiting.Task;
        }

        public async Task WaitAsync() => Assert.True(await Asked.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class Provider : ITokenProvider
    {
        public volatile bool Refuse;

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Refuse ? throw new FadenstackSignInRequiredException("The sign-in ended.") : Task.FromResult("agent-token");

        public Task InvalidateAsync(string rejectedToken, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static Task<bool> Run(ChatSession session, string text) => Task.Run(async () =>
    {
        try
        {
            await foreach (var _ in session.StreamAsync(text))
            {
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            return true; // stopped
        }
    });

    private static string Delete(int page) => $$"""{"page":{{page}}}""";

    /// <summary>An agent with two chats that each hold a "for this chat" approval and a running turn waiting for the person.</summary>
    private static async Task<(FadenstackAgent Agent, Gateway Gateway, FakeGrants Grants, ChatSession A, ChatSession B, Task<bool> TurnA, Task<bool> TurnB,
        MemoryApprovalStore Store)> TwoBusyChatsAsync()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        gateway.Calls(("c1", "add_comment", """{"page":1,"text":"a"}""")).Says("One.")
            .Calls(("c2", "add_comment", """{"page":2,"text":"b"}""")).Says("Two.")
            .Calls(("d1", "delete_page", Delete(1))).Calls(("d2", "delete_page", Delete(2)));
        var cards = new Cards();
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("read_page", ToolClass.Read, DateTimeOffset.UtcNow) { Source = "host" });
        var agent = gateway.Builder(approvals: store).AddTools(new PageTools()).UseApprovals(cards.AskAsync).Build();
        var a = await agent.StartSessionAsync(ToolMode.Ask);
        await a.SendAsync("one");
        var b = await agent.StartSessionAsync(ToolMode.Ask);
        await b.SendAsync("two");
        var turnA = Run(a, "three");
        await cards.WaitAsync();
        var turnB = Run(b, "four");
        await cards.WaitAsync();
        return (agent, gateway, grants, a, b, turnA, turnB, store);
    }

    [Fact]
    public async Task Stop_everything_stops_both_turns_answers_their_cards_no_and_ends_every_grant_but_keeps_what_was_allowed_always()
    {
        var (agent, gateway, grants, a, b, turnA, turnB, store) = await TwoBusyChatsAsync();
        using var _ = agent;
        await agent.Grants.GrantForTimeAsync("read_page", TimeSpan.FromMinutes(30));
        await agent.Grants.GrantForTimeAsync("add_comment", TimeSpan.FromMinutes(30));
        grants.Hold("time", new { tools = new[] { "stamp" } }); // another app's, which this copy does not know yet
        Assert.Equal(new[] { "add_comment" }, a.ApprovedForSession);
        Assert.True(a.IsRunning && b.IsRunning);

        var result = await agent.StopAllAsync();

        Assert.Equal(2, result.TurnsStopped);
        Assert.Equal(2, result.ChatApprovalsEnded); // one "for this chat" approval in each chat
        Assert.Equal(5, result.GrantsEnded); // 3 on the server, 2 in the chats
        Assert.Equal(0, result.GrantsNotTold);
        Assert.True(await turnA && await turnB); // both turns ended as stopped
        Assert.Empty(a.ApprovedForSession);
        Assert.Empty(b.ApprovedForSession);
        Assert.Empty(agent.Grants.Live);
        Assert.All(new[] { "g-1", "g-2", "g-3" }, id => Assert.Equal("revoked", grants.Status(id)));
        Assert.Equal(new[] { "read_page" }, (await agent.ListApprovalsAsync()).Select(x => x.Tool)); // "always" stays
        Assert.All(new[] { a, b }, chat =>
        {
            Assert.Equal(TurnStatus.Cancelled, chat.Transcript.Last(e => e.Type == EntryTypes.TurnEnd).Data.GetProperty("status").GetString());
            var answer = ApprovalTests.Decisions(chat).Last();
            Assert.False(answer.GetProperty("approved").GetBoolean()); // the card was answered no
            Assert.Equal("Stopped.", ApprovalTests.Str(answer, "reason"));
        });
        Assert.False(a.IsRunning || b.IsRunning);
    }

    [Fact]
    public async Task Stop_everything_ends_the_grants_here_and_counts_those_the_server_could_not_be_told_about_after_one_more_try()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        using var agent = gateway.Builder().Build();
        await agent.StartAsync();
        await agent.Grants.GrantForTimeAsync("read_page", TimeSpan.FromMinutes(30));
        await agent.Grants.GrantForTimeAsync("add_comment", TimeSpan.FromMinutes(30));
        grants.RevokeOffline = true; // the server cannot be reached when a grant is ended

        var result = await agent.StopAllAsync();

        Assert.Equal((0, 2, 2), (result.TurnsStopped, result.GrantsEnded, result.GrantsNotTold));
        Assert.Equal(4, gateway.Server.Requests.Count(r => r.Method == "DELETE")); // each tried twice
        Assert.Empty(agent.Grants.Live); // ended here: none covers a call now
        Assert.True(agent.Grants.EndedHere("g-1") && agent.Grants.EndedHere("g-2"));
        // The server still says active (it was not told), and a check of the grant does not bring it back.
        Assert.Equal("active", grants.Status("g-1"));
        var (covered, _) = await agent.Grants.CheckAsync("g-1", "any", "read_page", ToolClass.Read, null, CancellationToken.None);
        Assert.False(covered);
    }

    [Fact]
    public async Task Stop_everything_works_with_the_server_down_and_says_nothing_ran_when_nothing_did()
    {
        var gateway = new Gateway();
        using var agent = gateway.Builder().Build();
        await agent.StartAsync();
        var session = await agent.StartSessionAsync();

        var result = await agent.StopAllAsync();

        Assert.True(result.NothingRan);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task Stop_everything_for_several_agents_stops_them_all_and_adds_the_counts()
    {
        var first = await TwoBusyChatsAsync();
        var second = await TwoBusyChatsAsync();
        using var one = first.Agent;
        using var two = second.Agent;

        var result = await FadenstackAgent.StopAllAsync(new[] { first.Agent, second.Agent });

        Assert.Equal(4, result.TurnsStopped);
        Assert.Equal(4, result.ChatApprovalsEnded);
        Assert.True(await first.TurnA && await first.TurnB && await second.TurnA && await second.TurnB);
    }

    [Fact]
    public async Task A_refused_refresh_in_the_middle_of_a_turn_ends_it_at_once_and_no_tool_of_the_turn_runs_after()
    {
        var provider = new Provider();
        var gateway = new Gateway().UseHostChecks(new HostDecision("run", "read", "auto"))
            .Then(_ =>
            {
                provider.Refuse = true; // the sign-in ends right after the model answered
                return FakeServer.Sse(Gateway.ToolCalls(("a", "read_page", """{"page":1}"""), ("b", "read_page", """{"page":2}""")),
                    Gateway.Finish("tool_calls"), FakeServer.Done);
            }).Says("Never.");
        var tools = new PageTools();
        using var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://faden.test"), provider, o => o.HttpClient = new HttpClient(gateway.Server))
            .UseAgent(Gateway.Slug).UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
            .AddTools(tools).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        await Assert.ThrowsAsync<FadenstackSignInRequiredException>(() => session.SendAsync("Read."));

        Assert.Empty(tools.Ran); // neither call ran
        Assert.Empty(gateway.Checks); // and the second was not even looked at
        Assert.Single(gateway.ChatRequests); // no second model round
        Assert.Equal(TurnStatus.Error, session.Transcript.Last(e => e.Type == EntryTypes.TurnEnd).Data.GetProperty("status").GetString());
    }

    private sealed class ServerTools
    {
        public FadenstackAgent? Agent { get; set; }

        public List<string> Ran { get; } = new();

        [AgentTool(ToolClass.Read), System.ComponentModel.Description("Asks the server something.")]
        public async Task<string> ReadPage(int page)
        {
            Ran.Add("read_page");
            await Agent!.ServerClient!.Agent!.ListToolsAsync(); // needs the sign-in
            return "page";
        }

        [AgentTool(ToolClass.Write), System.ComponentModel.Description("Adds a comment, on this device only.")]
        public string AddComment(int page, string text)
        {
            Ran.Add("add_comment");
            return "added";
        }
    }

    [Fact]
    public async Task A_tool_whose_server_call_is_refused_a_sign_in_ends_the_turn_and_the_next_call_of_the_turn_never_runs()
    {
        var provider = new Provider();
        var gateway = new Gateway().Then(_ =>
        {
            provider.Refuse = true;
            return FakeServer.Sse(Gateway.ToolCalls(("a", "read_page", """{"page":1}"""), ("b", "add_comment", """{"page":1,"text":"x"}""")),
                Gateway.Finish("tool_calls"), FakeServer.Done);
        }).Says("Never.");
        var tools = new ServerTools();
        using var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://faden.test"), provider, o => o.HttpClient = new HttpClient(gateway.Server))
            .UseAgent(Gateway.Slug).UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
            .AddTools(tools).Build();
        tools.Agent = agent;
        provider.Refuse = false;
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        await Assert.ThrowsAsync<FadenstackSignInRequiredException>(() => session.SendAsync("Go."));

        Assert.Equal(new[] { "read_page" }, tools.Ran); // the second call did not run after the refusal
        Assert.Single(gateway.ChatRequests);
    }

    // -- the off-switch ----------------------------------------------------------------------

    [Fact]
    public async Task An_agent_that_starts_off_makes_no_request_but_its_profile_events_and_manifest_and_refuses_a_turn_with_the_sentence()
    {
        var gateway = new Gateway().TurnOff();
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools).Build();
        var changes = 0;
        agent.TurnedOffChanged += (_, _) => changes++;

        await agent.StartAsync();
        var session = await agent.StartSessionAsync(ToolMode.Auto);
        var result = await session.SendAsync("Hello.");
        await session.CloseAsync(); // reports the chat's counts: the events still go

        Assert.True(agent.TurnedOff);
        Assert.False(agent.Profile.Enabled);
        Assert.Equal(1, changes);
        Assert.Equal(ChatOutcome.AgentDisabled, result.Outcome);
        Assert.Equal("Your organisation turned this agent off.", result.Notice!.Message);
        Assert.Equal("agent_disabled", result.Notice.Outcome);
        Assert.Empty(gateway.ChatRequests);
        Assert.Empty(tools.Ran);
        Assert.Empty(gateway.RefusedWhileOff(0)); // not one request to a refused route
        Assert.NotEmpty(gateway.Reports);
        Assert.All(gateway.Server.Requests, r => Assert.Contains(r.Path, OnlyThese));
    }

    [Fact]
    public async Task The_switch_in_the_middle_of_a_turn_ends_it_with_the_sentence_ends_the_grants_and_asks_nothing_more()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var tools = new PageTools();
        gateway.Then(_ =>
        {
            gateway.TurnOff(); // the administrator turns it off while the first answer streams
            return FakeServer.Sse(Gateway.ToolCalls(("a", "read_page", """{"page":1}""")), Gateway.Finish("tool_calls"), FakeServer.Done);
        }).Says("Never.");
        using var agent = gateway.Builder().AddTools(tools).Build();
        await agent.StartAsync();
        await agent.Grants.GrantForTimeAsync("read_page", TimeSpan.FromMinutes(30));
        var session = await agent.StartSessionAsync(ToolMode.Auto);
        var before = gateway.Server.Requests.Count;

        var result = await session.SendAsync("Read.");

        Assert.Equal(ChatOutcome.AgentDisabled, result.Outcome);
        Assert.Equal("Your organisation turned this agent off.", result.Notice!.Message);
        Assert.True(agent.TurnedOff);
        Assert.Equal(2, gateway.ChatRequests.Count); // the second round hit the refusal
        Assert.Single(gateway.RefusedWhileOff(gateway.OffAt)); // the refusal itself; nothing was asked after it
        Assert.Empty(agent.Grants.Live);
        Assert.Equal("revoked", grants.Status("g-1"));
        Assert.Equal("agent_turned_off", await agent.Grants.WhyEndedAsync("g-1"));
        Assert.Equal(TurnStatus.Error, session.Transcript.Last(e => e.Type == EntryTypes.TurnEnd).Data.GetProperty("status").GetString());
        // A new turn is refused without a request.
        var again = await session.SendAsync("Again.");
        Assert.Equal(ChatOutcome.AgentDisabled, again.Outcome);
        Assert.Equal(2, gateway.ChatRequests.Count);
    }

    [Fact]
    public async Task A_refusal_at_a_tool_calls_check_ends_the_turn_and_the_calls_after_it_never_run()
    {
        var gateway = new Gateway().UseHostChecks(new HostDecision("run", "read", "auto"))
            .Calls(("a", "read_page", """{"page":1}"""), ("b", "read_page", """{"page":2}""")).Says("Never.");
        gateway.OnCheck = _ => FakeServer.Respond(HttpStatusCode.Forbidden, new
        {
            error = new { type = "permission_error", code = "agent_disabled", message = "Your organisation turned this agent off." },
        });
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools).Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);

        var result = await session.SendAsync("Read.");

        Assert.Equal(ChatOutcome.AgentDisabled, result.Outcome);
        Assert.True(agent.TurnedOff);
        Assert.Empty(tools.Ran);
        Assert.Single(gateway.Checks); // the second call was not checked
        Assert.Single(gateway.ChatRequests);
    }

    [Fact]
    public async Task A_grants_route_that_answers_agent_disabled_turns_the_agent_off_and_nothing_asks_the_server_again()
    {
        var gateway = new Gateway();
        gateway.UseGrants();
        using var agent = gateway.Builder().Build();
        await agent.StartAsync();
        gateway.TurnOff();
        var before = gateway.Server.Requests.Count;

        var error = await Assert.ThrowsAsync<FadenstackPermissionException>(() => agent.Grants.RefreshAsync());

        Assert.True(error.IsAgentDisabled);
        Assert.True(agent.TurnedOff);
        var asked = gateway.Server.Requests.Count;
        await Assert.ThrowsAsync<FadenstackPermissionException>(() => agent.Grants.GrantForTimeAsync("read_page", TimeSpan.FromMinutes(15)));

        await Assert.ThrowsAsync<FadenstackPermissionException>(() => agent.ListServerToolsAsync());
        await Assert.ThrowsAsync<FadenstackPermissionException>(() => agent.SetUserLanguageAsync("de"));
        Assert.Equal(asked, gateway.Server.Requests.Count); // no retry, no further request
        Assert.Single(gateway.RefusedWhileOff(before));
    }

    [Fact]
    public async Task Check_again_reads_the_profile_only_and_turns_the_agent_back_on_when_it_says_enabled()
    {
        var gateway = new Gateway().TurnOff().Says("Back.");
        using var agent = gateway.Builder().Build();
        await agent.StartAsync();
        var changes = 0;
        agent.TurnedOffChanged += (_, _) => changes++;
        var reads = gateway.ProfileReads;

        Assert.True(await agent.CheckAgainAsync()); // still off
        Assert.Equal(reads + 1, gateway.ProfileReads);
        Assert.Equal(0, changes);

        gateway.TurnOn();
        Assert.False(await agent.CheckAgainAsync());

        Assert.False(agent.TurnedOff);
        Assert.Equal(1, changes);
        Assert.Empty(gateway.RefusedWhileOff(0));
        var result = await (await agent.StartSessionAsync()).SendAsync("Hello.");
        Assert.Equal(ChatOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task An_older_server_with_no_enabled_key_is_on()
    {
        var gateway = new Gateway();
        gateway.Server.On("GET", "/v1/agents/docs-assistant/profile", _ => FakeServer.Respond(HttpStatusCode.OK,
            """{"object":"agent.profile","id":"a-1","slug":"docs-assistant","name":"Docs","version":3,"draft":false,"host_tool_contract":[],"features":["agents"]}"""));
        using var agent = gateway.Builder().Build();

        await agent.StartAsync();

        Assert.True(agent.Profile.Enabled);
        Assert.False(agent.TurnedOff);
    }
}
