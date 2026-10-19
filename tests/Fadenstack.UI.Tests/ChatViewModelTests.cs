using System.Net;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Tools;
using Fadenstack.UI.Markdown;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The chat view model against a scripted gateway, with no UI thread (inline dispatch).</summary>
public class ChatViewModelTests
{
    private static (AgentChatViewModel Chat, ApprovalBroker Broker, FadenstackAgent Agent) Chat(Gateway gateway, ISessionStore? store = null,
        Action<AgentChatOptions>? configure = null)
    {
        var broker = new ApprovalBroker();
        var agent = gateway.Builder(store).AddTools(new PageTools()).UseApprovals(broker).Build();
        var options = new AgentChatOptions { RenderInterval = TimeSpan.Zero };
        configure?.Invoke(options);
        return (new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, options), broker, agent);
    }

    public static string Stream(params string[] chunks) => string.Concat(chunks.Select(Gateway.Text));

    /// <summary>Waits for a part to show up while the turn runs on another thread.</summary>
    private static async Task<T> WaitFor<T>(TurnViewModel turn) where T : ResponsePart
    {
        for (var i = 0; i < 500; i++)
        {
            try
            {
                if (turn.Parts.ToArray().OfType<T>().FirstOrDefault() is { } part)
                {
                    return part;
                }
            }
            catch (InvalidOperationException)
            {
                // changed while read
            }
            await Task.Delay(10);
        }
        throw new TimeoutException(typeof(T).Name);
    }

    private static async Task<TurnViewModel> TurnOf(AgentChatViewModel chat)
    {
        for (var i = 0; i < 500 && chat.Turns.Count == 0; i++)
        {
            await Task.Delay(10);
        }
        return chat.Turns.Last();
    }

    [Fact]
    public async Task It_starts_from_the_agents_profile_and_streams_an_answer_as_markdown()
    {
        var gateway = new Gateway().Then(Stream("## Page 7\n\nIt holds ", "**3 terminals**", " and `K7`."), Gateway.Finish("stop"), FakeServer.Done);
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();
        Assert.True(chat.IsReady);
        Assert.Equal(("Docs", "Ask Docs anything."), (chat.Title, chat.Greeting));
        Assert.Equal(ToolMode.Ask, chat.SelectedToolMode.Mode);
        Assert.True(chat.IsEmpty);

        chat.Input = "What is on page 7?";
        Assert.True(chat.SendCommand.CanExecute(null));
        await chat.SendCommand.ExecuteAsync(null);

        Assert.Equal("", chat.Input);
        Assert.False(chat.IsBusy);
        var turn = Assert.Single(chat.Turns);
        Assert.Equal(("What is on page 7?", TurnState.Completed, ChatOutcome.Completed), (turn.UserText, turn.State, turn.Outcome));
        var text = Assert.IsType<MarkdownPart>(Assert.Single(turn.Parts));
        Assert.False(text.IsStreaming);
        Assert.IsType<HeadingBlock>(text.Blocks[0]);
        Assert.Contains(Assert.IsType<ParagraphBlock>(text.Blocks[1]).Inlines, r => r.Text == "3 terminals" && r.Style == InlineStyle.Bold);
        Assert.Equal(chat.CurrentSessionId, Assert.Single(chat.Sessions).Id);
        Assert.True(chat.Sessions[0].IsCurrent);
        turn.CopyCommand.Execute(null);
        Assert.Equal("## Page 7\n\nIt holds **3 terminals** and `K7`.", ((NoClipboard)NoClipboard.Instance).LastText);
    }

    [Fact]
    public async Task A_tool_that_asks_shows_a_card_after_its_line_and_the_click_answers_it()
    {
        for (var run = 0; run < 5; run++) // the event and the broker race; the card must land in order every time
        {
            var gateway = new Gateway().Calls(("call-1", "add_comment", """{"page":2,"text":"ok"}""")).Says("Added.");
            var (chat, _, _) = Chat(gateway);
            await chat.InitializeAsync();
            var sending = chat.SendAsync("Comment page 2.");
            var turn = await TurnOf(chat);
            var card = await WaitFor<ApprovalPart>(turn);
            Assert.Equal(("Run add_comment?", "Changes data", TurnState.WaitingForApproval), (card.Title, card.ClassLabel, turn.State));
            Assert.Contains("page: 2", card.ArgumentsText);
            Assert.Equal(ToolCallState.WaitingForApproval, turn.Parts.OfType<ToolCallPart>().Single().State);
            Assert.Equal("", turn.StatusText); // the card and the tool's row say it, once
            card.AllowCommand.Execute(null);
            Assert.False(card.AllowCommand.CanExecute(null));
            await sending;

            // Asked, allowed, ran: the card sits above its tool's row.
            Assert.Equal(new[] { typeof(ApprovalPart), typeof(ToolCallPart), typeof(MarkdownPart) }, turn.Parts.Select(p => p.GetType()));
            var tool = turn.Parts.OfType<ToolCallPart>().Single();
            Assert.Equal((ToolCallState.Succeeded, "Ran add_comment", ToolClass.Write), (tool.State, tool.Title, tool.Class));
            Assert.Equal("", tool.ResultText); // not built until expanded
            tool.ToggleCommand.Execute(null);
            Assert.Equal("added", tool.ResultText);
            Assert.Contains("\"page\": 2", tool.ArgumentsText);
            Assert.Equal((ApprovalState.Approved, "Allowed"), (card.State, card.StateText));
            Assert.Equal(TurnState.Completed, turn.State);
        }
    }

    [Fact]
    public async Task Denied_the_tool_does_not_run_and_the_turn_says_so()
    {
        var gateway = new Gateway().Calls(("call-1", "delete_page", """{"page":2}""")).Says("I did not delete it.");
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();
        var sending = chat.SendAsync("Delete page 2.");
        var card = await WaitFor<ApprovalPart>(await TurnOf(chat));
        Assert.True(card.IsDestructive);
        card.DenyCommand.Execute(null);
        await sending;
        var turn = chat.Turns.Single();
        Assert.Equal((ToolCallState.Denied, ChatOutcome.ToolDenied), (turn.Parts.OfType<ToolCallPart>().Single().State, turn.Outcome));
        Assert.Equal(ApprovalState.Denied, card.State);
    }

    [Fact]
    public async Task For_this_chat_and_always_show_only_where_the_request_offers_them()
    {
        var gateway = new Gateway()
            .Calls(("call-1", "add_comment", """{"page":2,"text":"ok"}""")).Says("Added.")
            .Calls(("call-2", "delete_page", """{"page":2}""")).Says("Not deleted.");
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();

        var first = chat.SendAsync("Comment page 2.");
        var card = await WaitFor<ApprovalPart>(await TurnOf(chat));
        Assert.Equal((true, true), (card.CanAllowForSession, card.CanAllowAlways));
        Assert.True(card.AllowForSessionCommand.CanExecute(null));
        card.AllowAlwaysCommand.Execute(null);
        await first;
        Assert.Equal((ApprovalState.ApprovedAlways, "Allowed always"), (card.State, card.StateText));
        Assert.False(card.AllowAlwaysCommand.CanExecute(null));

        var second = chat.SendAsync("Delete page 2.");
        var destructive = await WaitFor<ApprovalPart>(chat.Turns.Last());
        Assert.Equal((false, false), (destructive.CanAllowForSession, destructive.CanAllowAlways));
        Assert.False(destructive.AllowForSessionCommand.CanExecute(null));
        Assert.False(destructive.AllowAlwaysCommand.CanExecute(null));
        Assert.True(destructive.AllowCommand.CanExecute(null));
        destructive.DenyCommand.Execute(null);
        await second;
    }

    [Fact]
    public async Task A_reopened_session_shows_allowed_always_and_no_card_for_a_call_let_through()
    {
        var store = new MemorySessionStore();
        var gateway = new Gateway()
            .Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("Added.")
            .Calls(("call-2", "add_comment", """{"page":2,"text":"b"}""")).Says("Added again.");
        var (chat, _, _) = Chat(gateway, store);
        await chat.InitializeAsync();
        var first = chat.SendAsync("Comment page 1.");
        (await WaitFor<ApprovalPart>(await TurnOf(chat))).AllowAlwaysCommand.Execute(null);
        await first;
        await chat.SendAsync("Comment page 2."); // allowed always: no card
        Assert.Empty(chat.Turns[1].Parts.OfType<ApprovalPart>());

        var (again, _, _) = Chat(new Gateway(), store);
        await again.InitializeAsync();
        await again.OpenSessionAsync(chat.CurrentSessionId!);
        var card = again.Turns[0].Parts.OfType<ApprovalPart>().Single();
        Assert.Equal((ApprovalState.ApprovedAlways, "Allowed always"), (card.State, card.StateText));
        Assert.Empty(again.Turns[1].Parts.OfType<ApprovalPart>());
        Assert.Equal(ToolCallState.Succeeded, again.Turns[1].Parts.OfType<ToolCallPart>().Single().State);
    }

    [Fact]
    public async Task Reset_approvals_makes_the_next_call_ask_again()
    {
        var gateway = new Gateway()
            .Calls(("call-1", "add_comment", """{"page":1,"text":"a"}""")).Says("Added.")
            .Calls(("call-2", "add_comment", """{"page":2,"text":"b"}""")).Says("Added.")
            .Calls(("call-3", "add_comment", """{"page":3,"text":"c"}""")).Says("Added.");
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();
        var first = chat.SendAsync("Comment page 1.");
        (await WaitFor<ApprovalPart>(await TurnOf(chat))).AllowAlwaysCommand.Execute(null);
        await first;
        await chat.SendAsync("Comment page 2.");
        Assert.Empty(chat.Turns[1].Parts.OfType<ApprovalPart>());

        chat.IsModeMenuOpen = true;
        Assert.True(chat.ResetApprovalsCommand.CanExecute(null));
        await chat.ResetApprovalsCommand.ExecuteAsync(null);
        Assert.False(chat.IsModeMenuOpen);

        var third = chat.SendAsync("Comment page 3.");
        (await WaitFor<ApprovalPart>(chat.Turns.Last())).AllowCommand.Execute(null);
        await third;
        Assert.Equal(ChatOutcome.Completed, chat.Turns[2].Outcome);
    }

    [Fact]
    public async Task Stop_ends_a_turn_that_waits_for_approval()
    {
        var gateway = new Gateway().Calls(("call-1", "stamp", "{}"));
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();
        var sending = chat.SendAsync("Stamp it.");
        var turn = await TurnOf(chat);
        var card = await WaitFor<ApprovalPart>(turn);
        Assert.True(chat.StopCommand.CanExecute(null));
        chat.StopCommand.Execute(null);
        await sending;
        Assert.Equal((TurnState.Stopped, ApprovalState.Expired), (turn.State, card.State));
        Assert.Equal(ToolCallState.Cancelled, turn.Parts.OfType<ToolCallPart>().Single().State);
        Assert.True(turn.RetryCommand.CanExecute(null));
        Assert.False(chat.IsBusy);
    }

    [Fact]
    public async Task A_refused_prompt_is_marked_and_can_be_edited_and_resent()
    {
        var gateway = new Gateway().Then(_ => FakeServer.Respond((HttpStatusCode)422, new
        {
            error = new { type = "governance_error", code = "policy_blocked", message = "Project names stay out." },
            faden = new { governance = new { outcome = "prompt_blocked", stage = "input", message = "Project names stay out.", decision_id = "d-1" } },
        }));
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();
        var focus = 0;
        chat.ComposerFocusRequested += (_, _) => focus++;
        await chat.SendAsync("Tell me about Project Falcon.");
        var turn = chat.Turns.Single();
        Assert.True(turn.IsPromptBlocked);
        var notice = Assert.IsType<NoticePart>(Assert.Single(turn.Parts));
        Assert.Equal((NoticeSeverity.Blocked, "Your message was not sent", "Project names stay out.", "Decision d-1"),
            (notice.Severity, notice.Title, notice.Message, notice.Detail));
        turn.EditCommand.Execute(null);
        Assert.Equal(("Tell me about Project Falcon.", 1), (chat.Input, focus));
    }

    [Fact]
    public async Task Tool_lines_and_text_interleave_in_the_order_they_happened()
    {
        var gateway = new Gateway()
            .Then(Gateway.Text("Let me look."), Gateway.ToolCalls(("call-1", "read_page", """{"page":1}""")), Gateway.Finish("tool_calls"), FakeServer.Done)
            .Then(Stream("Page 1 has ", "3 terminals."), Gateway.Finish("stop"), FakeServer.Done);
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();
        await chat.SendAsync("What is on page 1?");
        var parts = chat.Turns.Single().Parts;
        Assert.Equal(new[] { typeof(MarkdownPart), typeof(ToolCallPart), typeof(MarkdownPart) }, parts.Select(p => p.GetType()));
        Assert.Equal("Page 1 has 3 terminals.", ((MarkdownPart)parts[2]).Text);
    }

    [Fact]
    public async Task Gateway_events_become_citations_pii_notices_and_generic_notices()
    {
        var gateway = new Gateway().Then(
            Gateway.Custom("faden.pii", new { mode = "tokenize", entities = new { PERSON = 2, EMAIL_ADDRESS = 1 } }),
            FakeServer.Fadenstack(new { type = "TOOL_CALL_START", toolCallId = "gw-1", toolCallName = "knowledge_search", kind = "knowledge" }),
            FakeServer.Fadenstack(new { type = "TOOL_CALL_END", toolCallId = "gw-1", toolCallName = "knowledge_search", durationMs = 30, error = false }),
            Gateway.Custom("faden.citations", new { tool = "knowledge_search", sources = new object[] { new { source = "manual.pdf", chunk = 3, score = 0.8 }, new { source = "wiring.docx" } } }),
            Gateway.Custom("faden.tool_mode", new { mode = "ask", hidden = Array.Empty<string>() }),
            Gateway.Custom("faden.extension_status", new { message = "Something an extension says." }),
            Gateway.Text("Found it."), Gateway.Finish("stop"), FakeServer.Done);
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();
        await chat.SendAsync("Search.");
        var parts = chat.Turns.Single().Parts;
        Assert.Equal(new[] { typeof(NoticePart), typeof(ToolCallPart), typeof(CitationsPart), typeof(NoticePart), typeof(MarkdownPart) },
            parts.Select(p => p.GetType()));
        Assert.Equal("3 values kept from the model: 2 person, 1 email address", ((NoticePart)parts[0]).Message);
        Assert.Equal(("Ran knowledge_search", "gateway"), (((ToolCallPart)parts[1]).Title, ((ToolCallPart)parts[1]).Source));
        var citations = (CitationsPart)parts[2];
        Assert.Equal(("Used 2 references", "manual.pdf", "Part 3"), (citations.Title, citations.Citations[0].Title, citations.Citations[0].Detail));
        Assert.Equal("Something an extension says.", ((NoticePart)parts[3]).Message);
    }

    [Fact]
    public async Task An_answers_receipt_is_no_notice()
    {
        var gateway = new Gateway().Then(
            Gateway.Text("The reply."), Gateway.Finish("stop"),
            Gateway.Custom("faden.provenance", new { status = "signed", receipt = new { id = "r-1", kind = "text" } }),
            FakeServer.Done);
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();
        await chat.SendAsync("Write the reply.");
        Assert.Equal(new[] { typeof(MarkdownPart) }, chat.Turns.Single().Parts.Select(p => p.GetType()));
    }

    [Fact]
    public async Task A_ceiling_greys_out_the_wider_modes_and_the_pick_goes_to_the_gateway()
    {
        var gateway = new Gateway(ceiling: "\"read_only\"").Says("Hi.");
        var (chat, _, _) = Chat(gateway);
        await chat.InitializeAsync();
        Assert.Equal(new[] { true, true, false, false }, chat.ToolModes.Select(o => o.IsAvailable));
        Assert.Equal(ToolMode.ReadOnly, chat.SelectedToolMode.Mode);
        chat.SelectedToolMode = chat.ToolModes[3]; // Auto: not available
        Assert.Equal(ToolMode.ReadOnly, chat.SelectedToolMode.Mode);
        chat.SelectedToolMode = chat.ToolModes[0];
        await chat.SendAsync("Hi");
        Assert.Equal("off", gateway.ChatRequests[0].Header("X-Faden-Tool-Mode"));
    }

    [Fact]
    public async Task A_long_session_opens_with_its_latest_turns_and_loads_older_ones_on_request()
    {
        var store = new MemorySessionStore();
        var gateway = new Gateway();
        for (var i = 1; i <= 75; i++)
        {
            gateway.Says($"Answer {i}.");
        }
        var (writer, _, _) = Chat(gateway, store);
        await writer.InitializeAsync();
        for (var i = 1; i <= 75; i++)
        {
            await writer.SendAsync($"Question {i}");
        }
        var id = writer.CurrentSessionId!;

        var (reader, _, _) = Chat(new Gateway(), store, o => o.InitialTurns = 30);
        await reader.InitializeAsync();
        Assert.Equal(id, Assert.Single(reader.Sessions).Id);
        await reader.OpenSessionCommand.ExecuteAsync(reader.Sessions[0]);
        Assert.Equal(30, reader.Turns.Count);
        Assert.Equal(("Question 46", "Answer 75."), (reader.Turns[0].UserText, ((MarkdownPart)reader.Turns[29].Parts.Single()).Text));
        Assert.True(reader.HasEarlierTurns);
        await reader.LoadEarlierCommand.ExecuteAsync(null);
        await reader.LoadEarlierCommand.ExecuteAsync(null);
        Assert.Equal(75, reader.Turns.Count);
        Assert.Equal(Enumerable.Range(1, 75).Select(i => $"Question {i}"), reader.Turns.Select(t => t.UserText));
        Assert.False(reader.HasEarlierTurns);
        Assert.All(reader.Turns, t => Assert.Equal(TurnState.Completed, t.State));
    }

    [Fact]
    public async Task A_reopened_session_shows_its_tools_approvals_and_notices()
    {
        var store = new MemorySessionStore();
        var gateway = new Gateway()
            .Calls(("call-1", "delete_page", """{"page":2}""")).Says("Not deleted.")
            .Calls(("call-2", "read_page", """{"page":1}""")).Says("Page 1.");
        var (chat, _, _) = Chat(gateway, store);
        await chat.InitializeAsync();
        var first = chat.SendAsync("Delete page 2.");
        (await WaitFor<ApprovalPart>(await TurnOf(chat))).DenyCommand.Execute(null);
        await first;
        await chat.SendAsync("Read page 1.");

        var (again, _, _) = Chat(new Gateway(), store);
        await again.InitializeAsync();
        await again.OpenSessionAsync(chat.CurrentSessionId!);
        Assert.Equal(new[] { typeof(ApprovalPart), typeof(ToolCallPart), typeof(MarkdownPart) }, again.Turns[0].Parts.Select(p => p.GetType()));
        Assert.Equal((ToolCallState.Denied, ToolClass.Destructive), (again.Turns[0].Parts.OfType<ToolCallPart>().Single().State,
            again.Turns[0].Parts.OfType<ToolCallPart>().Single().Class));
        Assert.Equal(ApprovalState.Denied, again.Turns[0].Parts.OfType<ApprovalPart>().Single().State);
        Assert.Equal(ChatOutcome.ToolDenied, again.Turns[0].Outcome);
        Assert.Equal(ToolCallState.Succeeded, again.Turns[1].Parts.OfType<ToolCallPart>().Single().State);
    }

    [Fact]
    public async Task A_fast_stream_reaches_the_ui_thread_in_a_few_batches()
    {
        var chunks = Enumerable.Range(0, 400).Select(i => $"w{i} ").ToArray();
        var gateway = new Gateway().Then(Stream(chunks), Gateway.Finish("stop"), FakeServer.Done);
        var dispatcher = new CountingDispatcher();
        var broker = new ApprovalBroker();
        var agent = gateway.Builder().UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, dispatcher, new AgentChatOptions { RenderInterval = TimeSpan.FromMilliseconds(30) });
        await chat.InitializeAsync();
        await chat.SendAsync("Go");
        Assert.Equal(string.Concat(chunks), ((MarkdownPart)chat.Turns.Single().Parts.Single()).Text);
        Assert.True(dispatcher.Posts < 40, $"{dispatcher.Posts} posts for 400 tokens");
    }

    [Fact]
    public async Task Without_a_sign_in_the_chat_says_so()
    {
        var gateway = new Gateway();
        var broker = new ApprovalBroker();
        var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://faden.test"), new Fadenstack.Auth.AgentTokenProvider(new Uri("https://faden.test"), Gateway.Slug,
                new Fadenstack.Auth.MemoryTokenStore(), new HttpClient(gateway.Server)), o => o.HttpClient = new HttpClient(gateway.Server))
            .UseAgent(Gateway.Slug).UseSessionStore(new MemorySessionStore()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance);
        await chat.InitializeAsync();
        Assert.True(chat.SignInRequired);
        Assert.False(chat.IsReady);
        Assert.False(chat.SendCommand.CanExecute(null));
    }

    private sealed class CountingDispatcher : IUiDispatcher
    {
        private int _posts;

        public int Posts => _posts;

        public bool CheckAccess() => true;

        public void Post(Action action)
        {
            Interlocked.Increment(ref _posts);
            action();
        }
    }
}

