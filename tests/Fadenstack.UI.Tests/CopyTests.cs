using System.Text;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>Copying an answer in the chat: marked through the agent where it marks, never copied when the policy refuses.</summary>
public class CopyTests
{
    /// <summary>A marker as the provenance module is for the chat: it wraps the HTML, or refuses.</summary>
    private sealed class FakeMarker : IAnswerCopyMarker
    {
        public bool Refuse { get; init; }

        public List<AnswerCopy> Seen { get; } = new();

        public Task<MarkedCopy> MarkAsync(AnswerCopy copy, CancellationToken cancellationToken = default)
        {
            Seen.Add(copy);
            if (Refuse)
            {
                return Task.FromResult(new MarkedCopy(copy.Text, null, null, false) { Refused = true });
            }
            return Task.FromResult(copy.IsCode
                ? new MarkedCopy(copy.Text, copy.Html, null, false) { ExemptBecause = "code" }
                : new MarkedCopy(copy.Text, "<span data-ai-generated=\"r-1\">" + copy.Html + "</span>", "r-1", true));
        }
    }

    private static async Task<(AgentChatViewModel Chat, NoClipboard Clipboard, FakeMarker Marker)> AnsweredAsync(bool refuse = false, bool withMarker = true)
    {
        var marker = new FakeMarker { Refuse = refuse };
        var gateway = new Gateway().Then(Gateway.Text("Hello **Jane**"), Gateway.Finish("stop"), FakeServer.Done);
        var broker = new ApprovalBroker();
        var builder = gateway.Builder().UseApprovals(broker);
        if (withMarker)
        {
            builder.UseAnswerCopyMarker(_ => marker);
        }
        var clipboard = new NoClipboard();
        var chat = new AgentChatViewModel(builder.Build(), broker, InlineDispatcher.Instance,
            new AgentChatOptions { RenderInterval = TimeSpan.Zero, Clipboard = clipboard });
        await chat.InitializeAsync();
        await chat.SendAsync("Greet Jane.");
        return (chat, clipboard, marker);
    }

    [Fact]
    public async Task A_copy_puts_the_text_and_the_marked_html_on_the_clipboard_and_says_copied()
    {
        var (chat, clipboard, marker) = await AnsweredAsync();
        var turn = chat.Turns.Single();

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)turn.CopyCommand).ExecuteAsync(null);

        Assert.Equal("Hello **Jane**", clipboard.LastText); // the plain text is as it was
        Assert.Equal("<span data-ai-generated=\"r-1\"><p>Hello <strong>Jane</strong></p></span>", clipboard.LastHtml);
        Assert.Equal(("Hello **Jane**", turn.Id, chat.CurrentSessionId), (marker.Seen.Single().Text, marker.Seen.Single().Turn, marker.Seen.Single().Chat));
        Assert.Equal("Copied", turn.CopyStatus);
        Assert.True(turn.HasCopyStatus);
    }

    [Fact]
    public async Task Without_a_marker_the_copy_is_the_text_and_its_html_as_before()
    {
        var (chat, clipboard, _) = await AnsweredAsync(withMarker: false);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)chat.Turns.Single().CopyCommand).ExecuteAsync(null);

        Assert.Equal(("Hello **Jane**", "<p>Hello <strong>Jane</strong></p>"), (clipboard.LastText, clipboard.LastHtml));
    }

    [Fact]
    public async Task When_the_policy_refuses_nothing_is_copied_and_the_person_is_told()
    {
        var (chat, clipboard, _) = await AnsweredAsync(refuse: true);
        var turn = chat.Turns.Single();

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)turn.CopyCommand).ExecuteAsync(null);

        Assert.Null(clipboard.LastText);
        Assert.Equal("Not copied: this answer could not be marked as AI-generated, and your organisation requires it.", turn.CopyStatus);
    }

    [Fact]
    public async Task A_code_block_is_copied_as_it_is_and_goes_through_the_marker_as_code()
    {
        var gateway = new Gateway().Then(Gateway.Text("```\nvar x = 1;\n```"), Gateway.Finish("stop"), FakeServer.Done);
        var marker = new FakeMarker();
        var broker = new ApprovalBroker();
        var clipboard = new NoClipboard();
        var chat = new AgentChatViewModel(gateway.Builder().UseApprovals(broker).UseAnswerCopyMarker(_ => marker).Build(), broker, InlineDispatcher.Instance,
            new AgentChatOptions { RenderInterval = TimeSpan.Zero, Clipboard = clipboard });
        await chat.InitializeAsync();
        await chat.SendAsync("Code.");
        var part = chat.Turns.Single().Parts.OfType<Fadenstack.UI.Parts.MarkdownPart>().Single();

        part.CopyCodeCommand.Execute(part.Blocks.OfType<Fadenstack.UI.Markdown.CodeBlock>().Single());

        Assert.True(marker.Seen.Single().IsCode);
        Assert.Equal("var x = 1;", clipboard.LastText);
        Assert.Null(clipboard.LastHtml);
    }

    [Fact]
    public void The_cf_html_header_has_correct_offsets_and_reads_back_the_fragment()
    {
        var fragment = "<p>Grüße 你好 <b>there</b></p>";

        var cf = ClipboardHtml.ToCfHtml(fragment);

        var bytes = Encoding.UTF8.GetBytes(cf);
        int Header(string name) => int.Parse(cf.Substring(cf.IndexOf(name + ":", StringComparison.Ordinal) + name.Length + 1, 10));
        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes, Header("StartFragment"), Header("EndFragment") - Header("StartFragment")));
        Assert.StartsWith("<html>", Encoding.UTF8.GetString(bytes, Header("StartHTML"), 6));
        Assert.Equal(bytes.Length, Header("EndHTML"));
        Assert.Equal(fragment, ClipboardHtml.FromCfHtml(cf));
        Assert.StartsWith("Version:0.9\r\nStartHTML:0000000", cf);
        Assert.Contains("<!--StartFragment-->" + fragment + "<!--EndFragment-->", cf);
    }
}
