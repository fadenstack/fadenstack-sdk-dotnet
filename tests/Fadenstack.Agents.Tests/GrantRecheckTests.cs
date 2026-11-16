using Fadenstack.Client.Tests;
using Fadenstack.Tools;
using Xunit;
using static Fadenstack.Agents.Tests.ApprovalTests;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The second line reads the server's grant each time (09-handoffs 2.3, F04 R4; as the TypeScript SDK does): for the call,
/// and once more as it goes out. What the device's copy last saw never lets a call run on its own.
/// </summary>
public class GrantRecheckTests
{
    private const string Comment = """{"page":1,"text":"Fine."}""";
    private const string Route = "/v1/agents/docs-assistant/grants/";

    /// <summary>The gateway says a grant covers the call, then the model calls the tool.</summary>
    private static Gateway CoveredCall(Gateway gateway, string grantId) =>
        gateway.Then(Gateway.Custom("faden.grant_used", new { toolCallId = "call-1", toolCallName = "add_comment", grantId }),
            Gateway.ToolCalls(("call-1", "add_comment", Comment)), Gateway.Finish("tool_calls"), FakeServer.Done);

    private static int Reads(Gateway gateway, string grantId) =>
        gateway.Server.Requests.Count(r => r.Method == "GET" && r.Path == Route + grantId);

    [Fact]
    public async Task A_covered_call_reads_the_servers_grant_for_the_call_and_again_as_it_goes_out()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { "add_comment" } });
        CoveredCall(gateway, id).Says("Done.");
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Deny())).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");

        Assert.Single(tools.Ran);
        Assert.Equal(2, Reads(gateway, id));
        Assert.Equal("grant", Str(Decisions(session).Single(), "scope"));
    }

    [Fact]
    public async Task A_grant_the_server_ended_after_the_copy_saw_it_covers_nothing_and_the_person_is_asked()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { "add_comment" } });
        CoveredCall(gateway, id).Says("Done.");
        var tools = new PageTools();
        var asked = 0;
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Deny()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        await agent.Grants.RefreshAsync();
        Assert.Contains(agent.Grants.Live, g => g.Id == id); // the copy holds it, running
        grants.End(id, "admin"); // the copy has not heard

        await session.SendAsync("Comment.");

        Assert.Equal(1, asked);
        Assert.Empty(tools.Ran);
        Assert.DoesNotContain(agent.Grants.Live, g => g.Id == id); // the read told the copy at once
        Assert.Equal("admin", await agent.Grants.WhyEndedAsync(id));
    }

    [Fact]
    public async Task A_grant_ended_between_the_approval_and_the_dispatch_runs_nothing()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { "add_comment" } });
        CoveredCall(gateway, id).Says("It did not run.");
        var tools = new PageTools();
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Deny()))
            .AddToolStage(StageOrder.Custom, new EndsGrant(() => grants.End(id, "person"))) // the marker's Stop, meanwhile
            .Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");

        Assert.Empty(tools.Ran);
        Assert.Contains("no longer covers this call", gateway.ChatRequests.Last().Json.ToString());
    }

    [Fact]
    public async Task A_server_that_cannot_say_covers_nothing_and_the_person_is_asked()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { "add_comment" } });
        grants.CanSayWhy = false; // reads of one grant answer 404, as from an older server
        CoveredCall(gateway, id).Says("Done.");
        var tools = new PageTools();
        var asked = 0;
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Approve()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");

        Assert.Equal(1, asked);
        Assert.Single(tools.Ran); // the person allowed it once
        Assert.Equal("once", Str(Decisions(session).Single(), "scope"));
    }

    [Fact]
    public async Task A_grant_bound_to_another_conversation_covers_nothing_here()
    {
        var gateway = new Gateway();
        var grants = gateway.UseGrants();
        var id = grants.Hold("time", new { tools = new[] { "add_comment" } });
        grants.TamperShown = g => g["conversation_id"] = "another-chat";
        CoveredCall(gateway, id).Says("Done.");
        var tools = new PageTools();
        var asked = 0;
        using var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((_, _) => { asked++; return Task.FromResult(ApprovalDecision.Deny()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");

        Assert.Equal(1, asked);
        Assert.Empty(tools.Ran);
    }

    private sealed class EndsGrant(Action end) : IToolStage
    {
        public Task<object?> InvokeAsync(ToolInvocation invocation, ToolInvoker next, CancellationToken cancellationToken)
        {
            end();
            return next(invocation, cancellationToken);
        }
    }
}
