using System.Text;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Client.Tests;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The session and approval stores skip what they cannot read, log its kind, and keep the rest: one torn line (a crash
/// while appending) or another user's file never breaks the list of chats (sdk/51 I00 #4, I-9 and N3).
/// </summary>
public sealed class StoreFailureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "faden-stores-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static TranscriptEntry Said(string text) => new()
    {
        Type = EntryTypes.UserMessage, TurnId = "t", Data = JsonDocument.Parse(JsonSerializer.Serialize(new { text })).RootElement.Clone(),
    };

    [Fact]
    public async Task A_torn_or_foreign_line_is_skipped_and_the_list_still_shows_every_chat()
    {
        var logs = new CapturingLoggerFactory();
        var store = new FileSessionStore(_directory, logger: logs.CreateLogger("Fadenstack.Agents"));
        await store.AppendAsync("a", "s1", Said("PLAN-ONE"));
        await store.AppendAsync("a", "s1", Said("PLAN-TWO"));
        await store.AppendAsync("a", "s2", Said("other chat"));
        var file = Path.Combine(_directory, "a", "s1.jsonl");
        // A line of another user's data (not this protector's), garbage, and a line torn by a crash while appending.
        File.AppendAllText(file, Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"not\":\"an entry\"")) + "\n", Encoding.ASCII);
        File.AppendAllText(file, "%%% not base64 %%%\n", Encoding.ASCII);
        File.AppendAllText(file, "eyJ0eXBlIjoidXNlcl9tZXNz", Encoding.ASCII);

        var entries = await store.ReadAsync("a", "s1");
        var listed = await store.ListAsync("a");

        Assert.Equal(new[] { "PLAN-ONE", "PLAN-TWO" }, entries.Select(e => e.Data.GetProperty("text").GetString()));
        Assert.Equal(new[] { "s1", "s2" }, listed.Select(s => s.Id).OrderBy(id => id));
        var skipped = logs.Entries.Where(e => e.EventId == 2051).ToList();
        Assert.NotEmpty(skipped);
        Assert.All(skipped, e => Assert.Contains(e.Values, v => v == "Count=3"));
        Assert.DoesNotContain(logs.Texts, t => t.Contains(_directory) || t.Contains("PLAN") || t.Contains("s1.jsonl"));
    }

    [Fact]
    public async Task A_session_file_another_process_holds_locked_reads_as_empty_and_the_others_are_listed()
    {
        var logs = new CapturingLoggerFactory();
        var store = new FileSessionStore(_directory, logger: logs.CreateLogger("Fadenstack.Agents"));
        await store.AppendAsync("a", "s1", Said("PLAN-ONE"));
        await store.AppendAsync("a", "s2", Said("other chat"));

        using (new FileStream(Path.Combine(_directory, "a", "s1.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(await store.ReadAsync("a", "s1"));
            Assert.Equal("s2", Assert.Single(await store.ListAsync("a")).Id);
        }
        Assert.Contains(logs.Entries, e => e.EventId == 2052 && e.Values.Contains("Kind=IOException"));
        Assert.Equal("PLAN-ONE", Assert.Single(await store.ReadAsync("a", "s1")).Data.GetProperty("text").GetString());
    }

    [Fact]
    public async Task An_approval_file_another_process_holds_locked_reads_as_none_so_the_user_is_asked()
    {
        var logs = new CapturingLoggerFactory();
        var store = new FileApprovalStore(_directory, logger: logs.CreateLogger("Fadenstack.Agents"));
        await store.AddAsync("a", new StoredApproval("read_page", ToolClass.Read, DateTimeOffset.UtcNow));

        using (new FileStream(Directory.GetFiles(_directory).Single(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(await store.ListAsync("a"));
        }

        var entry = Assert.Single(logs.Entries, e => e.EventId == 2050);
        Assert.Contains(entry.Values, v => v.StartsWith("Kind=", StringComparison.Ordinal));
        Assert.Equal("read_page", Assert.Single(await store.ListAsync("a")).Tool); // free again: the approval is there
    }

    [Fact]
    public async Task Adding_an_approval_while_the_file_is_locked_fails_and_writes_nothing_over()
    {
        var store = new FileApprovalStore(_directory);
        await store.AddAsync("a", new StoredApproval("read_page", ToolClass.Read, DateTimeOffset.UtcNow));

        using (new FileStream(Directory.GetFiles(_directory).Single(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                store.AddAsync("a", new StoredApproval("edit_page", ToolClass.Write, DateTimeOffset.UtcNow)));
        }

        Assert.Equal("read_page", Assert.Single(await store.ListAsync("a")).Tool);
    }
}
