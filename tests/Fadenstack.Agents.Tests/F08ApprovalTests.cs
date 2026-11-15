using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// sdk/51 F08: a reason for declining reaches the model and the transcript, cut; "always" and "for this chat" know where a tool
/// comes from; a stored approval is sealed, so one planted, copied or kept before 0.3.0 answers nothing.
/// </summary>
public sealed class F08ApprovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden-f08-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Gateway Comment() => new Gateway().Calls(("call-1", "add_comment", """{"page":1,"text":"Fine."}""")).Says("OK.");

    [Fact]
    public async Task A_reason_for_declining_reaches_the_model_and_the_transcript_cut_and_cleaned()
    {
        var gateway = Comment();
        var sessions = new MemorySessionStore();
        var reason = "Not on page 1.\u202E" + new string('x', 700);
        using var agent = gateway.Builder(sessions).AddTools(new PageTools())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Deny(reason))).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");

        var told = McpRig.LastToolResult(gateway.ChatRequests[1]);
        Assert.StartsWith("The user declined to run 'add_comment'. Their reason: Not on page 1.x", told);
        Assert.DoesNotContain("\u202E", told, StringComparison.Ordinal);
        var kept = (await sessions.ReadAsync(Gateway.Slug, session.Id)).Single(e => e.Type == EntryTypes.ApprovalDecision);
        var stored = kept.Data.GetProperty("reason").GetString()!;
        Assert.Equal(ApprovalDecision.MaxReasonChars, stored.Length);
        Assert.StartsWith("Not on page 1.x", stored);
    }

    [Fact]
    public async Task An_approval_is_recorded_without_a_reason()
    {
        var gateway = Comment();
        var sessions = new MemorySessionStore();
        using var agent = gateway.Builder(sessions).AddTools(new PageTools())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        await session.SendAsync("Comment.");

        var kept = (await sessions.ReadAsync(Gateway.Slug, session.Id)).Single(e => e.Type == EntryTypes.ApprovalDecision);
        Assert.False(kept.Data.TryGetProperty("reason", out var r) && r.ValueKind != JsonValueKind.Null);
    }

    [Theory]
    [InlineData("host", null, 0)] // the same tool from the same place: answered
    [InlineData("mcp", "old-server", 1)] // a tool of the same name from another source: asked
    [InlineData(null, null, 1)] // kept before 0.3.0, no source: asked again (F08 §4, the safe answer)
    public async Task Allow_always_answers_only_a_tool_from_the_same_source_and_server(string? source, string? server, int asked)
    {
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow) { Source = source, Server = server });
        var requests = new List<ApprovalRequest>();
        using var agent = Comment().Builder(approvals: store).AddTools(new PageTools())
            .UseApprovals((r, _) => { requests.Add(r); return Task.FromResult(ApprovalDecision.Approve()); }).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("Comment.");

        Assert.Equal(asked, requests.Count);
    }

    [Fact]
    public async Task Allow_always_keeps_where_the_tool_comes_from()
    {
        var store = new MemoryApprovalStore();
        using var agent = Comment().Builder(approvals: store).AddTools(new PageTools())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.ApproveAlways())).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("Comment.");

        var kept = Assert.Single(await store.ListAsync(Gateway.Slug));
        Assert.Equal(("add_comment", "host", (string?)null), (kept.Tool, kept.Source, kept.Server));
    }

    private FileApprovalStore Person(string user, ISecretProtector protector) => new(Path.Combine(_root, "users", user, "approvals"), protector);

    private static StoredApproval Always(string tool = "add_comment") =>
        new(tool, ToolClass.Write, DateTimeOffset.UtcNow) { Source = "host" };

    [Fact]
    public async Task A_sealed_approval_reads_back_and_one_copied_to_another_agent_or_person_answers_nothing()
    {
        var protector = new FakeEncryptingProtector();
        var anna = Person("anna", protector);
        await anna.AddAsync("docs", Always());

        Assert.Single(await Person("anna", protector).ListAsync("docs"));
        Assert.True(File.Exists(Path.Combine(_root, "users", "anna", "approvals", FileApprovalStore.SealKeyName)));

        var file = Path.Combine(_root, "users", "anna", "approvals", "docs.json");
        File.Copy(file, Path.Combine(_root, "users", "anna", "approvals", "mail.json"));
        Assert.Empty(await Person("anna", protector).ListAsync("mail")); // another agent's file

        var ben = Person("ben", protector);
        await ben.AddAsync("other", Always("read_page"));
        File.Copy(file, Path.Combine(_root, "users", "ben", "approvals", "docs.json"));
        Assert.Empty(await Person("ben", protector).ListAsync("docs")); // another person's folder, another key
    }

    [Fact]
    public async Task An_approval_planted_or_changed_in_the_file_answers_nothing()
    {
        var protector = new OwnerOnlyFileProtector(); // nothing encrypts: the file is plain, so it can be written by hand
        var store = Person("anna", protector);
        await store.AddAsync("docs", Always());
        var file = Path.Combine(_root, "users", "anna", "approvals", "docs.json");

        var changed = File.ReadAllText(file).Replace("add_comment", "delete_page");
        File.WriteAllText(file, changed);
        Assert.Empty(await Person("anna", protector).ListAsync("docs"));

        File.WriteAllText(file, """[{"tool":"delete_page","tool_class":"destructive","approved_at":"2026-11-15T10:00:00+00:00","source":"host"}]""");
        Assert.Empty(await Person("anna", protector).ListAsync("docs"));
    }
}
