using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Models;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// Host tools started from the chat (a button, a module's view): shown as a turn with a label, approved on the usual cards,
/// run without a model request, and never edited or sent again as text. Ported from the TypeScript chat's
/// <c>commands.test.ts</c>.
/// </summary>
public class ChatCommandTests
{
    private sealed class Rig
    {
        public Gateway Gateway = null!;
        public AgentChatViewModel Chat = null!;
        public FadenstackAgent Agent = null!;
        public PageTools Tools = null!;
    }

    private static async Task<Rig> Fixture(Action<AgentChatOptions>? configure = null)
    {
        var rig = new Rig { Gateway = new Gateway().UseHostChecks(), Tools = new PageTools() };
        var broker = new ApprovalBroker();
        rig.Agent = rig.Gateway.Builder().AddTools(rig.Tools).UseApprovals(broker).Build();
        var options = new AgentChatOptions { RenderInterval = TimeSpan.Zero };
        configure?.Invoke(options);
        rig.Chat = new AgentChatViewModel(rig.Agent, broker, InlineDispatcher.Instance, options);
        await rig.Chat.InitializeAsync();
        rig.Chat.SelectedToolMode = rig.Chat.ToolModes.First(m => m.Mode == Tools.ToolMode.Auto);
        return rig;
    }

