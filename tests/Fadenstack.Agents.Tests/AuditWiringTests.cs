using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Fadenstack.Agents.Audit;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Storage;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// Where the agent writes the record of tool calls (sdk/51 8.10): every call's decision and outcome, content-free, in every mode; the
/// hash of the arguments' text in the events and answers only for a server that lists the feature; the record in "Your data on this device".
/// ASVS 5.0 V16 (security logging).
/// </summary>
[Collection(PathsCollection.Name)]
public sealed class AuditWiringTests : IDisposable
{
    private const string Principal = """{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1","device_id":null}""";
    private const string WithHash = """["agents","client_events.arguments_sha256"]""";
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-auditwire-" + Guid.NewGuid().ToString("N"));

    public AuditWiringTests()
    {
        FadenstackPaths.RootForTests = _temporary;
        SecretProtection.ResetForTests();
        SecretProtection.Use(new FakeEncryptingProtector());
    }

    public void Dispose()
    {
        SecretProtection.ResetForTests();
        FadenstackPaths.RootForTests = null;
        if (Directory.Exists(_temporary))
        {
            Directory.Delete(_temporary, true);
        }
    }

    private static string Sha(string text) => AuditChain.ArgumentsSha256(text);

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static AgentBuilder Audited(Gateway gateway, MemoryAuditLog log, Func<ApprovalRequest, ApprovalDecision>? ask = null)
    {
        var builder = gateway.Builder().UseAuditLog(log);
        return ask is null ? builder : builder.UseApprovals((r, _) => Task.FromResult(ask(r)));
    }

    [Fact]
    public async Task A_call_the_mode_runs_is_recorded_with_the_hash_of_the_arguments_text_exactly_as_the_model_wrote_it()
    {
        var text = """{ "page" : 1 }"""; // spaces the SDK's own JSON would not write back
        var gateway = new Gateway().Calls(("call-1", "read_page", text)).Says("Done.");
        var log = new MemoryAuditLog();
        using var agent = Audited(gateway, log).AddTools(new PageTools()).DescribeApp(new AppDescription { Name = "Word add-in", Version = "1" }).Build();

        await (await agent.StartSessionAsync(ToolMode.Auto)).SendAsync("Read.");

        var entry = Assert.Single(log.Entries);
        Assert.Equal(("tool", "Word add-in", Gateway.Slug, "read_page", "read", "host", AuditDecisions.Mode, AuditOutcomes.Ran),
            (entry.Kind, entry.App, entry.Agent, entry.Tool, entry.Class, entry.Source, entry.Decision, entry.Outcome));
        Assert.Null(entry.Scope);
        Assert.Equal(Sha(text), entry.ArgumentsSha256);
        Assert.False(string.IsNullOrEmpty(entry.Chat) || string.IsNullOrEmpty(entry.Turn));
        Assert.True((await log.VerifyAsync()).Intact);
    }

    [Fact]
    public async Task Where_only_parsed_arguments_exist_the_hash_is_of_the_json_the_sdk_writes_back()
    {
        var call = new Microsoft.Extensions.AI.FunctionCallContent("c", "read_page", new Dictionary<string, object?> { ["page"] = 1, ["note"] = "café" });

        Assert.Equal("""{"page":1,"note":"café"}""", Fadenstack.Chat.ChatWire.ArgumentsText(call));
    }

    [Fact]
    public async Task Every_decision_the_loop_makes_is_named()
    {
        var gateway = new Gateway()
            .Calls(("c1", "add_comment", """{"page":1,"text":"first"}""")).Says("1")  // asked, once
            .Calls(("c2", "add_comment", """{"page":1,"text":"second"}""")).Says("2") // asked, declined
            .Calls(("c3", "add_comment", """{"page":1,"text":"third"}""")).Says("3")  // asked, for this chat
            .Calls(("c4", "add_comment", """{"page":1,"text":"fourth"}""")).Says("4") // the chat's approval stands
            .Calls(("c5", "no_such_tool", "{}")).Says("5")
            .Calls(("c6", "read_page", "not json")).Says("6");
        var log = new MemoryAuditLog();
        var answers = new Queue<ApprovalDecision>(new[] { ApprovalDecision.Approve(), ApprovalDecision.Deny("no"), ApprovalDecision.ApproveForSession() });
        using var agent = Audited(gateway, log, _ => answers.Dequeue()).AddTools(new PageTools()).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);

        for (var i = 1; i <= 6; i++)
        {
            await session.SendAsync("Go " + i);
        }

