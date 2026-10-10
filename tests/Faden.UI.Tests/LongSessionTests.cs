using System.Diagnostics;
using System.Text.Json;
using Faden.Agents;
using Faden.Agents.Sessions;
using Faden.Agents.Tests;
using Faden.Auth;
using Faden.Governance;
using Faden.Samples.Wpf;
using Faden.UI.Parts;
using Faden.UI.Services;
using Faden.UI.ViewModels;
using Xunit;

namespace Faden.UI.Tests;

public class LongSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "faden-ui-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task A_session_of_2000_turns_opens_fast_and_shows_only_its_latest_turns()
    {
        var store = new FileSessionStore(_directory, new XorProtector());
        const string id = "s-long";
        var answer = string.Concat(Enumerable.Repeat("Some **markdown** with `code` and a [link](https://x.example). ", 20));
        await store.AppendAsync(Gateway.Slug, id, Entry(EntryTypes.SessionStart, null, new { agent = Gateway.Slug, tool_mode = "ask" }));
        for (var t = 0; t < 2000; t++)
        {
            var turn = $"t{t}";
            await store.AppendAsync(Gateway.Slug, id, Entry(EntryTypes.UserMessage, turn, new { text = $"Question {t}" }));
            await store.AppendAsync(Gateway.Slug, id, Entry(EntryTypes.TurnStart, turn, new { tool_mode = "ask" }));
            await store.AppendAsync(Gateway.Slug, id, Entry(EntryTypes.ToolStart, turn, new { call_id = $"c{t}", name = "read_page", source = "host", tool_class = "read" }));
            await store.AppendAsync(Gateway.Slug, id, Entry(EntryTypes.ToolComplete, turn, new { call_id = $"c{t}", name = "read_page", source = "host", result = "page", outcome = "ok", duration_ms = 5 }));
            await store.AppendAsync(Gateway.Slug, id, Entry(EntryTypes.AssistantMessage, turn, new { text = answer, tool_calls = Array.Empty<object>(), origin = "model" }));
            await store.AppendAsync(Gateway.Slug, id, Entry(EntryTypes.TurnEnd, turn, new { status = "ok", outcome = "Completed" }, TurnStatus.Ok));
        }
        var broker = new ApprovalBroker();
        using var agent = new Gateway().Builder(store).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions { RenderInterval = TimeSpan.Zero });
        await chat.InitializeAsync();

        var watch = Stopwatch.StartNew();
        await chat.OpenSessionAsync(id);
        watch.Stop();

        Assert.Equal(30, chat.Turns.Count);
        Assert.Equal("Question 1999", chat.Turns[^1].UserText);
        Assert.True(chat.HasEarlierTurns);
        Assert.True(watch.ElapsedMilliseconds < 3000, $"opening 2000 turns took {watch.ElapsedMilliseconds} ms");
        Console.WriteLine($"open 2000 turns (12 000 entries): {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task The_wpf_samples_demo_gateway_drives_the_chat()
    {
        var broker = new ApprovalBroker();
        using var agent = FadenAgent.CreateBuilder()
            .UseServer(new Uri("https://demo.invalid"), new StaticTokenProvider("demo"), o => o.HttpClient = new HttpClient(new DemoGateway()))
            .UseAgent(DemoGateway.Agent).UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
            .AddTools(new Faden.Samples.DemoTools()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions { RenderInterval = TimeSpan.Zero });
        await chat.InitializeAsync();
        Assert.Equal(("Demo assistant", 4), (chat.Title, chat.Suggestions.Count));

        await chat.SendSuggestionCommand.ExecuteAsync("What can you do?");
        var blocks = ((MarkdownPart)chat.Turns[0].Parts.Single()).Blocks;
        Assert.Equal(new[] { "HeadingBlock", "ParagraphBlock", "ListBlock", "TableBlock", "CodeBlock", "QuoteBlock" },
            blocks.Select(b => b.GetType().Name));

        await chat.SendAsync("Read page 3");
        Assert.Contains("Page 3: contactor K3", ((MarkdownPart)chat.Turns[1].Parts.Last()).Text);

        var sending = chat.SendAsync("Delete page 2");
        ApprovalPart? card = null;
        for (var i = 0; i < 300 && card is null; i++)
        {
            await Task.Delay(10);
            try
            {
                card = chat.Turns.Count > 2 ? chat.Turns[2].Parts.ToArray().OfType<ApprovalPart>().FirstOrDefault() : null;
            }
            catch (InvalidOperationException)
            {
            }
        }
        card!.DenyCommand.Execute(null);
        await sending;
        Assert.Equal(ChatOutcome.ToolDenied, chat.Turns[2].Outcome);
    }

    private static TranscriptEntry Entry(string type, string? turn, object data, string? status = null) => new()
    {
        Type = type,
        TurnId = turn,
        Status = status,
        Data = JsonSerializer.SerializeToElement(data),
    };

    private sealed class XorProtector : ISecretProtector
    {
        public bool Encrypts => true;

        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data) => Protect(data);
    }
}
