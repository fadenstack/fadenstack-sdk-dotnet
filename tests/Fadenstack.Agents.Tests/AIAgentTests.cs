using System.ComponentModel;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Client.Tests;
using Fadenstack.Endpoints;
using Fadenstack.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>sdk/51 F02 step 2 (I02 commits 10-12): the agent as a Microsoft Agent Framework AIAgent.</summary>
public class AIAgentTests
{
    private const string Definition = """
        {"slug":"docs-assistant","name":"Docs assistant","version":{"instructions":"Be brief.",
          "host_tool_contract":[{"name":"read_page","risk":"read"}]}}
        """;

    private static (FadenstackAgent Agent, OpenAiFake Fake, MemorySessionStore Store) Make(bool approvals = true)
    {
        var fake = new OpenAiFake();
        var store = new MemorySessionStore();
        var builder = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "m", Handler = fake })
            .UseLocalProfile(AgentDefinition.FromJson(Definition))
            .UseSessionStore(store)
            .UseApprovalStore(new MemoryApprovalStore())
            .AddTools(new Tools());
        if (approvals)
        {
            builder.UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve()));
        }
        return (builder.Build(), fake, store);
    }

    public sealed class Tools
    {
        [AgentTool(ToolClass.Read), Description("Reads a page.")]
        public string read_page(int page) => $"Page {page}: hello";
    }

    private static async Task<List<(string Type, string Data)>> TranscriptAsync(FadenstackAgent agent, MemorySessionStore store, string sessionId)
    {
        var entries = await store.ReadAsync(agent.Slug, sessionId, default);
        // Ids and times differ between two runs; what happened, in order, must not.
        return entries.Select(e => (e.Type, Scrub(e.Data))).ToList();
    }

    private static string Scrub(JsonElement data)
    {
        var json = data.GetRawText();
        foreach (var field in new[] { "\"duration_ms\":", "\"at\":" })
        {
            var at = json.IndexOf(field, StringComparison.Ordinal);
            while (at >= 0)
            {
                var end = json.IndexOfAny([',', '}'], at + field.Length);
                json = json.Substring(0, at) + json.Substring(end);
                at = json.IndexOf(field, StringComparison.Ordinal);
            }
        }
        return json;
    }

    [Fact]
    public async Task A_run_through_maf_gives_the_same_turn_as_the_native_send()
    {
        var (native, nativeFake, nativeStore) = Make();
        var (maf, mafFake, mafStore) = Make();
        foreach (var fake in new[] { nativeFake, mafFake })
        {
            fake.Answers.Enqueue(OpenAiFake.Calls(("call-1", "read_page", "{\"page\":3}")));
            fake.Answers.Enqueue(OpenAiFake.Text("Page 3 says hello."));
        }

        var session = await native.StartSessionAsync();
        var result = await session.SendAsync("What does page 3 say?");
        var mafSession = await maf.CreateSessionAsync();
        var response = await maf.RunAsync("What does page 3 say?", mafSession);

        Assert.Equal(result.Text, response.Text);
        Assert.Equal("Page 3 says hello.", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal("Completed", response.AdditionalProperties![FadenstackAgent.OutcomeProperty]);
        Assert.Equal("docs-assistant", response.AgentId);
        Assert.Equal(nativeFake.Requests.Select(r => r.Body), mafFake.Requests.Select(r => r.Body));
        var chat = Assert.IsType<FadenstackAgentSession>(mafSession).Chat;
        Assert.Equal(
            (await TranscriptAsync(native, nativeStore, session.Id)).Select(e => e.Type),
            (await TranscriptAsync(maf, mafStore, chat.Id)).Select(e => e.Type));
    }

    [Fact]
    public async Task A_session_survives_serializing_and_carries_only_its_id()
    {
        var (agent, fake, _) = Make();
        fake.Answers.Enqueue(OpenAiFake.Text("The code word is PELICAN."));
        fake.Answers.Enqueue(OpenAiFake.Text("PELICAN."));
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("Remember the code word PELICAN.", session);

        var state = await agent.SerializeSessionAsync(session);
        var again = await agent.DeserializeSessionAsync(state);
        await agent.RunAsync("What was the code word?", again);

        Assert.DoesNotContain("PELICAN", state.GetRawText());
        Assert.Contains(((FadenstackAgentSession)session).Chat.Id, state.GetRawText());
        Assert.Equal(((FadenstackAgentSession)session).Chat.Id, ((FadenstackAgentSession)again).Chat.Id);
        Assert.Same(((FadenstackAgentSession)again).Chat, again.GetService<ChatSession>());
        var history = fake.Requests.Last().Json.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("content").GetString()).ToList();
        Assert.Contains("Remember the code word PELICAN.", history);
        Assert.Contains("The code word is PELICAN.", history);
    }

    [Fact]
    public async Task A_streamed_run_shows_the_text_the_tool_and_the_outcome()
    {
        var (agent, fake, _) = Make();
        fake.Answers.Enqueue(OpenAiFake.Calls(("call-1", "read_page", "{\"page\":2}")));
        fake.Answers.Enqueue(OpenAiFake.Text("Two."));

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync("Page 2?"))
        {
            updates.Add(update);
        }

        Assert.Equal("Two.", string.Concat(updates.Select(u => u.Text)));
        var call = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Single();
        Assert.True(call.InformationalOnly); // the agent ran it; the caller must not
        Assert.Equal("read_page", call.Name);
        Assert.Equal("Page 2: hello", updates.SelectMany(u => u.Contents).OfType<FunctionResultContent>().Single().Result);
        Assert.Equal(ChatFinishReason.Stop, updates.Last().FinishReason);
        Assert.All(updates, u => Assert.Equal("docs-assistant", u.AgentId));
    }

    [Fact]
    public async Task Without_an_approval_handler_a_maf_run_is_refused()
    {
        var (agent, fake, _) = Make(approvals: false);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("Hi"));

        Assert.Contains("UseApprovals", e.Message);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Another_agents_session_is_refused()
    {
        var (one, _, _) = Make();
        var (other, _, _) = Make();
        var session = await one.CreateSessionAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => other.RunAsync("Hi", session));
    }

    [Fact]
    public void The_agent_is_an_aiagent_with_its_slug_and_name()
    {
        var (agent, _, _) = Make();
        AIAgent asMaf = agent;

        Assert.Equal("docs-assistant", asMaf.Id);
        Assert.Equal("docs-assistant", asMaf.Name); // before start: the slug
    }

    [Fact]
    public async Task A_maf_workflow_step_runs_a_fadenstack_agent()
    {
        var (agent, fake, _) = Make();
        fake.Answers.Enqueue(OpenAiFake.Text("From the workflow."));
        var workflow = AgentWorkflowBuilder.BuildSequential(agent);

        await using var run = await InProcessExecution.RunStreamingAsync(workflow, new List<ChatMessage> { new(ChatRole.User, "Hello") });
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var text = "";
        object? output = null;
        await foreach (var e in run.WatchStreamAsync())
        {
            if (e is AgentResponseUpdateEvent update)
            {
                text += update.Update.Text;
            }
            else if (e is WorkflowOutputEvent done)
            {
                output = done.Data;
                break;
            }
            else if (e is WorkflowErrorEvent or ExecutorFailedEvent)
            {
                Assert.Fail("The workflow failed: " + e);
            }
        }

        Assert.Equal("From the workflow.", text);
        Assert.NotNull(output);
        Assert.Single(fake.Requests);
    }

    // ---- I02 commit 12: the context provider adapter ----

    [Fact]
    public async Task The_adapter_calls_a_provider_before_and_after_a_turn()
    {
        var (agent, _, _) = Make();
        var session = await agent.CreateSessionAsync();
        var provider = new StubProvider();
        var adapter = new ContextProviderAdapter(provider);

        var context = await adapter.BeforeTurnAsync(agent, session, new AIContext { Instructions = "Ours." }, default);
        await adapter.AfterTurnAsync(agent, session, [new ChatMessage(ChatRole.User, "Hi")], [new ChatMessage(ChatRole.Assistant, "Hello")],
            null, default);
        await adapter.AfterTurnAsync(agent, session, [new ChatMessage(ChatRole.User, "Hi")], null, new InvalidOperationException("x"), default);

        Assert.Contains("Ours.", context.Instructions);
        Assert.Contains("From the provider.", context.Instructions);
        Assert.Same(agent, provider.Agent);
        Assert.Equal(new[] { "Hello" }, provider.Answers);
        Assert.Equal(1, provider.Failures);
    }

    private sealed class StubProvider : AIContextProvider
    {
        public AIAgent? Agent { get; private set; }
        public List<string> Answers { get; } = new();
        public int Failures { get; private set; }

        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken)
        {
            Agent = context.Agent;
            return new(new AIContext { Instructions = "From the provider." });
        }

        // InvokedCoreAsync sees every turn, a failed one too (MAF stores context only for a turn that succeeded).
        protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken)
        {
            if (context.InvokeException is not null)
            {
                Failures++;
            }
            else
            {
                Answers.AddRange(context.ResponseMessages!.Select(m => m.Text));
            }
            return default;
        }
    }
}