        Assert.Equal(new[]
        {
            ("add_comment", AuditDecisions.AskedApproved, AuditScopes.Once, AuditOutcomes.Ran),
            ("add_comment", AuditDecisions.AskedDenied, (string?)null, AuditOutcomes.Refused),
            ("add_comment", AuditDecisions.AskedApproved, AuditScopes.Chat, AuditOutcomes.Ran),
            ("add_comment", AuditDecisions.SavedApproval, AuditScopes.Chat, AuditOutcomes.Ran),
            ("(unknown)", AuditDecisions.None, (string?)null, AuditOutcomes.NotRun),
            ("read_page", AuditDecisions.None, (string?)null, AuditOutcomes.NotRun), // arguments that are not JSON
        }, log.Entries.Select(e => (e.Tool, e.Decision, e.Scope, e.Outcome)));
        Assert.Equal(new[] { 1L, 2, 3, 4, 5, 6 }, log.Entries.Select(e => e.Seq));
    }

    [Fact]
    public async Task A_call_the_app_cannot_ask_about_is_recorded_as_the_floor_and_a_hidden_tool_as_hidden()
    {
        var gateway = new Gateway()
            .Calls(("c1", "delete_page", """{"page":1}""")).Says("1") // destructive in Ask: asks; no handler: nobody can answer
            .Calls(("c2", "add_comment", """{"page":1,"text":"x"}""")).Says("2"); // hidden by ReadOnly
        var log = new MemoryAuditLog();
        using var agent = Audited(gateway, log).AddTools(new PageTools()).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("Delete.");
        await (await agent.StartSessionAsync(ToolMode.ReadOnly)).SendAsync("Comment.");

        Assert.Equal(new[]
        {
            ("delete_page", AuditDecisions.Floor, AuditOutcomes.Refused),
            ("add_comment", AuditDecisions.Hidden, AuditOutcomes.Refused),
        }, log.Entries.Select(e => (e.Tool, e.Decision, e.Outcome)));
    }

    [Fact]
    public async Task A_kept_always_approval_is_recorded_as_a_saved_approval()
    {
        var store = new MemoryApprovalStore();
        await store.AddAsync(Gateway.Slug, new StoredApproval("add_comment", ToolClass.Write, DateTimeOffset.UtcNow) { Source = "host" });
        var gateway = new Gateway().Calls(("c1", "add_comment", """{"page":1,"text":"x"}""")).Says("1");
        var log = new MemoryAuditLog();
        using var agent = gateway.Builder(approvals: store).UseAuditLog(log).AddTools(new PageTools()).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("Comment.");

        var entry = Assert.Single(log.Entries);
        Assert.Equal((AuditDecisions.SavedApproval, AuditScopes.Always, AuditOutcomes.Ran), (entry.Decision, entry.Scope, entry.Outcome));
    }

    [Fact]
    public async Task An_entry_holds_no_prompt_answer_argument_or_result_text()
    {
        var gateway = new Gateway().Calls(("c1", "add_comment", """{"page":1,"text":"SECRET-ARGUMENT"}""")).Says("SECRET-ANSWER");
        var log = new MemoryAuditLog();
        using var agent = Audited(gateway, log, _ => ApprovalDecision.Approve()).AddTools(new PageTools()).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("SECRET-PROMPT");

        using var csv = new MemoryStream();
        await log.ExportAsync(AuditExportFormat.Csv, csv);
        var all = Encoding.UTF8.GetString(csv.ToArray()) + JsonSerializer.Serialize(log.Entries);
        foreach (var secret in new[] { "SECRET-ARGUMENT", "SECRET-ANSWER", "SECRET-PROMPT", "added" })
        {
            Assert.DoesNotContain(secret, all);
        }
    }

    [Fact]
    public async Task A_log_that_cannot_be_written_does_not_break_the_turn()
    {
        var gateway = new Gateway().Calls(("c1", "read_page", """{"page":1}""")).Says("Done.");
        var tools = new PageTools();
        using var agent = gateway.Builder().UseAuditLog(new BrokenLog()).AddTools(tools).Build();

        var result = await (await agent.StartSessionAsync(ToolMode.Auto)).SendAsync("Read.");

        Assert.Equal("Done.", result.Text);
        Assert.Single(tools.Ran);
    }

    private sealed class BrokenLog : IAuditLog
    {
        public Task<AuditEntry> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default) => throw new IOException("disk full");

        public Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default) => throw new IOException();

        public Task ExportAsync(AuditExportFormat format, Stream destination, CancellationToken cancellationToken = default) => throw new IOException();

        public Task<int> PruneAsync(int retentionDays, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    // ---- the server's events: the hash only for a server that lists the feature ----

    [Fact]
    public async Task With_the_feature_the_events_carry_the_arguments_hash()
    {
        var text = """{"page":1,"text":"x"}""";
        var gateway = new Gateway { Features = WithHash }.Calls(("c1", "add_comment", text)).Says("Done.");
        using var agent = gateway.Builder().UseAuditLog(new MemoryAuditLog()).AddTools(new PageTools())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("Comment.");

        var events = gateway.Reported;
        Assert.Equal(Sha(text), Str(events.Single(e => Str(e, "type") == "tool.executed"), "arguments_sha256"));
        Assert.Equal(Sha(text), Str(events.Single(e => Str(e, "type") == "approval.decided"), "arguments_sha256"));
    }

    [Fact]
    public async Task Without_the_feature_nothing_new_is_sent()
    {
        var gateway = new Gateway().Calls(("c1", "add_comment", """{"page":1,"text":"x"}""")).Says("Done.");
        using var agent = gateway.Builder().UseAuditLog(new MemoryAuditLog()).AddTools(new PageTools())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("Comment.");

        Assert.NotEmpty(gateway.Reported);
        Assert.All(gateway.Reported, e => Assert.False(e.TryGetProperty("arguments_sha256", out _)));
        Assert.DoesNotContain("arguments_sha256", string.Join("", gateway.Reports.Select(r => r.Json.GetRawText())));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_answer_to_a_call_the_server_holds_carries_the_hash_only_with_the_feature(bool feature)
    {
        var text = """{"title":"x"}""";
        var gateway = new Gateway { Features = feature ? WithHash : """["agents"]""" };
        gateway.Then(
            Gateway.Custom("faden.approval_required", new
            {
                approvalId = "ap-1", toolCallId = "gw-1", toolCallName = "mcp.github.comment", message = "The tool mode asks.",
                timeoutS = 300, toolClass = "write", rule = true, arguments = text,
            }),
            Gateway.Text("Done."), Gateway.Finish("stop"), FakeServer.Done);
        gateway.Server.Json("POST", "/v1/agents/docs-assistant/approvals/ap-1", new { approval_id = "ap-1", approved = true });
        var log = new MemoryAuditLog();
        using var agent = gateway.Builder().UseAuditLog(log).UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve())).Build();

        await (await agent.StartSessionAsync(ToolMode.Ask)).SendAsync("Comment.");

        var answer = Assert.Single(gateway.Server.Requests, r => r.Path == "/v1/agents/docs-assistant/approvals/ap-1");
        Assert.Equal(feature ? Sha(text) : null, Str(answer.Json, "arguments_sha256"));
        var entry = Assert.Single(log.Entries); // the record keeps it either way, as far as the SDK knows it
        Assert.Equal(("mcp.github.comment", "gateway", AuditDecisions.AskedApproved), (entry.Tool, entry.Source, entry.Decision));
        Assert.Equal(Sha(text), entry.ArgumentsSha256);
    }

    // ---- retention ----

    [Fact]
    public async Task The_start_removes_entries_older_than_180_days_or_than_the_policys_days()
    {
        async Task<int> Remaining(string? policy)
        {
            var log = new MemoryAuditLog();
            await log.AppendAsync(new AuditRecord { Tool = "t", Class = "read", Time = DateTimeOffset.UtcNow.AddDays(-200), Outcome = AuditOutcomes.Ran });
            await log.AppendAsync(new AuditRecord { Tool = "t", Class = "read", Time = DateTimeOffset.UtcNow.AddDays(-10), Outcome = AuditOutcomes.Ran });
            var builder = new Gateway().Builder().UseAuditLog(log);
            if (policy is not null)
            {
                builder.UseMachinePolicy(MachinePolicy.FromJson(policy));
            }
            using var agent = builder.Build();
            await agent.StartAsync();
            Assert.True((await log.VerifyAsync()).Intact);
            return log.Entries.Count;
        }

        Assert.Equal(1, await Remaining(null));
        Assert.Equal(1, await Remaining("""{"auditRetentionDays":30}"""));
        Assert.Equal(2, await Remaining("""{"auditRetentionDays":365}"""));
    }

    // ---- on the device: the person's folder, "Your data on this device" ----

    private static FadenstackAgent Device(string name, string slug = Gateway.Slug, Gateway? gateway = null) => FadenstackAgent.CreateBuilder()
        .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"),
            o => o.HttpClient = new HttpClient((gateway ?? new Gateway { Principal = Principal }).Server))
        .UseAgent(slug)
        .DescribeApp(new AppDescription { Name = name, Version = "1.0" })
        .AddTools(new PageTools())
        .Build();

    private static string AuditFile => Path.Combine(FadenstackPaths.UserFolder("u-1"), "audit", FileAuditLog.FileName);

    private static async Task RunOne(FadenstackAgent agent, Gateway gateway)
    {
        gateway.Calls(("c1", "read_page", """{"page":1}""")).Says("Done.");
        await (await agent.StartSessionAsync(ToolMode.Auto)).SendAsync("Read.");
    }

    [Fact]
    public async Task By_default_the_log_is_an_encrypted_file_in_the_persons_own_folder_and_two_apps_write_into_the_same_chain()
    {
        var gatewayA = new Gateway { Principal = Principal };
        var gatewayB = new Gateway { Principal = Principal };
        using var word = Device("Word add-in", gateway: gatewayA);
        using var excel = Device("Excel add-in", gateway: gatewayB);

        await RunOne(word, gatewayA);
        await RunOne(excel, gatewayB);
        await RunOne(word, gatewayA);

        Assert.True(File.Exists(AuditFile));
        Assert.DoesNotContain("read_page", File.ReadAllText(AuditFile));
        var result = await word.VerifyAuditAsync();
        Assert.True(result.Intact, result.Problem);
        Assert.Equal(3, result.Entries);
        using var csv = new MemoryStream();
        await excel.ExportAuditAsync(AuditExportFormat.Csv, csv);
        var text = Encoding.UTF8.GetString(csv.ToArray());
        Assert.Contains("Word add-in", text);
        Assert.Contains("Excel add-in", text);
    }

    [Fact]
    public async Task The_export_of_all_data_holds_the_record_as_a_csv_with_its_verification()
    {
        var gateway = new Gateway { Principal = Principal };
        using var agent = Device("Word add-in", gateway: gateway);
        await RunOne(agent, gateway);
        using var zip = new MemoryStream();

        await agent.ExportMyDataAsync(zip, DataScope.AllApps);

        using var archive = new ZipArchive(new MemoryStream(zip.ToArray()), ZipArchiveMode.Read);
        var csv = new StreamReader(archive.GetEntry("audit/tool-calls.csv")!.Open()).ReadToEnd();
        Assert.StartsWith("verification,intact,entries,", csv);
        Assert.Contains("verification,true,1,", csv);
        Assert.Contains("read_page", csv);
        Assert.DoesNotContain(archive.Entries, e => e.FullName.StartsWith("audit/") && e.FullName != "audit/tool-calls.csv"); // never the raw lines
        var readme = new StreamReader(archive.GetEntry("README.txt")!.Open()).ReadToEnd();
        Assert.Contains("audit/tool-calls.csv", readme);
    }

    [Fact]
    public async Task The_record_is_shared_data_it_stays_while_another_app_uses_the_device_and_goes_with_remove_all_or_the_last_app()
    {
        var gateway = new Gateway { Principal = Principal };
        using var word = Device("Word add-in", gateway: gateway);
        await RunOne(word, gateway);
        MyDataApps.TryRecord(FadenstackPaths.UserFolder("u-1"), "Desktop agent", "desktop-assistant", DateTimeOffset.UtcNow); // another app

        var kept = await word.DeleteMyDataAsync(DataScope.ThisApp);
        Assert.True(kept.SharedDataKept);
        Assert.True(File.Exists(AuditFile));
        using var zip = new MemoryStream();
        await word.ExportMyDataAsync(zip, DataScope.ThisApp);
        using (var archive = new ZipArchive(new MemoryStream(zip.ToArray()), ZipArchiveMode.Read))
        {
            Assert.Null(archive.GetEntry("audit/tool-calls.csv")); // not this app's: it is named in the README as what stayed
            Assert.Contains("record of tool calls", new StreamReader(archive.GetEntry("README.txt")!.Open()).ReadToEnd());
        }

        await word.DeleteMyDataAsync(DataScope.AllApps);
        Assert.False(File.Exists(AuditFile));
        Assert.False(Directory.Exists(Path.Combine(FadenstackPaths.UserFolder("u-1"), "audit")));
    }

    [Fact]
    public async Task The_last_app_takes_the_record_with_it_and_a_server_without_a_user_keeps_it_in_the_data_roots_audit_folder()
    {
        var gateway = new Gateway { Principal = Principal };
        using var word = Device("Word add-in", gateway: gateway);
        await RunOne(word, gateway);
        Assert.False((await word.DeleteMyDataAsync(DataScope.ThisApp)).SharedDataKept);
        Assert.False(File.Exists(AuditFile));

        var anonymous = new Gateway();
        using var nobody = Device("Word add-in", gateway: anonymous);
        await RunOne(nobody, anonymous);
        var shared = Path.Combine(FadenstackPaths.DataRoot, "audit", FileAuditLog.FileName);
        Assert.True(File.Exists(shared));
        await nobody.DeleteMyDataAsync(DataScope.ThisApp);
        Assert.True(File.Exists(shared)); // the device's, shared by every agent on it
        await nobody.DeleteMyDataAsync(DataScope.AllApps);
        Assert.False(File.Exists(shared));
    }
}
