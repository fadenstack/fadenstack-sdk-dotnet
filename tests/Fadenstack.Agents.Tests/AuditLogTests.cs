using System.Text;
using Fadenstack.Agents.Audit;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The person's record of tool calls (sdk/51 8.10): the chain's format (shared vectors with the TypeScript SDK), the file log, and what
/// verify finds when a line is changed, deleted, swapped, borrowed or forged. ASVS 5.0 V16 (security logging: integrity).
/// </summary>
public class AuditLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "faden-audit-" + Guid.NewGuid());
    private readonly FakeEncryptingProtector _protector = new();
    private DateTimeOffset _now = new(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private FileAuditLog Open(string? name = null) =>
        new(Path.Combine(_folder, name ?? "log"), _protector, () => _now);

    private static AuditRecord Record(string tool = "read_page", string decision = AuditDecisions.Mode, string outcome = AuditOutcomes.Ran,
        string app = "Word") => new()
    {
        App = app, Agent = "writer", Chat = "chat-1", Turn = "turn-1", Tool = tool, Class = "read", Source = "host", Decision = decision,
        Outcome = outcome, ArgumentsSha256 = AuditChain.ArgumentsSha256("""{"page":1}"""),
    };

    private string FileOf(string name = "log") => Path.Combine(_folder, name, FileAuditLog.FileName);

    private AuditEntry Decode(string line) => AuditChain.FromJson(_protector.Unprotect(Convert.FromBase64String(line)))!;

    private string Encode(AuditEntry entry) => Convert.ToBase64String(_protector.Protect(AuditChain.ToJson(entry)));

    private byte[] KeyOf(string name = "log") =>
        _protector.Unprotect(File.ReadAllBytes(Path.Combine(_folder, name, FileAuditLog.SealKeyName)));

    /// <summary>An entry as an attacker who holds the key would forge it: hash and seal both hold.</summary>
    private AuditEntry Reseal(AuditEntry entry, byte[] key)
    {
        var hash = AuditChain.HashOf(entry);
        return entry with { Hash = hash, Seal = AuditChain.SealOf(key, hash) };
    }

    // ---- the format, the same vectors as the TypeScript SDK ----

    private static readonly byte[] VectorKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static AuditEntry VectorOne() => new()
    {
        Seq = 1, Time = new DateTimeOffset(2026, 11, 16, 9, 30, 0, 123, TimeSpan.Zero), Kind = "tool", App = "Word", Agent = "writer",
        Chat = "chat-1", Turn = "turn-1", Tool = "read_page", Class = "read", Source = "host", Server = null, Decision = "mode", Scope = null,
        Outcome = "ran", ArgumentsSha256 = AuditChain.ArgumentsSha256("""{"page":1}"""), Prev = AuditChain.Zeros,
    };

    private static AuditEntry VectorTwo() => new()
    {
        Seq = 2, Time = new DateTimeOffset(2026, 11, 16, 9, 30, 5, 7, TimeSpan.Zero), Kind = "tool", App = "Excel", Agent = "writer",
        Chat = "chat-1", Turn = "turn-2", Tool = "delete_page", Class = "destructive", Source = "mcp", Server = "files",
        Decision = "asked_approved", Scope = "once", Outcome = "ran", ArgumentsSha256 = AuditChain.ArgumentsSha256("{\"name\":\"caf\u00e9\"}"),
        Prev = "bd9a85855ec1225e80f87aca01e2df62576b870b10dde8646a8f753e38fafea9",
    };

    [Fact]
    public void The_shared_vectors_give_the_same_arguments_hashes_hashes_and_seals_as_the_other_sdk()
    {
        Assert.Equal("70fb0185588d2e765454a7927f2792ae2b6faa2516781deb69864246e0803d05", VectorOne().ArgumentsSha256);
        Assert.Equal("645fa443126a8954fc6d871912b8fc67bc2ee8feae417efe55546251962ca74d", VectorTwo().ArgumentsSha256); // UTF-8, not UTF-16
        var one = VectorOne();
        Assert.Equal("bd9a85855ec1225e80f87aca01e2df62576b870b10dde8646a8f753e38fafea9", AuditChain.HashOf(one));
        Assert.Equal("91k1kG1HT7VKBq8H4KqYLxwIOpCel/S/XC3OSfDoGfg=", AuditChain.SealOf(VectorKey, AuditChain.HashOf(one)));
        var two = VectorTwo();
        Assert.Equal("4ede970e8bfea453d7762d5b94669e6ec36fb8b6175ff9419fcc61228dffc6a2", AuditChain.HashOf(two));
        Assert.Equal("W96Za/zLEg2/Eopq8rv7EgtpDs/Jbc6h/mUCQblIsO8=", AuditChain.SealOf(VectorKey, AuditChain.HashOf(two)));
        Assert.Equal("k1NEiONJG9RFLUFaoPd4RhaN7N+bJVDg5RLzCmrVrbQ=", AuditChain.AnchorSeal(VectorKey, 7, AuditChain.HashOf(one)));
        Assert.Equal("kQnmGQD/HVREn3S+2Wge+RAR7wEUNYQggSql1Ys2i+w=", AuditChain.HeadSeal(VectorKey, 7, AuditChain.HashOf(one)));
    }

    [Fact]
    public void The_canonical_text_is_the_version_and_the_fields_in_order_with_null_as_nothing()
    {
        Assert.Equal(
            "fadenstack.audit.v1\n1\n2026-11-16T09:30:00.123Z\ntool\nWord\nwriter\nchat-1\nturn-1\nread_page\nread\nhost\n\nmode\n\nran\n"
            + "70fb0185588d2e765454a7927f2792ae2b6faa2516781deb69864246e0803d05\n" + AuditChain.Zeros,
            AuditChain.CanonicalText(VectorOne()));
    }

    // ---- the log ----

    [Fact]
    public async Task Entries_are_numbered_from_one_linked_and_verify_intact()
    {
        var log = Open();
        var first = await log.AppendAsync(Record());
        _now = _now.AddSeconds(1);
        var second = await log.AppendAsync(Record("add_comment", AuditDecisions.AskedApproved));

        Assert.Equal((1L, AuditChain.Zeros), (first.Seq, first.Prev));
        Assert.Equal((2L, first.Hash), (second.Seq, second.Prev));
        var result = await log.VerifyAsync();
        Assert.True(result.Intact);
        Assert.Equal((2, 1L, _now.AddSeconds(-1), _now), (result.Entries, result.FirstSeq, result.FirstTime, result.LastTime));
    }

    [Fact]
    public async Task An_empty_log_is_intact_and_the_lines_on_disk_hold_no_readable_text()
    {
        var log = Open();
        Assert.True((await log.VerifyAsync()).Intact);
        await log.AppendAsync(Record("read_page_secret_name"));

        var onDisk = File.ReadAllText(FileOf());
        Assert.DoesNotContain("read_page_secret_name", onDisk);
        Assert.DoesNotContain("writer", onDisk);
        Assert.True(File.Exists(Path.Combine(_folder, "log", FileAuditLog.SealKeyName)));
    }

    [Fact]
    public async Task Another_instance_carries_on_the_same_chain()
    {
        await Open().AppendAsync(Record());
        var next = await Open().AppendAsync(Record());

        Assert.Equal(2, next.Seq);
        Assert.True((await Open().VerifyAsync()).Intact);
    }

    [Fact]
    public async Task Two_writers_of_two_apps_in_parallel_never_fork_the_chain()
    {
        var one = Open();
        var two = Open(); // another process in real life: its own instance, the same folder
        var work = Enumerable.Range(0, 30).Select(i => Task.Run(() => one.AppendAsync(Record(app: "Word"))))
            .Concat(Enumerable.Range(0, 30).Select(i => Task.Run(() => two.AppendAsync(Record(app: "Excel")))));

        var entries = await Task.WhenAll(work);

        Assert.Equal(Enumerable.Range(1, 60).Select(i => (long)i), entries.Select(e => e.Seq).OrderBy(s => s));
        var result = await one.VerifyAsync();
        Assert.True(result.Intact, result.Problem);
        Assert.Equal(60, result.Entries);
    }

    [Fact]
    public async Task A_changed_field_is_found_at_its_entry()
    {
        var log = Open();
        for (var i = 0; i < 4; i++)
        {
            await log.AppendAsync(Record());
        }
        var lines = File.ReadAllLines(FileOf());
        lines[2] = Encode(Decode(lines[2]) with { Decision = AuditDecisions.AskedApproved }); // hash and seal left as they were
        File.WriteAllLines(FileOf(), lines);

        var result = await log.VerifyAsync();

        Assert.False(result.Intact);
        Assert.Equal((3L, AuditProblems.HashMismatch), (result.BrokenAtSeq, result.Problem));
    }

    [Fact]
    public async Task A_deleted_line_is_found_where_the_number_is_missing()
    {
        var log = Open();
        for (var i = 0; i < 4; i++)
        {
            await log.AppendAsync(Record());
        }
        File.WriteAllLines(FileOf(), File.ReadAllLines(FileOf()).Where((_, i) => i != 1));

        var result = await log.VerifyAsync();

        Assert.False(result.Intact);
        Assert.Equal((2L, AuditProblems.SeqGap), (result.BrokenAtSeq, result.Problem));
    }

    [Fact]
    public async Task Swapped_lines_are_found()
    {
        var log = Open();
        for (var i = 0; i < 4; i++)
        {
            await log.AppendAsync(Record());
        }
        var lines = File.ReadAllLines(FileOf());
        (lines[1], lines[2]) = (lines[2], lines[1]);
        File.WriteAllLines(FileOf(), lines);

        var result = await log.VerifyAsync();

        Assert.False(result.Intact);
        Assert.Equal((2L, AuditProblems.SeqGap), (result.BrokenAtSeq, result.Problem));
    }

    [Fact]
    public async Task A_line_from_another_persons_log_does_not_hold()
    {
        var mine = Open("mine");
        var theirs = Open("theirs");
        for (var i = 0; i < 3; i++)
        {
            await mine.AppendAsync(Record()); // the same fields and times: only the keys differ
            await theirs.AppendAsync(Record());
        }
        var lines = File.ReadAllLines(FileOf("mine"));
        lines[1] = File.ReadAllLines(FileOf("theirs"))[1];
        File.WriteAllLines(FileOf("mine"), lines);

        var result = await mine.VerifyAsync();

        Assert.False(result.Intact);
        Assert.Equal((2L, AuditProblems.SealMismatch), (result.BrokenAtSeq, result.Problem));
    }

    [Fact]
    public async Task A_forged_seal_does_not_hold()
    {
        var log = Open();
        for (var i = 0; i < 3; i++)
        {
            await log.AppendAsync(Record());
        }
        var lines = File.ReadAllLines(FileOf());
        lines[1] = Encode(Decode(lines[1]) with { Seal = AuditChain.SealOf(new byte[32], Decode(lines[1]).Hash) });
        File.WriteAllLines(FileOf(), lines);

        var result = await log.VerifyAsync();

        Assert.Equal((2L, AuditProblems.SealMismatch), (result.BrokenAtSeq, result.Problem));
    }

    [Fact]
    public async Task An_entry_that_links_to_the_wrong_predecessor_is_found_even_when_hash_and_seal_hold()
    {
        var log = Open();
        for (var i = 0; i < 3; i++)
        {
            await log.AppendAsync(Record());
        }
        var key = KeyOf();
        var lines = File.ReadAllLines(FileOf());
        lines[1] = Encode(Reseal(Decode(lines[1]) with { Prev = new string('a', 64) }, key)); // someone who holds the key, linking elsewhere
        File.WriteAllLines(FileOf(), lines);

        var result = await log.VerifyAsync();

        Assert.Equal((2L, AuditProblems.PrevMismatch), (result.BrokenAtSeq, result.Problem));
    }

    [Fact]
    public async Task A_line_that_cannot_be_read_is_found()
    {
        var log = Open();
        for (var i = 0; i < 3; i++)
        {
            await log.AppendAsync(Record());
        }
        var lines = File.ReadAllLines(FileOf());
        lines[1] = "not-an-entry";
        File.WriteAllLines(FileOf(), lines);

        var result = await log.VerifyAsync();

        Assert.Equal((2L, AuditProblems.Unreadable), (result.BrokenAtSeq, result.Problem));
        Assert.DoesNotContain("not-an-entry", result.Problem);
    }

    [Fact]
    public async Task A_damaged_last_line_does_not_stop_the_log_and_the_next_verify_reports_it()
    {
        var log = Open();
        await log.AppendAsync(Record());
        File.AppendAllText(FileOf(), "garbage-without-newline");

        var next = await log.AppendAsync(Record()); // still written, on a line of its own

        Assert.Equal(2, next.Seq);
        var result = await log.VerifyAsync();
        Assert.False(result.Intact);
        Assert.Equal(AuditProblems.Unreadable, result.Problem);
    }

    // ---- the sealed head: the end of the log ----

    private async Task AppendMany(IAuditLog log, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await log.AppendAsync(Record());
        }
    }

    private string HeadFile(string name = "log") => Path.Combine(_folder, name, FileAuditLog.HeadName);

    [Fact]
    public async Task A_tail_cut_by_one_entry_or_by_several_is_found()
    {
        var log = Open();
        await AppendMany(log, 5);
        var lines = File.ReadAllLines(FileOf());

        File.WriteAllLines(FileOf(), lines.Take(4));
        var one = await log.VerifyAsync();
        Assert.False(one.Intact);
        Assert.Equal((5L, AuditProblems.HeadMismatch), (one.BrokenAtSeq, one.Problem));

        File.WriteAllLines(FileOf(), lines.Take(2));
        var several = await log.VerifyAsync();
        Assert.Equal((3L, AuditProblems.HeadMismatch), (several.BrokenAtSeq, several.Problem));

        File.WriteAllText(FileOf(), "");
        Assert.Equal(AuditProblems.HeadMismatch, (await log.VerifyAsync()).Problem); // everything gone
    }

    [Fact]
    public async Task A_deleted_head_is_found_and_a_log_from_before_the_head_takes_one_with_its_next_append()
    {
        var log = Open();
        await AppendMany(log, 3);
        File.Delete(HeadFile());

        var missing = await log.VerifyAsync();
        Assert.Equal((3L, AuditProblems.HeadMissing), (missing.BrokenAtSeq, missing.Problem));

        await log.AppendAsync(Record()); // a valid chain without a head: the head is written once, with this entry
        Assert.True((await log.VerifyAsync()).Intact);
        Assert.True(File.Exists(HeadFile()));
    }

    [Fact]
    public async Task A_forged_or_stale_head_is_found()
    {
        var log = Open();
        await AppendMany(log, 3);
        var head = File.ReadAllText(HeadFile());

        File.WriteAllText(HeadFile(), head.Replace("\"seq\":3", "\"seq\":2")); // another number, the old seal
        Assert.Equal(AuditProblems.HeadMismatch, (await log.VerifyAsync()).Problem);

        File.WriteAllText(HeadFile(), "not json");
        Assert.Equal(AuditProblems.HeadMismatch, (await log.VerifyAsync()).Problem);

        File.WriteAllText(HeadFile(), head);
        Assert.True((await log.VerifyAsync()).Intact);
    }

    [Fact]
    public async Task Pruning_leaves_the_head_alone_and_the_head_goes_with_the_log()
    {
        var log = Open();
        await AppendMany(log, 2);
        var head = File.ReadAllText(HeadFile());
        _now = _now.AddDays(400);
        await log.PruneAsync(180);

        Assert.Equal(head, File.ReadAllText(HeadFile()));
        Assert.True((await log.VerifyAsync()).Intact);
    }

    // ---- retention ----

    [Fact]
    public async Task Pruning_removes_the_old_start_and_leaves_a_sealed_anchor_so_the_rest_still_verifies()
    {
        var log = Open();
        for (var i = 0; i < 3; i++)
        {
            await log.AppendAsync(Record());
            _now = _now.AddDays(100);
        }
        // entries at day 0, 100, 200; now is day 300: 180 days keep only the last one
        var removed = await log.PruneAsync(180);

        Assert.Equal(2, removed);
        var result = await log.VerifyAsync();
        Assert.True(result.Intact, result.Problem);
        Assert.Equal((1, 3L), (result.Entries, result.FirstSeq));
        Assert.True(File.Exists(Path.Combine(_folder, "log", FileAuditLog.AnchorName)));
        Assert.Equal(4, (await log.AppendAsync(Record())).Seq); // the chain goes on
        Assert.True((await log.VerifyAsync()).Intact);
    }

    [Fact]
    public async Task Without_its_anchor_or_with_a_changed_one_a_pruned_log_does_not_verify()
    {
        var log = Open();
        for (var i = 0; i < 3; i++)
        {
            await log.AppendAsync(Record());
            _now = _now.AddDays(100);
        }
        await log.PruneAsync(180);
        var anchor = Path.Combine(_folder, "log", FileAuditLog.AnchorName);
        var kept = File.ReadAllText(anchor);

        File.WriteAllText(anchor, kept.Replace("\"seq\":2", "\"seq\":5"));
        Assert.Equal(AuditProblems.AnchorMismatch, (await log.VerifyAsync()).Problem);

        File.Delete(anchor);
        var missing = await log.VerifyAsync();
        Assert.Equal((3L, AuditProblems.AnchorMissing), (missing.BrokenAtSeq, missing.Problem));
    }

    [Fact]
    public async Task Pruning_everything_leaves_an_empty_log_that_carries_on_from_the_anchor()
    {
        var log = Open();
        await log.AppendAsync(Record());
        await log.AppendAsync(Record());
        _now = _now.AddDays(400);

        Assert.Equal(2, await log.PruneAsync(180));

        Assert.True((await log.VerifyAsync()).Intact);
        var next = await log.AppendAsync(Record());
        Assert.Equal(3, next.Seq);
        Assert.True((await log.VerifyAsync()).Intact);
    }

    [Fact]
    public async Task Nothing_old_means_nothing_removed_and_no_anchor()
    {
        var log = Open();
        await log.AppendAsync(Record());

        Assert.Equal(0, await log.PruneAsync(180));
        Assert.False(File.Exists(Path.Combine(_folder, "log", FileAuditLog.AnchorName)));
    }

    // ---- export ----

    [Fact]
    public async Task The_csv_has_the_verification_on_top_and_one_row_per_entry_and_is_safe_in_a_spreadsheet()
    {
        var log = Open();
        await log.AppendAsync(Record("=HYPERLINK(\"x\")") with { Server = "a,b" });
        using var csv = new MemoryStream();

        await log.ExportAsync(AuditExportFormat.Csv, csv);

        var lines = Encoding.UTF8.GetString(csv.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
        Assert.Equal("verification,intact,entries,first_seq,first_time,last_time,broken_at_seq,problem", lines[0]);
        Assert.StartsWith("verification,true,1,1,2026-11-16T09:30:00.000Z,2026-11-16T09:30:00.000Z,,", lines[1]);
        Assert.StartsWith("seq,time,kind,app,agent,chat,turn,tool,class,source,server,decision,scope,outcome,arguments_sha256,prev,hash,seal", lines[3]);
        Assert.Contains("'=HYPERLINK(\"\"x\"\")", lines[4]); // quoted, and not a formula
        Assert.Contains("\"a,b\"", lines[4]);
    }

    [Fact]
    public async Task The_csv_of_a_damaged_log_says_so_at_the_top()
    {
        var log = Open();
        await log.AppendAsync(Record());
        await log.AppendAsync(Record());
        File.WriteAllLines(FileOf(), File.ReadAllLines(FileOf()).Take(1).Concat(new[] { "junk" }));
        using var csv = new MemoryStream();

        await log.ExportAsync(AuditExportFormat.Csv, csv);

        Assert.StartsWith("verification,false,1,1,", Encoding.UTF8.GetString(csv.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None)[1]);
    }

    [Fact]
    public async Task The_html_is_one_page_with_escaped_text_inline_style_no_script_and_no_outside_resource()
    {
        var log = Open();
        await log.AppendAsync(Record("<script>alert(1)</script>") with { Server = "\"><img src=http://evil.test/x>" });
        using var html = new MemoryStream();

        await log.ExportAsync(AuditExportFormat.Html, html);

        var page = Encoding.UTF8.GetString(html.ToArray());
        Assert.StartsWith("<!DOCTYPE html>", page);
        var tags = System.Text.RegularExpressions.Regex.Matches(page, "<(/?[a-zA-Z0-9]+)").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value.TrimStart('/')).ToHashSet();
        Assert.Subset(new HashSet<string> { "html", "head", "meta", "title", "style", "body", "h1", "p", "span", "table", "thead", "tbody", "tr", "th", "td" }, tags);
        Assert.DoesNotContain("@import", page);
        Assert.DoesNotContain("url(", page);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page);
        Assert.Contains("<table>", page);
        Assert.Contains("Intact", page);
    }

    // ---- in memory, and the policy ----

    [Fact]
    public async Task The_memory_log_keeps_the_same_chain_and_checks()
    {
        var log = new MemoryAuditLog(() => _now);
        await log.AppendAsync(Record());
        await log.AppendAsync(Record());
        _now = _now.AddDays(400);

        Assert.True((await log.VerifyAsync()).Intact);
        Assert.Equal(2, await log.PruneAsync(180));
        Assert.Equal(3, (await log.AppendAsync(Record())).Seq);
        Assert.True((await log.VerifyAsync()).Intact);
    }

    [Theory]
    [InlineData("""{"auditRetentionDays":30}""", 30, 0)]
    [InlineData("""{"auditRetentionDays":3650}""", 3650, 0)]
    [InlineData("""{"auditRetentionDays":29}""", 180, 1)]
    [InlineData("""{"auditRetentionDays":3651}""", 180, 1)]
    [InlineData("""{"auditRetentionDays":"90"}""", 180, 1)]
    [InlineData("""{"auditRetentionDays":90.5}""", 180, 1)]
    public void The_machine_policy_sets_the_retention_within_bounds_and_an_odd_value_is_reported(string json, int days, int warnings)
    {
        var policy = MachinePolicy.FromJson(json);

        Assert.Equal(days, policy.AuditRetentionDays);
        Assert.Equal(warnings, policy.Warnings.Count);
        Assert.Contains("auditRetentionDays", policy.Locked);
    }

    [Fact]
    public void Without_a_policy_the_retention_is_not_set_and_the_agent_keeps_180_days()
    {
        Assert.Null(MachinePolicy.FromJson("{}").AuditRetentionDays);
        Assert.Equal(180, AuditRetention.DefaultDays);
    }
}