public class BeforeSendTests
{
    private static async Task<(AgentChatViewModel Chat, Gateway Gateway)> ChatAsync(Gateway gateway, Action<AgentChatOptions> configure)
    {
        var broker = new ApprovalBroker();
        var agent = gateway.Builder(new MemorySessionStore()).UseApprovals(broker).Build();
        var options = new AgentChatOptions { RenderInterval = TimeSpan.Zero };
        configure(options);
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, options);
        await chat.InitializeAsync();
        return (chat, gateway);
    }

    [Fact]
    public async Task It_runs_around_every_send_and_what_it_attaches_goes_with_the_message()
    {
        var log = new List<string>();
        AgentChatViewModel chat = null!;
        (chat, var gateway) = await ChatAsync(new Gateway().Says("Ok.").Says("Ok again.").Says("Once more."), o =>
        {
            o.BeforeSend = async (message, _) =>
            {
                log.Add($"before {message.Text} busy={chat.IsBusy}");
                await Task.Yield();
                if (message.Text == "Explain.")
                {
                    chat.Attachments.Add(AttachmentViewModel.FromContent(new MessageAttachment { Title = "Sheet2", Content = "whole sheet", Kind = "sheet" }));
                }
            };
            o.AfterSend = message => log.Add($"after {message.Text}");
        });

        chat.Input = "Explain.";
        await chat.SendCommand.ExecuteAsync(null);
        await chat.SendSuggestionCommand.ExecuteAsync("Summarise.");
        await chat.SendAsync("Again.");

        Assert.Equal(new[]
        {
            "before Explain. busy=True", "after Explain.",
            "before Summarise. busy=True", "after Summarise.",
            "before Again. busy=True", "after Again.",
        }, log);
        Assert.Equal(new[] { "Explain.", "Summarise.", "Again." }, chat.Turns.Select(t => t.UserText));
        Assert.Equal(new[] { "Sheet2" }, chat.Turns[0].Attachments.Select(a => a.Title));
        Assert.Contains("whole sheet", gateway.ChatRequests[0].Json.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("", chat.Input);
    }

    [Fact]
    public async Task When_it_throws_nothing_is_sent_the_text_stays_and_the_next_message_clears_the_error()
    {
        var refuse = true;
        var after = new List<string>();
        var (chat, gateway) = await ChatAsync(new Gateway().Says("Ok."), o =>
        {
            o.BeforeSend = (_, _) => refuse ? throw new InvalidOperationException("Select a file first.") : Task.CompletedTask;
            o.AfterSend = message => after.Add(message.Text);
        });

        chat.Input = "Summarise the file.";
        await chat.SendCommand.ExecuteAsync(null);
        Assert.Equal(("Select a file first.", "Summarise the file.", false, true), (chat.Error, chat.Input, chat.IsBusy, chat.IsEmpty));
        Assert.Empty(gateway.ChatRequests);

        refuse = false;
        await chat.SendCommand.ExecuteAsync(null);
        Assert.Null(chat.Error);
        Assert.Single(chat.Turns);
        Assert.Equal(new[] { "Summarise the file.", "Summarise the file." }, after);
    }

    [Fact]
    public async Task Stop_while_the_host_prepares_sends_nothing_and_shows_no_error()
    {
        var started = new TaskCompletionSource();
        var after = 0;
        var (chat, gateway) = await ChatAsync(new Gateway().Says("Ok."), o =>
        {
            o.BeforeSend = async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            };
            o.AfterSend = _ => after++;
        });

        var sending = chat.SendAsync("Go.");
        await started.Task;
        Assert.True(chat.IsBusy);
        Assert.True(chat.StopCommand.CanExecute(null));
        chat.StopCommand.Execute(null);
        await sending;

        Assert.Equal((false, (string?)null, true, 1), (chat.IsBusy, chat.Error, chat.IsEmpty, after));
        Assert.Empty(gateway.ChatRequests);
    }
}

