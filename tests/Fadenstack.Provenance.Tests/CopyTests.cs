using System.Text.Json;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Provenance.Tests;

/// <summary>The mark on copy: what the person copies out of the chat goes through the placement pipeline as the tool <c>copy</c>.</summary>
public class CopyTests : IDisposable
{
    private const string Id = "5a0e2b5c-7f3e-4a51-9a40-000000000001";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "faden-copy-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private static Gateway NewGateway(string? policy = """{"on_missing":"record","short_text_tokens":200}""") =>
        new(contract: WritingTools.Contract, userLanguage: "en", provenancePolicy: policy);

    private static Gateway Answering(Gateway gateway) =>
        gateway.Then(Gateway.Text("Here is the answer."), Gateway.Finish("stop"), Gateway.Receipt("rc-1"), FakeServer.Done);

    private static async Task<(ChatSession Session, string Turn)> AskAsync(FadenstackAgent agent)
    {
        var session = await agent.StartSessionAsync(ToolMode.Auto);
        await session.SendAsync("Answer the customer.");
        return (session, session.Transcript.Last(e => e.TurnId is not null).TurnId!);
    }

    private static List<JsonElement> Entries(ChatSession session, string type) =>
        session.Transcript.Where(e => e.Type == type).Select(e => e.Data).ToList();

    [Fact]
    public async Task A_copy_is_one_placement_with_the_sink_clipboard_linked_to_the_turns_receipt()
    {
        var gateway = Answering(NewGateway());
        using var agent = gateway.Builder().Use(new ProvenanceModule()).Build();
        var (session, turn) = await AskAsync(agent);
        var html = "<p>" + TestAgents.LongText + "</p>";

        var copied = await agent.MarkCopyAsync(new AnswerCopy(TestAgents.LongText, html, session.Id, turn));

        Assert.True(copied.Marked);
        Assert.False(copied.Refused);
        Assert.Equal(Id, copied.RecordId);
        Assert.Equal(TestAgents.LongText, copied.Text); // the plain text is as it was
        Assert.StartsWith($"<p><span data-ai-generated=\"{Id}\" title=\"AI-generated {Id}\">The pump", copied.Html);
        Assert.Equal(new[] { new FoundMark(Id, "html-attribute"), new FoundMark(Id, ProvenanceReader.HtmlTitle) },
            ProvenanceReader.Read(copied.Html!, "text/html"));

        var request = Assert.Single(gateway.RecordRequests).Json;
        Assert.Equal((ContentHash.TextSha256(html), ContentHash.TextSha256(TestAgents.LongText), "text/html", "generated", "rc-1"),
            (request.GetProperty("content_sha256").GetString(), request.GetProperty("text_sha256").GetString(), request.GetProperty("media_type").GetString(),
             request.GetProperty("edit_kind").GetString(), request.GetProperty("receipt_id").GetString()));

        var report = Assert.Single(gateway.Placements);
        Assert.DoesNotContain("pump", report.Body); // never content
        Assert.Equal(("copy", "clipboard", Id), (report.Json.GetProperty("tool").GetString(), report.Json.GetProperty("sink_kind").GetString(),
            report.Json.GetProperty("record_id").GetString()));
        var methods = report.Json.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("method").GetString()).ToList();
        Assert.Contains("html-attribute", methods);
        Assert.Contains("signed-record", methods);

        var placed = Assert.Single(Entries(session, ProvenanceEntries.Placed));
        Assert.Equal(("copy", "clipboard", "rc-1", Id), (placed.GetProperty("tool").GetString(), placed.GetProperty("sink_kind").GetString(),
            placed.GetProperty("receipt_id").GetString(), placed.GetProperty("record_id").GetString()));
        Assert.DoesNotContain("pump", placed.GetRawText());
    }

    [Fact]
    public async Task Without_the_module_the_copy_comes_back_as_it_is()
    {
        var gateway = Answering(NewGateway());
        using var agent = gateway.Builder().Build();

        var copied = await agent.MarkCopyAsync(new AnswerCopy("Hello", "<p>Hello</p>"));

        Assert.Equal(new MarkedCopy("Hello", "<p>Hello</p>", null, false), copied);
        Assert.Empty(gateway.RecordRequests);
    }

    [Fact]
    public async Task Code_is_exempt_copied_as_it_is_and_recorded_as_exempt()
    {
        var gateway = Answering(NewGateway());
        using var agent = gateway.Builder().Use(new ProvenanceModule()).Build();
        var (session, turn) = await AskAsync(agent);

        var copied = await agent.MarkCopyAsync(new AnswerCopy("var x = 1;", "<pre><code>var x = 1;</code></pre>", session.Id, turn) { IsCode = true });

        Assert.False(copied.Marked);
        Assert.Equal("<pre><code>var x = 1;</code></pre>", copied.Html);
        Assert.Equal(ExemptReasons.Code, copied.ExemptBecause);
        Assert.Empty(gateway.RecordRequests); // no record
        var placed = Assert.Single(Entries(session, ProvenanceEntries.Placed));
        Assert.True(placed.GetProperty("exempt").GetBoolean());
        Assert.Equal("copy", placed.GetProperty("tool").GetString());
    }

    [Fact]
    public async Task Text_without_html_is_signed_and_reported_and_the_text_stays()
    {
        var gateway = Answering(NewGateway());
        using var agent = gateway.Builder().Use(new ProvenanceModule()).Build();
        var (session, turn) = await AskAsync(agent);

        var copied = await agent.MarkCopyAsync(new AnswerCopy(TestAgents.LongText, null, session.Id, turn));

        Assert.False(copied.Marked); // no carrier in plain text: nothing is written into it
        Assert.Null(copied.Html);
        Assert.Equal(TestAgents.LongText, copied.Text);
        Assert.Equal("text/markdown", Assert.Single(gateway.RecordRequests).Json.GetProperty("media_type").GetString());
        Assert.Equal(Id, copied.RecordId);
    }

    [Fact]
    public async Task With_the_policy_refuse_a_copy_that_cannot_get_its_layer_is_not_copied()
    {
        var gateway = Answering(NewGateway("""{"on_missing":"refuse"}"""));
        gateway.ProvenanceDown = true;
        using var agent = gateway.Builder().Use(new ProvenanceModule()).Build();
        var (session, turn) = await AskAsync(agent);

        var copied = await agent.MarkCopyAsync(new AnswerCopy("Hello there.", "<p>Hello there.</p>", session.Id, turn));

        Assert.True(copied.Refused);
        Assert.False(copied.Marked);
        Assert.Null(copied.Html);
        Assert.Single(Entries(session, ProvenanceEntries.Refused));
        Assert.Empty(Entries(session, ProvenanceEntries.Placed));
        Assert.Empty(gateway.Placements);
    }

    [Fact]
    public async Task With_the_default_policy_a_copy_goes_through_unmarked_when_the_server_cannot_sign()
    {
        var gateway = Answering(NewGateway());
        gateway.ProvenanceDown = true;
        using var agent = gateway.Builder().Use(new ProvenanceModule()).Build();
        var (session, turn) = await AskAsync(agent);

        var copied = await agent.MarkCopyAsync(new AnswerCopy("Hello there.", "<p>Hello there.</p>", session.Id, turn));

        Assert.False(copied.Refused);
        Assert.False(copied.Marked);
        Assert.Equal("<p>Hello there.</p>", copied.Html);
        Assert.Null(copied.RecordId);
    }

    [Fact]
    public async Task On_the_device_without_a_server_the_copy_is_signed_by_the_device()
    {
        using var agent = FadenstackAgent.CreateBuilder()
            .UseChatClient(new NoModel())
            .UseLocalProfile(AgentDefinition.FromJson("""{"slug":"docs-assistant"}"""))
            .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
            .Use(new ProvenanceModule(o => o.UseLocalBackend(new LocalProvenanceOptions { Folder = _folder, Signer = DeviceSigner.Ephemeral(), Timestamping = false })))
            .Build();

        var copied = await agent.MarkCopyAsync(new AnswerCopy("Hello there.", "<p>Hello there.</p>"));

        Assert.True(copied.Marked);
        Assert.True(Guid.TryParse(copied.RecordId, out _));
        Assert.Contains($"data-ai-generated=\"{copied.RecordId}\"", copied.Html);
        Assert.Equal("Hello there.", copied.Text);
    }

    private sealed class NoModel : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
