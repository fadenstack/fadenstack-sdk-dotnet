using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>Sessions on the device (06): the transcript, the store, and the history built from it.</summary>
public class SessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "faden-sessions-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task A_session_is_reopened_from_its_file_with_its_mode_and_history()
    {
        var gateway = new Gateway().Says("First.").Says("Second.");
        using var agent = gateway.Builder(new FileSessionStore(_directory)).Build();
        var session = await agent.StartSessionAsync(ToolMode.ReadOnly);
        await session.SendAsync("One");

        using var again = new Gateway().Says("Second.").Builder(new FileSessionStore(_directory)).Build();
        var reopened = await again.OpenSessionAsync(session.Id);
        Assert.Equal(ToolMode.ReadOnly, reopened.ToolMode);
        Assert.Equal(session.Transcript.Select(e => (e.Id, e.Type)), reopened.Transcript.Select(e => (e.Id, e.Type)));
        Assert.Equal(new[] { "One", "First." }, reopened.History.Select(m => m.Text));
        Assert.All(reopened.Transcript.Skip(1), e => Assert.Equal(reopened.Transcript[reopened.Transcript.ToList().IndexOf(e) - 1].Id, e.ParentId));

        var listed = Assert.Single(await again.ListSessionsAsync());
        Assert.Equal((session.Id, "One"), (listed.Id, listed.Title));
        var file = Directory.GetFiles(Path.Combine(_directory, Gateway.Slug)).Single();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        await again.DeleteSessionAsync(session.Id);
        Assert.Empty(await again.ListSessionsAsync());
    }

    [Fact]
    public async Task Lines_are_protected_with_the_given_protector()
    {
        var store = new FileSessionStore(_directory, new XorProtector());
        await store.AppendAsync("a", "s1", new TranscriptEntry { Type = EntryTypes.UserMessage, TurnId = "t", Data = Json("""{"text":"secret plan"}""") });
        var raw = File.ReadAllText(Directory.GetFiles(Path.Combine(_directory, "a")).Single());
        Assert.DoesNotContain("secret", raw);
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(Convert.FromBase64String(raw.Trim())));
        var entry = Assert.Single(await store.ReadAsync("a", "s1"));
        Assert.Equal("secret plan", entry.Data.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Retention_keeps_the_newest_sessions()
    {
        var store = new FileSessionStore(_directory, retention: new SessionRetention(MaxSessions: 2));
        foreach (var id in new[] { "s1", "s2", "s3" })
        {
            await store.AppendAsync("a", id, new TranscriptEntry { Type = EntryTypes.SessionStart, Data = Json("{}") });
            File.SetLastWriteTimeUtc(Path.Combine(_directory, "a", id + ".jsonl"), DateTime.UtcNow.AddMinutes(id[1] - '9'));
        }
        Assert.Equal(new[] { "s2", "s3" }, (await store.ListAsync("a")).Select(s => s.Id).OrderBy(s => s));
    }

    [Fact]
    public void The_history_follows_the_session_rule()
    {
        var entries = new List<TranscriptEntry>();
        void Turn(string id, string status, string question, string? answer = null)
        {
            entries.Add(new TranscriptEntry { Type = EntryTypes.UserMessage, TurnId = id, Data = Json(JsonSerializer.Serialize(new { text = question })) });
            if (answer is not null)
            {
                entries.Add(new TranscriptEntry { Type = EntryTypes.AssistantMessage, TurnId = id, Data = Json(JsonSerializer.Serialize(new { text = answer, tool_calls = Array.Empty<object>() })) });
            }
            entries.Add(new TranscriptEntry { Type = EntryTypes.TurnEnd, TurnId = id, Status = status, Data = Json("{}") });
        }
        Turn("t1", TurnStatus.Ok, "q1", "a1");
        Turn("t2", TurnStatus.PromptBlocked, "blocked");
        Turn("t3", TurnStatus.ResponseBlocked, "q3", "half an answer");
        Turn("t4", TurnStatus.Error, "failed", "partial");
        Turn("t5", TurnStatus.Cancelled, "stopped", "partial");
        entries.Add(new TranscriptEntry { Type = EntryTypes.UserMessage, TurnId = "t6", Data = Json("""{"text":"q6"}""") });

        var history = new ContextBuilder().Build(entries);

        Assert.Equal(new[] { ("user", "q1"), ("assistant", "a1"), ("user", "q3\n\nq6") },
            history.Select(m => (m.Role.Value, m.Text)));
    }

    [Fact]
    public void Old_turns_go_first_when_the_history_passes_its_budget()
    {
        var entries = Enumerable.Range(1, 10).SelectMany(i => new[]
        {
            new TranscriptEntry { Type = EntryTypes.UserMessage, TurnId = $"t{i}", Data = Json(JsonSerializer.Serialize(new { text = $"q{i} " + new string('x', 400) })) },
            new TranscriptEntry { Type = EntryTypes.AssistantMessage, TurnId = $"t{i}", Data = Json(JsonSerializer.Serialize(new { text = $"a{i}" })) },
            new TranscriptEntry { Type = EntryTypes.TurnEnd, TurnId = $"t{i}", Status = TurnStatus.Ok, Data = Json("{}") },
        }).ToList();
        var history = new ContextBuilder { TokenBudget = 350 }.Build(entries); // ~1400 characters: three turns
        Assert.Equal(new[] { "q8", "a8", "q9", "a9", "q10", "a10" }, history.Select(m => m.Text.Split(' ')[0]));
    }

    [Fact]
    public async Task The_agent_is_a_service_on_the_standard_host()
    {
        var gateway = new Gateway().Says("Hi.");
        var services = new ServiceCollection()
            .AddSingleton(new PageTools())
            .AddFadenstackAgent((agent, provider) => agent
                .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("t"), o => o.HttpClient = new HttpClient(gateway.Server))
                .UseAgent(Gateway.Slug)
                .UseSessionStore(new MemorySessionStore())
                .AddTools(provider.GetRequiredService<PageTools>()));
        using var provider = services.BuildServiceProvider();
        var agent = provider.GetRequiredService<FadenstackAgent>();
        Assert.Same(agent, provider.GetRequiredService<FadenstackAgent>());
        await agent.StartAsync();
        Assert.Equal(4, agent.Tools.Count);
    }

    [Fact]
    public void Host_tools_are_named_and_described_from_their_methods()
    {
        var tools = AgentTool.From(new PageTools());
        Assert.Equal(new[] { "read_page", "add_comment", "delete_page", "stamp" }, tools.Select(t => t.Name));
        Assert.Equal(new[] { ToolClass.Read, ToolClass.Write, ToolClass.Destructive, ToolClass.Unknown }, tools.Select(t => t.Class));
        Assert.Equal("Reads a page of the document.", tools[0].Function.Description);
        Assert.Contains("Page number", tools[0].Function.JsonSchema.GetRawText());
        Assert.Throws<ArgumentException>(() => AgentTool.From(new object()));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class XorProtector : ISecretProtector
    {
        public bool Encrypts => true;

        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data) => Protect(data);
    }
}