public class AttachmentTrayTests
{
    [Fact]
    public async Task Attached_context_is_read_when_sent_explicit_chips_go_and_the_implicit_one_stays()
    {
        var gateway = new Gateway().Says("Ok.").Says("Ok again.");
        var broker = new ApprovalBroker();
        using var agent = gateway.Builder(new MemorySessionStore()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions { RenderInterval = TimeSpan.Zero });
        await chat.InitializeAsync();
        var selection = "A1:B2";
        var reads = 0;
        var implicitChip = new AttachmentViewModel("Selection", "range", _ =>
        {
            reads++;
            return Task.FromResult<MessageAttachment?>(new MessageAttachment { Title = selection, Content = $"cells {selection}", Kind = "selection" });
        }, isImplicit: true);
        chat.Attachments.Add(implicitChip);
        chat.Attachments.Add(AttachmentViewModel.FromContent(new MessageAttachment { Title = "Sheet2", Content = "whole sheet", Kind = "sheet" }));
        Assert.Equal(0, reads);

        selection = "C3:D4"; // what is selected when the message goes is what goes
        await chat.SendAsync("Explain.");
        Assert.Equal(1, reads);
        var sent = gateway.ChatRequests[0].Json.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("cells C3:D4", sent);
        Assert.Contains("whole sheet", sent);
        Assert.Equal(new[] { "Selection", "Sheet2" }, chat.Turns[0].Attachments.Select(a => a.Title));
        Assert.Same(implicitChip, Assert.Single(chat.Attachments));

        implicitChip.ToggleCommand.Execute(null); // switched off: not sent
        await chat.SendAsync("And now?");
        Assert.Equal(1, reads);
        Assert.Empty(chat.Turns[1].Attachments);

        var (again, _, _) = (new AgentChatViewModel(agent, broker, InlineDispatcher.Instance), 0, 0);
        await again.InitializeAsync();
        await again.OpenSessionAsync(chat.CurrentSessionId!);
        Assert.Equal(new[] { "C3:D4", "Sheet2" }, again.Turns[0].Attachments.Select(a => a.Title));
    }

    [Fact]
    public async Task Context_that_cannot_be_read_is_a_notice_and_the_message_still_goes()
    {
        var gateway = new Gateway().Says("Ok.");
        var broker = new ApprovalBroker();
        using var agent = gateway.Builder().UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions { RenderInterval = TimeSpan.Zero });
        await chat.InitializeAsync();
        chat.Attachments.Add(new AttachmentViewModel("Selection", null, _ => throw new InvalidOperationException("Excel is busy.")));
        await chat.SendAsync("Go");
        var notice = chat.Turns[0].Parts.OfType<NoticePart>().Single();
        Assert.Equal(("Selection was not attached", "Excel is busy."), (notice.Title, notice.Message));
        Assert.Equal(TurnState.Completed, chat.Turns[0].State);
    }
}