    private static async Task<ApprovalPart> Pending(AgentChatViewModel chat)
    {
        for (var i = 0; i < 500; i++)
        {
            var card = chat.Turns.ToArray().SelectMany(t => t.Parts.ToArray()).OfType<ApprovalPart>().FirstOrDefault(p => p.AllowCommand.CanExecute(null));
            if (card is not null)
            {
                return card;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("No approval card.");
    }

    private static Dictionary<string, object?> Page(int page) => new() { ["page"] = page };

    [Fact]
    public async Task A_label_is_kept_across_reopening_without_changing_the_policy_arguments_or_the_model_history()
    {
        var rig = await Fixture();
        rig.Gateway.Says("Seen.");

        var running = rig.Chat.InvokeToolAsync("delete_page", Page(7), new ChatCommandOptions { Label = "Undo file change", Origin = HostToolOrigin.UserHistory });
        (await Pending(rig.Chat)).AllowCommand.Execute(null);
        await running;

        Assert.Equal("Undo file change", rig.Chat.Turns[0].UserText);
        var first = rig.Gateway.Checks[0].Json;
        Assert.Equal(("delete_page", 7), (first.GetProperty("name").GetString(), first.GetProperty("arguments").GetProperty("page").GetInt32()));
        var id = rig.Chat.CurrentSessionId!;
        rig.Chat.NewChatCommand.Execute(null);
        await rig.Chat.OpenSessionAsync(id);
        var reopened = rig.Chat.Turns[0];
        Assert.Equal(("Undo file change", true), (reopened.UserText, reopened.IsCommand));
        Assert.False(reopened.EditCommand.CanExecute(null));
        Assert.False(reopened.RetryCommand.CanExecute(null));
        await rig.Chat.SendAsync("A separate message");
        Assert.DoesNotContain("Undo file change", rig.Gateway.ChatRequests.Single().Body);
        rig.Chat.Dispose();
        rig.Agent.Dispose();
    }

    [Fact]
    public async Task It_uses_the_shared_approval_and_stop_state_and_leaves_the_draft_and_attachments_alone()
    {
        var beforeSend = 0;
        OutgoingCommand? before = null;
        OutgoingCommand? after = null;
        var rig = await Fixture(o =>
        {
            o.BeforeSend = (_, _) => { beforeSend++; return Task.CompletedTask; };
            o.BeforeCommand = (command, _) => { before = command; return Task.CompletedTask; };
            o.AfterCommand = command => after = command;
        });
        rig.Chat.Input = "Unsent draft";
        var attachment = AttachmentViewModel.FromContent(new MessageAttachment { Title = "Unsent source", Content = "Not sent to a command", Kind = "document" });
        rig.Chat.Attachments.Add(attachment);

        var running = rig.Chat.InvokeToolAsync("delete_page", Page(7), new ChatCommandOptions { Origin = HostToolOrigin.UserHistory });
        var card = await Pending(rig.Chat);
        Assert.True(rig.Chat.IsBusy);
        Assert.Empty(rig.Tools.Ran);
        card.AllowCommand.Execute(null);
        var result = await running;

        Assert.Equal("ok", result!.Outcome);
        Assert.Equal((false, "Unsent draft"), (rig.Chat.IsBusy, rig.Chat.Input));
        Assert.Same(attachment, Assert.Single(rig.Chat.Attachments));
        Assert.Empty(rig.Gateway.ChatRequests);
        Assert.Equal(0, beforeSend);
        Assert.Equal(("delete_page", HostToolOrigin.UserHistory, (string?)null), (before!.Name, before.Origin, before.Label));
        Assert.Same(before, after);
        Assert.Equal(2, rig.Gateway.Checks.Count);
        var turn = Assert.Single(rig.Chat.Turns);
        Assert.Equal(("delete_page", true, false, false), (turn.UserText, turn.IsCommand, turn.EditCommand.CanExecute(null), turn.RetryCommand.CanExecute(null)));
        Assert.Equal(TurnState.Completed, turn.State);

        var id = rig.Chat.CurrentSessionId!;
        rig.Chat.NewChatCommand.Execute(null);
        await rig.Chat.OpenSessionAsync(id);
        Assert.Equal(("delete_page", true), (rig.Chat.Turns[0].UserText, rig.Chat.Turns[0].IsCommand));
        Assert.Empty(rig.Gateway.ChatRequests);
        rig.Gateway.Says("Answer.");
        await rig.Chat.SendAsync("A separate user message");
        var sent = rig.Gateway.ChatRequests.Single().Json.GetProperty("messages").EnumerateArray().ToList();
        Assert.DoesNotContain(sent, m => m.GetProperty("role").GetString() == "tool");
        rig.Chat.Dispose();
        rig.Agent.Dispose();
    }

    [Fact]
    public async Task Stop_during_approval_cancels_the_command_and_a_simultaneous_send_cannot_start_a_model_turn()
    {
        var rig = await Fixture();

        var running = rig.Chat.InvokeToolAsync("delete_page", Page(7));
        await Pending(rig.Chat);
        await rig.Chat.SendAsync("Must not dispatch while busy");
        rig.Chat.StopCommand.Execute(null);
        await running;

        var turn = Assert.Single(rig.Chat.Turns);
        Assert.Equal((TurnState.Stopped, false, false), (turn.State, turn.EditCommand.CanExecute(null), turn.RetryCommand.CanExecute(null)));
        Assert.False(rig.Chat.IsBusy);
        Assert.Empty(rig.Tools.Ran);
        Assert.Empty(rig.Gateway.ChatRequests);
        rig.Chat.Dispose();
        rig.Agent.Dispose();
    }

    [Fact]
    public async Task A_failed_preparation_keeps_the_draft_and_never_reaches_the_policy_or_the_tool()
    {
        var afterCommands = 0;
        var rig = await Fixture(o =>
        {
            o.BeforeCommand = (_, _) => throw new InvalidOperationException("Native editor is dirty");
            o.AfterCommand = _ => afterCommands++;
        });
        rig.Chat.Input = "Keep this";

        var result = await rig.Chat.InvokeToolAsync("delete_page", Page(7));

        Assert.Null(result);
        Assert.Equal(("Keep this", "Native editor is dirty", false), (rig.Chat.Input, rig.Chat.Error, rig.Chat.IsBusy));
        Assert.Empty(rig.Chat.Turns);
        Assert.Empty(rig.Gateway.Checks);
        Assert.Empty(rig.Tools.Ran);
        Assert.Equal(1, afterCommands);
        rig.Chat.Dispose();
        rig.Agent.Dispose();
    }

    [Fact]
    public async Task The_arguments_are_copied_before_the_host_is_awaited_and_the_original_payload_runs()
    {
        var prepared = new TaskCompletionSource();
        var rig = await Fixture(o => o.BeforeCommand = (_, _) => prepared.Task);
        var arguments = Page(7);

        var running = rig.Chat.InvokeToolAsync("delete_page", arguments);
        arguments["page"] = 9;
        prepared.SetResult();
        (await Pending(rig.Chat)).AllowCommand.Execute(null);
        await running;

        Assert.Equal(new[] { "delete_page 7" }, rig.Tools.Ran);
        Assert.Empty(rig.Gateway.ChatRequests);
        rig.Chat.Dispose();
        rig.Agent.Dispose();
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("  ")]
    public async Task A_label_that_is_not_one_line_is_refused_before_the_chat_changes(string label)
    {
        var rig = await Fixture();

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Chat.InvokeToolAsync("delete_page", Page(7), new ChatCommandOptions { Label = label }));

        Assert.Empty(rig.Chat.Turns);
        Assert.False(rig.Chat.IsBusy);
        Assert.Empty(rig.Gateway.Checks);
        rig.Chat.Dispose();
        rig.Agent.Dispose();
    }
}
