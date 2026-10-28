using System.Text.Json;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Agents.Tests;
using Fadenstack.Governance;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Provenance.Tests;

/// <summary>Content a tool places goes through the pipeline: signed, marked where the host can carry it, reported.</summary>
public class PlacementTests
{
    private static string Args(object value) => JsonSerializer.Serialize(value);

    /// <summary>A gateway for an English-speaking user, so the marks' words do not follow the machine's language.</summary>
    private static Gateway NewGateway(string? policy = """{"on_missing":"record","short_text_tokens":200}""") =>
        new(contract: WritingTools.Contract, userLanguage: "en", provenancePolicy: policy);

    private static List<JsonElement> Entries(AgentSession session, string type) =>
        session.Transcript.Where(e => e.Type == type).Select(e => e.Data).ToList();

    private static async Task<(AgentSession Session, AgentResult Result)> RunAsync(FadenstackAgent agent, string message = "Answer the customer.")
    {
        var session = await agent.StartSessionAsync(ToolMode.Auto);
        var result = await session.SendAsync(message);
        return (session, result);
    }

    [Fact]
    public async Task Html_written_into_an_editor_is_signed_marked_read_back_and_reported_without_content()
    {
        var text = "<p>" + TestAgents.LongText + "</p>";
        var gateway = NewGateway()
            .SaysAndCalls("Here is a reply.", Gateway.Receipt("rc-1"), ("call-1", "insert_reply", Args(new { text })))
            .Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        var (session, result) = await RunAsync(agent);

        var request = Assert.Single(gateway.RecordRequests).Json;
        Assert.Equal((ContentHash.TextSha256(text), ContentHash.TextSha256(TestAgents.LongText), "text/html", "generated", "rc-1", "qwen3.8-27b"),
            (request.GetProperty("content_sha256").GetString(), request.GetProperty("text_sha256").GetString(), request.GetProperty("media_type").GetString(),
             request.GetProperty("edit_kind").GetString(), request.GetProperty("receipt_id").GetString(), request.GetProperty("model").GetString()));
        const string id = "5a0e2b5c-7f3e-4a51-9a40-000000000001";
        // One paragraph is marked inside it.
        Assert.StartsWith($"<p><span data-ai-generated=\"{id}\" title=\"AI-generated {id}\">The pump", tools.Editor.Html);
        Assert.EndsWith("</span></p>", tools.Editor.Html);
        Assert.Equal(new[] { new FoundMark(id, "html-attribute"), new FoundMark(id, ProvenanceReader.HtmlTitle) },
            ProvenanceReader.Read(tools.Editor.Html, "text/html"));

        var report = Assert.Single(gateway.Placements);
        Assert.DoesNotContain("pump", report.Body); // never content
        var layers = report.Json.GetProperty("layers").EnumerateArray()
            .Select(l => (l.GetProperty("layer").GetString(), l.GetProperty("method").GetString(), l.GetProperty("applied").GetBoolean())).ToList();
        Assert.Contains(("metadata", "signed-record", true), layers);
        Assert.Contains(("metadata", "html-attribute", true), layers);
        Assert.Contains(("watermark", "vllm-gumbel", true), layers);
        Assert.Equal((id, "insert_reply", "html.editor", "medium"), (report.Json.GetProperty("record_id").GetString(),
            report.Json.GetProperty("tool").GetString(), report.Json.GetProperty("sink_kind").GetString(), report.Json.GetProperty("length_class").GetString()));
        Assert.False(report.Json.TryGetProperty("written", out _) || report.Json.TryGetProperty("failure", out _)); // written: the default

        var reported = report.Json.GetProperty("layers").EnumerateArray().ToList();
        Assert.True(reported.Single(l => l.GetProperty("method").GetString() == "html-attribute").GetProperty("verified").GetBoolean());
        Assert.False(reported.Single(l => l.GetProperty("method").GetString() == "signed-record").TryGetProperty("verified", out _)); // nothing read back

        var placed = Assert.Single(Entries(session, ProvenanceEntries.Placed));
        Assert.Equal((id, "rc-1", false, true, true, "text"), (placed.GetProperty("record_id").GetString(), placed.GetProperty("receipt_id").GetString(),
            placed.GetProperty("exempt").GetBoolean(), placed.GetProperty("signed").GetBoolean(), placed.GetProperty("reported").GetBoolean(),
            placed.GetProperty("argument").GetString()));
        Assert.True(placed.GetProperty("layers").EnumerateArray().Single(l => l.GetProperty("method").GetString() == "html-attribute").GetProperty("verified").GetBoolean());
        Assert.Empty(placed.GetProperty("missing").EnumerateArray());
        Assert.Empty(Entries(session, ProvenanceEntries.Unmarked));
        Assert.Equal(ChatOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task Gmail_drops_the_data_attribute_and_the_title_holds_the_mark()
    {
        var gateway = NewGateway().SaysAndCalls("Reply:", Gateway.Receipt("rc-1"), ("call-1", "insert_reply", Args(new { text = "Thank you, we will call you on Monday." }))).Says("Done.");
        var tools = new WritingTools();
        tools.Editor.Keep = html => html.Replace(" data-ai-generated=\"5a0e2b5c-7f3e-4a51-9a40-000000000001\"", "");
        using var agent = TestAgents.Build(gateway, tools);

        var (session, _) = await RunAsync(agent);

        var placed = Assert.Single(Entries(session, ProvenanceEntries.Placed));
        var html = placed.GetProperty("layers").EnumerateArray().Single(l => l.GetProperty("layer").GetString() == "metadata"
            && l.GetProperty("method").GetString() != "signed-record");
        Assert.Equal(("html-title", true, "title; data-ai-generated dropped"), (html.GetProperty("method").GetString(), html.GetProperty("applied").GetBoolean(),
            html.GetProperty("detail").GetString()));
        var watermark = placed.GetProperty("layers").EnumerateArray().Single(l => l.GetProperty("layer").GetString() == "watermark");
        Assert.Equal("short-text", watermark.GetProperty("reason").GetString()); // short text: metadata, but no watermark expected
        Assert.Equal("short", placed.GetProperty("length_class").GetString());
    }

    [Fact]
    public async Task An_editor_that_keeps_neither_mark_is_recorded_as_stripped()
    {
        var gateway = NewGateway().SaysAndCalls("Reply:", Gateway.Receipt("rc-1"), ("call-1", "insert_reply", Args(new { text = "See you on Monday at the plant." }))).Says("Done.");
        var tools = new WritingTools();
        tools.Editor.Keep = html => System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "");
        using var agent = TestAgents.Build(gateway, tools);

        var (session, _) = await RunAsync(agent);

        var missing = Assert.Single(Assert.Single(Entries(session, ProvenanceEntries.Placed)).GetProperty("missing").EnumerateArray());
        Assert.Equal(("metadata", "html-attribute", "stripped-by-host"), (missing.GetProperty("layer").GetString(), missing.GetProperty("method").GetString(),
            missing.GetProperty("reason").GetString()));
        Assert.Equal("See you on Monday at the plant.", tools.Editor.Html); // placed, with the policy record
    }

    [Fact]
    public async Task A_crm_note_keeps_the_record_in_its_hidden_field()
    {
        var gateway = NewGateway().SaysAndCalls("Noting it.", Gateway.Receipt("rc-2"), ("call-1", "add_crm_note", Args(new { note = TestAgents.LongText }))).Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        await RunAsync(agent);

        var placement = Assert.Single(tools.Placed);
        Assert.Equal(new Dictionary<string, string> { ["ai-generated"] = "true", ["ai-record-id"] = placement.RecordId! }, tools.Crm.Fields);
        Assert.Equal(TestAgents.LongText, Assert.Single(tools.Crm.Notes)); // the note itself is unchanged
        Assert.Contains(placement.Layers, l => l.Method == "key-value" && l.Verified == true);
        Assert.Empty(placement.Missing);
        Assert.Equal($"Placed and marked as AI-generated (record {placement.RecordId}).", placement.Summary);
    }

    [Fact]
    public async Task A_translation_is_inserted_as_it_is_and_recorded_as_exempt_without_a_record()
    {
        var gateway = NewGateway().CallsWithReceipt(Gateway.Receipt("rc-3"), ("call-1", "translate_field", Args(new { text = "Die Pumpe ist nächste Woche fällig. Ein Techniker ruft an." }))).Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        var (session, _) = await RunAsync(agent);

        Assert.Empty(gateway.RecordRequests);
        Assert.Equal("Die Pumpe ist nächste Woche fällig. Ein Techniker ruft an.", Assert.Single(tools.Plain));
        var placement = Assert.Single(tools.Placed);
        Assert.Equal((true, "edit-kind", EditKind.Translated, (string?)null), (placement.Exempt, placement.ExemptBecause, placement.EditKind, placement.RecordId));
        var report = Assert.Single(gateway.Placements).Json;
        Assert.False(report.TryGetProperty("record_id", out _)); // no record: none named
        var layer = Assert.Single(report.GetProperty("layers").EnumerateArray());
        Assert.Equal(("metadata", "record", false, "exempt-edit"), (layer.GetProperty("layer").GetString(), layer.GetProperty("method").GetString(),
            layer.GetProperty("applied").GetBoolean(), layer.GetProperty("reason").GetString()));
        var placed = Assert.Single(Entries(session, ProvenanceEntries.Placed));
        Assert.Equal(("translated", true, "edit-kind"), (placed.GetProperty("edit_kind").GetString(), placed.GetProperty("exempt").GetBoolean(),
            placed.GetProperty("exempt_because").GetString()));
    }

    [Fact]
    public async Task Code_and_short_labels_need_no_mark()
    {
        var gateway = NewGateway()
            .Calls(("call-1", "write_config", Args(new { json = """{"timeout": 30}""" })), ("call-2", "chat_message", Args(new { text = "Approved" })))
            .Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        await RunAsync(agent);

        Assert.Equal(new[] { "code", "label" }, tools.Placed.Select(p => p.ExemptBecause));
        Assert.Empty(gateway.RecordRequests);
    }

    [Fact]
    public async Task A_place_that_carries_nothing_gets_the_record_and_says_the_sink_cannot_carry_a_mark()
    {
        var gateway = NewGateway().SaysAndCalls("Posting.", Gateway.Receipt("rc-4"), ("call-1", "chat_message", Args(new { text = TestAgents.LongText }))).Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        await RunAsync(agent);

        var placement = Assert.Single(tools.Placed);
        Assert.NotNull(placement.RecordId);
        Assert.Contains(placement.Layers, l => l is { Layer: MarkLayer.Metadata, Method: "host", Applied: false, Reason: ProvenanceReasons.SinkCannotCarry });
        Assert.Empty(placement.Missing); // free-form text needs the watermark only, and it has one
        Assert.Equal(TestAgents.LongText, Assert.Single(tools.Plain));
    }

    [Fact]
    public async Task With_the_policy_refuse_content_that_cannot_get_a_layer_it_needs_is_not_written_and_both_are_told()
    {
        var gateway = NewGateway("""{"on_missing":"refuse"}""")
            .SaysAndCalls("Posting.", Gateway.Receipt("rc-5", applied: "none", reason: "runtime-without-watermark"),
                ("call-1", "chat_message", Args(new { text = TestAgents.LongText })))
            .Says("I could not post it.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        var (session, result) = await RunAsync(agent);

        Assert.Empty(tools.Plain); // nothing written
        var notice = Assert.Single(result.Notices);
        Assert.Equal(("provenance_refused", "tool"), (notice.Outcome, notice.Stage));
        Assert.Contains("the model's runtime does not watermark text", notice.Message);
        var completed = session.Transcript.Single(e => e.Type == EntryTypes.ToolComplete).Data;
        Assert.Equal("error", completed.GetProperty("outcome").GetString());
        Assert.Equal("Error: Not written: this agent only places content it can mark as AI-generated, and here the model's runtime does not watermark text.",
            completed.GetProperty("result").GetString());
        var refused = Assert.Single(Entries(session, ProvenanceEntries.Refused));
        var missing = Assert.Single(refused.GetProperty("missing").EnumerateArray());
        Assert.Equal(("watermark", "model", "runtime-without-watermark"), (missing.GetProperty("layer").GetString(), missing.GetProperty("method").GetString(),
            missing.GetProperty("reason").GetString()));
        Assert.Empty(Entries(session, ProvenanceEntries.Placed));
        Assert.Empty(gateway.Placements); // nothing placed: nothing reported
        Assert.Empty(gateway.RecordRequests); // refused before anything was signed
    }

    [Fact]
    public async Task With_the_default_policy_the_same_content_is_written_and_the_missing_watermark_recorded()
    {
        var gateway = NewGateway()
            .SaysAndCalls("Posting.", Gateway.Receipt("rc-5", applied: "none", reason: "runtime-without-watermark"),
                ("call-1", "chat_message", Args(new { text = TestAgents.LongText })))
            .Says("Posted.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        var (_, result) = await RunAsync(agent);

        Assert.Single(tools.Plain);
        Assert.Empty(result.Notices);
        var missing = Assert.Single(Assert.Single(tools.Placed).Missing);
        Assert.Equal((MarkLayer.Watermark, ProvenanceReasons.RuntimeWithoutWatermark), (missing.Layer, missing.Reason));
    }

    [Fact]
    public async Task A_container_that_carries_nothing_is_refused_under_refuse_even_for_short_text()
    {
        var gateway = NewGateway("""{"on_missing":"refuse"}""")
            .SaysAndCalls("Inserting.", Gateway.Receipt("rc-6"), ("call-1", "insert_reply", Args(new { text = "See you on Monday, then." })))
            .Says("Done.");
        var tools = new WritingTools();
        using var agent = gateway.Builder().AddTools(new SinkLessEditor()).Use(new ProvenanceModule()).Build();

        var (_, result) = await RunAsync(agent);

        Assert.Contains("this place cannot carry a machine-readable mark", Assert.Single(result.Notices).Message);
    }

    [Fact]
    public async Task With_the_policy_refuse_content_whose_record_cannot_be_signed_is_not_written()
    {
        var gateway = NewGateway("""{"on_missing":"refuse"}""")
            .SaysAndCalls("Reply:", Gateway.Receipt("rc-7"), ("call-1", "insert_reply", Args(new { text = TestAgents.LongText })))
            .Says("I could not insert it.");
        gateway.ProvenanceDown = true; // receipts were signed; the signer is down now
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        var (session, result) = await RunAsync(agent);

        Assert.Equal("", tools.Editor.Html);
        Assert.Equal("Not written: this agent only places content it can mark as AI-generated, and here the provenance service could not sign a record of it.",
            Assert.Single(result.Notices).Message);
        var missing = Assert.Single(Assert.Single(Entries(session, ProvenanceEntries.Refused)).GetProperty("missing").EnumerateArray());
        Assert.Equal(("metadata", "signed-record", "server-unavailable"), (missing.GetProperty("layer").GetString(), missing.GetProperty("method").GetString(),
            missing.GetProperty("reason").GetString()));
        Assert.Empty(gateway.Placements);
    }

    /// <summary>An editor whose sink says it is a document and carries nothing.</summary>
    public sealed class SinkLessEditor
    {
        [AgentTool(ToolClass.Write), Emits("text", "text/html")]
        public Task<string> InsertReplyAsync(string text, IProvenance provenance, CancellationToken cancellationToken) =>
            provenance.PlaceAsync(new MarkableContent(text, "text/html", new SinkInfo("doc.range") { Container = true }),
                _ => Task.FromResult("Inserted."), cancellationToken);
    }

    [Fact]
    public async Task When_the_provenance_service_is_down_the_content_is_placed_unmarked_and_that_is_recorded()
    {
        var gateway = NewGateway().SaysAndCalls("Reply:", Gateway.Unsigned, ("call-1", "insert_reply", Args(new { text = TestAgents.LongText }))).Says("Done.");
        gateway.ProvenanceDown = true;
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        var (session, _) = await RunAsync(agent);

        Assert.Equal(TestAgents.LongText, tools.Editor.Html); // no record id to carry: no mark
        var placed = Assert.Single(Entries(session, ProvenanceEntries.Placed));
        var reasons = placed.GetProperty("missing").EnumerateArray()
            .Select(m => (m.GetProperty("layer").GetString()!, m.GetProperty("method").GetString()!, m.GetProperty("reason").GetString()!)).ToList();
        Assert.Equal(new[] { ("metadata", "signed-record", "server-unavailable"), ("watermark", "model", "server-unavailable") }, reasons);
        Assert.False(placed.TryGetProperty("record_id", out _)); // no record: none named
        Assert.False(placed.GetProperty("signed").GetBoolean());
        var layers = placed.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("method").GetString()).ToList();
        Assert.Equal(new[] { "signed-record", "model" }, layers); // no marker ran
    }

    [Fact]
    public async Task A_tool_that_declares_content_and_does_not_place_it_is_recorded_and_in_development_mode_fails()
    {
        var gateway = NewGateway().Calls(("call-1", "forget_to_mark", Args(new { text = "Hello." }))).Says("Done.")
            .Calls(("call-2", "forget_to_mark", Args(new { text = "Hello." }))).Says("Done.");
        var tools = new WritingTools();
        using var quiet = TestAgents.Build(gateway, tools, o => o.UseDevelopmentMode(false));
        var (session, _) = await RunAsync(quiet);

        var unmarked = Assert.Single(Entries(session, ProvenanceEntries.Unmarked));
        Assert.Equal(("forget_to_mark", "text", "generated", "short", true), (unmarked.GetProperty("tool").GetString(), unmarked.GetProperty("argument").GetString(),
            unmarked.GetProperty("edit_kind").GetString(), unmarked.GetProperty("length_class").GetString(), unmarked.GetProperty("reported").GetBoolean()));
        Assert.Equal("ok", session.Transcript.Single(e => e.Type == EntryTypes.ToolComplete).Data.GetProperty("outcome").GetString());
        var report = Assert.Single(gateway.Placements).Json; // reported without a record
        Assert.Equal(new[] { "edit_kind", "layers", "length_class", "media_type", "sink_kind", "tool" },
            report.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(("unmarked", 0), (report.GetProperty("sink_kind").GetString(), report.GetProperty("layers").GetArrayLength()));

        using var strict = TestAgents.Build(gateway, tools, o => o.UseDevelopmentMode());
        var (strictSession, _) = await RunAsync(strict);
        var completed = strictSession.Transcript.Single(e => e.Type == EntryTypes.ToolComplete).Data;
        Assert.Equal("error", completed.GetProperty("outcome").GetString());
        Assert.Contains("did not place it through IProvenance.PlaceAsync", completed.GetProperty("result").GetString());
    }

    [Fact]
    public async Task An_agent_that_requires_the_module_starts_with_it_and_not_without()
    {
        var gateway = new Gateway(contract: WritingTools.Contract, modules: """["provenance"]""");
        using var with = TestAgents.Build(gateway, new WritingTools());
        await with.StartAsync();

        using var without = gateway.Builder().AddTools(new WritingTools()).Build();
        var e = await Assert.ThrowsAsync<FadenstackException>(() => without.StartAsync());
        Assert.Contains("provenance", e.Message);
    }

    [Fact]
    public async Task A_tool_call_gets_the_receipt_of_the_answer_it_came_from_never_an_earlier_one()
    {
        var gateway = NewGateway()
            .SaysAndCalls("First.", Gateway.Receipt("rc-first"), ("call-1", "add_crm_note", Args(new { note = TestAgents.LongText })))
            .Calls(("call-2", "add_crm_note", Args(new { note = TestAgents.LongText + " Again." }))) // tool calls only: no receipt
            .Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        await RunAsync(agent);

        var receipts = gateway.RecordRequests.Select(r => r.Json.TryGetProperty("receipt_id", out var v) ? v.GetString() : null).ToList();
        Assert.Equal(new[] { "rc-first", null }, receipts);
        var second = tools.Placed[1].Layers.Single(l => l.Layer == MarkLayer.Watermark);
        Assert.Equal((false, (string?)null, "no receipt"), (second.Applied, second.Reason, second.Detail));
    }

    private sealed class MatterEnricher : IRecordEnricher
    {
        public string Namespace => "com.example";

        public IReadOnlyDictionary<string, string> Enrich(EnrichmentContext context) => new Dictionary<string, string>
        {
            ["com.example.matter"] = "M-2026-0042",
            ["com.example.owner"] = "erika.mustermann@example.com",
            ["org.other.key"] = "x",
        };
    }

    private sealed class FakeNamespace : IRecordEnricher
    {
        public string Namespace => "c2pa.actions";

        public IReadOnlyDictionary<string, string> Enrich(EnrichmentContext context) => new Dictionary<string, string> { ["c2pa.actions.x"] = "y" };
    }

    private sealed class Observer : IPlacementObserver
    {
        public List<PlacementReport> Seen { get; } = new();

        public Task OnPlacedAsync(PlacementReport report, Placement placement, CancellationToken cancellationToken)
        {
            Seen.Add(report);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Enrichers_add_non_personal_facts_under_their_namespace_and_observers_see_each_report()
    {
        var gateway = NewGateway().SaysAndCalls("Noting it.", Gateway.Receipt("rc-7"), ("call-1", "add_crm_note", Args(new { note = TestAgents.LongText }))).Says("Done.");
        var tools = new WritingTools();
        var observer = new Observer();
        using var agent = TestAgents.Build(gateway, tools, o => o.AddEnricher(new MatterEnricher()).AddEnricher(new FakeNamespace()).AddObserver(observer));

        var (session, _) = await RunAsync(agent);

        var placed = Assert.Single(Entries(session, ProvenanceEntries.Placed));
        Assert.Equal("M-2026-0042", placed.GetProperty("facts").GetProperty("com.example.matter").GetString());
        var dropped = placed.GetProperty("dropped_facts").EnumerateArray().Select(d => d.GetString()!).ToList();
        Assert.Contains(dropped, d => d.StartsWith("com.example.owner: looks like personal data"));
        Assert.Contains(dropped, d => d.StartsWith("org.other.key: outside com.example"));
        Assert.Contains(dropped, d => d.StartsWith("c2pa.actions: not a namespace"));
        Assert.Equal(Assert.Single(tools.Placed).RecordId, Assert.Single(observer.Seen).RecordId);
        Assert.DoesNotContain("M-2026-0042", Assert.Single(gateway.Placements).Body); // facts stay on the device
    }

    [Fact]
    public async Task A_light_correction_is_recorded_as_exempt_and_a_rewrite_is_marked()
    {
        var original = "Ths pump is due for its seal chek next week and a technician will call you to agree on a time.";
        var corrected = "This pump is due for its seal check next week and a technician will call you to agree on a time.";
        var rewritten = "Next week we will service the pump. Our technician phones you first.";
        var gateway = NewGateway()
            .Calls(("call-1", "improve_text", Args(new { original, text = corrected })), ("call-2", "improve_text", Args(new { original, text = rewritten })))
            .Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools, o => o.UseClassifier(new SimilarityEditClassifier()));

        var (session, _) = await RunAsync(agent);

        Assert.Equal(new[] { (EditKind.Corrected, true), (EditKind.Rewritten, false) }, tools.Placed.Select(p => (p.EditKind, p.Exempt)));
        var classified = Entries(session, ProvenanceEntries.Placed)[0].GetProperty("classified");
        Assert.Equal(("rewritten", "corrected"), (classified.GetProperty("from").GetString(), classified.GetProperty("to").GetString()));
        Assert.StartsWith("small change:", classified.GetProperty("reason").GetString());
        Assert.Single(gateway.RecordRequests); // only the rewrite is signed
    }

    [Fact]
    public async Task Local_options_only_tighten_the_agents_policy()
    {
        var gateway = NewGateway("""{"on_missing":"record","short_text_tokens":200,"exempt_edit_kinds":["translated","corrected"]}""");
        var tools = new WritingTools();
        gateway.CallsWithReceipt(Gateway.Receipt("rc-8"), ("call-1", "translate_field", Args(new { text = "Die Pumpe ist fällig, und ein Techniker ruft an." }))).Says("Done.");
        using var agent = TestAgents.Build(gateway, tools, o => o.RefuseWhenMissing().UseShortTextTokens(500).MarkEditKind(EditKind.Translated));

        await RunAsync(agent);

        var placement = Assert.Single(tools.Placed);
        Assert.False(placement.Exempt); // translations are marked here
        Assert.False(placement.Refused); // a field that carries nothing, short text: nothing it needs is missing
        var policy = new ProvenanceOptions().RefuseWhenMissing().UseShortTextTokens(500).MarkEditKind(EditKind.Translated)
            .Tighten(new ProvenancePolicy { ShortTextTokens = 200 });
        Assert.Equal((MissingLayerPolicy.Refuse, 200), (policy.OnMissing, policy.ShortTextTokens)); // 500 would loosen: ignored
        Assert.DoesNotContain(EditKind.Translated, policy.ExemptEditKinds);
        Assert.Equal(100, new ProvenanceOptions().UseShortTextTokens(100).Tighten(new ProvenancePolicy()).ShortTextTokens);
    }

    [Fact]
    public void An_option_that_would_loosen_the_policy_is_logged_under_the_modules_category()
    {
        var logs = new Fadenstack.Client.Tests.CapturingLoggerFactory();

        new ProvenanceOptions().UseShortTextTokens(500).Tighten(new ProvenancePolicy { ShortTextTokens = 200 }, logs.CreateLogger(Log.Category));

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(("Fadenstack.Provenance", 4001, Microsoft.Extensions.Logging.LogLevel.Warning), (entry.Category, entry.EventId, entry.Level));
        Assert.Contains("500", entry.Message);
    }

    [Fact]
    public async Task With_the_visible_label_on_a_label_goes_above_the_content_in_the_users_language()
    {
        var gateway = new Gateway(contract: WritingTools.Contract, userLanguage: "de", provenancePolicy: """{"visible_label":true}""")
            .SaysAndCalls("Antwort:", Gateway.Receipt("rc-9"), ("call-1", "insert_reply", Args(new { text = "<p>Wir rufen Sie am Montag an.</p>" })))
            .Says("Erledigt.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        await RunAsync(agent);

        const string id = "5a0e2b5c-7f3e-4a51-9a40-000000000001";
        Assert.StartsWith($"<p data-ai-label=\"{id}\">KI-generiert</p><p><span data-ai-generated=\"{id}\" title=\"KI-generiert {id}\">", tools.Editor.Html);
    }

    [Fact]
    public async Task A_record_of_html_is_found_by_the_words_a_reader_copies_and_plain_text_sends_no_text_hash()
    {
        var html = $"<p>Dear Ms <b>Huber</b>,</p><p>{TestAgents.LongText}</p>";
        var copied = "Dear Ms Huber, " + TestAgents.LongText;
        var gateway = NewGateway()
            .SaysAndCalls("Reply:", Gateway.Receipt("rc-1"), ("call-1", "insert_reply", Args(new { text = html })), ("call-2", "chat_message", Args(new { text = copied })))
            .Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        await RunAsync(agent);

        var requests = gateway.RecordRequests.Select(r => r.Json).ToList();
        Assert.Equal(ContentHash.TextSha256(copied), requests[0].GetProperty("text_sha256").GetString());
        Assert.False(requests[1].TryGetProperty("text_sha256", out _)); // plain text is what a reader sees
        var found = await agent.Client.Agent!.Provenance.VerifyAsync(new VerifyRequest { Text = copied });
        Assert.Equal("5a0e2b5c-7f3e-4a51-9a40-000000000001", Assert.Single(found.Findings).RecordId);
    }

    [Fact]
    public async Task An_insert_that_fails_after_the_record_was_signed_is_reported_with_no_layer_applied_and_fails_the_call()
    {
        var gateway = NewGateway()
            .SaysAndCalls("Inserting.", Gateway.Receipt("rc-1"), ("call-1", "insert_into_gone_field", Args(new { text = "<p>" + TestAgents.LongText + "</p>" })))
            .Says("The field is gone.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        var (session, _) = await RunAsync(agent);

        var completed = session.Transcript.Single(e => e.Type == EntryTypes.ToolComplete).Data;
        Assert.Equal("error", completed.GetProperty("outcome").GetString());
        Assert.Contains("The field is gone.", completed.GetProperty("result").GetString());
        var report = Assert.Single(gateway.Placements).Json;
        Assert.Equal("5a0e2b5c-7f3e-4a51-9a40-000000000001", report.GetProperty("record_id").GetString());
        // Not written: the engine marks the record so. The exception's name only, never its message.
        Assert.Equal((false, "InvalidOperationException"), (report.GetProperty("written").GetBoolean(), report.GetProperty("failure").GetString()));
        Assert.DoesNotContain("field is gone", report.GetRawText());
        var layers = report.GetProperty("layers").EnumerateArray()
            .Select(l => (l.GetProperty("layer").GetString()!, l.GetProperty("method").GetString()!, l.GetProperty("applied").GetBoolean(), l.TryGetProperty("reason", out _)))
            .ToList();
        Assert.Equal(new[] { ("metadata", "signed-record", false, false), ("metadata", "html-attribute", false, false), ("watermark", "vllm-gumbel", false, false) },
            layers);
        var placed = Assert.Single(Entries(session, ProvenanceEntries.Placed));
        Assert.All(placed.GetProperty("layers").EnumerateArray(), l => Assert.Equal("insert failed: InvalidOperationException", l.GetProperty("detail").GetString()));
        Assert.Empty(Entries(session, ProvenanceEntries.Unmarked));
    }

    [Fact]
    public async Task A_value_the_declaration_says_is_no_content_goes_in_as_it_is_unmarked_and_unreported()
    {
        var gateway = NewGateway()
            .SaysAndCalls("Filling.", Gateway.Receipt("rc-1"),
                ("call-1", "fill_form", Args(new { value = TestAgents.LongText, kind = "select" })),
                ("call-2", "tick", Args(new { value = TestAgents.LongText, kind = "checkbox" })))
            .Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools, p => p.UseDevelopmentMode());

        var (session, result) = await RunAsync(agent);

        Assert.Equal(TestAgents.LongText, tools.Editor.Html);
        var placement = Assert.Single(tools.Placed);
        Assert.Equal((true, ExemptReasons.NotContent, (string?)null, "value"), (placement.Exempt, placement.ExemptBecause, placement.RecordId, placement.Argument));
        Assert.Empty(placement.Layers);
        Assert.Equal("Placed; not content for people to read.", placement.Summary);
        Assert.Empty(gateway.RecordRequests);
        Assert.Empty(gateway.Placements);
        Assert.Empty(Entries(session, ProvenanceEntries.Placed));
        Assert.Empty(Entries(session, ProvenanceEntries.Unmarked)); // the check box was not placed, and is not content
        Assert.Equal(ChatOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task A_tool_method_called_by_hand_places_through_a_pass_through_provenance()
    {
        var tools = new WritingTools();
        var provenance = new Testing.PassThroughProvenance(new ProvenancePolicy { ShortTextTokens = 50 });

        var said = await tools.InsertReplyAsync("<p>Hello</p>", provenance, default);

        Assert.Equal(("Inserted.", "<p>Hello</p>"), (said, tools.Editor.Html)); // as it is: no mark
        Assert.Equal("<p>Hello</p>", Assert.Single(provenance.Requests).Text);
        var placement = Assert.Single(provenance.Placements);
        Assert.Equal(((string?)null, false, "html.editor"), (placement.RecordId, placement.Exempt, placement.SinkKind));
        Assert.Equal(50, (await provenance.GetPolicyAsync()).ShortTextTokens);
    }

    [Fact]
    public async Task The_same_tools_text_is_content_and_marked()
    {
        var gateway = NewGateway()
            .SaysAndCalls("Filling.", Gateway.Receipt("rc-1"), ("call-1", "fill_form", Args(new { value = TestAgents.LongText, kind = "text" })))
            .Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        await RunAsync(agent);

        Assert.Contains("data-ai-generated", tools.Editor.Html);
        Assert.Single(gateway.RecordRequests);
    }

    [Fact]
    public async Task A_picture_goes_in_as_the_same_bytes_and_a_host_that_re_encodes_it_is_recorded()
    {
        var picture = Pictures.Png();
        var gateway = NewGateway()
            .Calls(("call-1", "insert_picture", Args(new { picture = Convert.ToBase64String(picture) })))
            .Calls(("call-2", "insert_picture", Args(new { picture = Convert.ToBase64String(picture) })))
            .Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);
        var calls = 0;
        tools.KeepPicture = bytes => ++calls == 1 ? bytes : Pictures.Png(c2pa: false, xmp: false);

        await RunAsync(agent);

        Assert.Equal(picture, tools.Pictures[0]);
        var (kept, lost) = (tools.Placed[0], tools.Placed[1]);
        Assert.Contains(kept.Layers, l => l is { Method: "c2pa", Verified: true });
        Assert.Contains(kept.Layers, l => l is { Layer: MarkLayer.Watermark, Method: "trustmark", Applied: true });
        Assert.Empty(kept.Missing);
        Assert.Contains(lost.Missing, l => l is { Method: "c2pa", Reason: ProvenanceReasons.StrippedByHost });
        var sign = gateway.RecordRequests[0].Json;
        Assert.Equal((ContentHash.BytesSha256(picture), "rp-1", JsonValueKind.Undefined),
            (sign.GetProperty("content_sha256").GetString(), sign.GetProperty("receipt_id").GetString(), sign.TryGetProperty("model", out var m) ? m.ValueKind : JsonValueKind.Undefined));
    }

    [Fact]
    public async Task A_picture_that_lost_its_manifest_before_it_was_placed_is_refused_under_refuse()
    {
        var gateway = NewGateway("""{"on_missing":"refuse"}""")
            .Calls(("call-1", "insert_picture", Args(new { picture = Convert.ToBase64String(Pictures.Png(c2pa: false)) })))
            .Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        var (_, result) = await RunAsync(agent);

        Assert.Empty(tools.Pictures);
        Assert.Contains("the picture lost its signed metadata before it was placed", Assert.Single(result.Notices).Message);
    }

    [Fact]
    public async Task Without_a_server_module_the_default_policy_applies_and_placing_still_works()
    {
        var gateway = NewGateway(policy: null).SaysAndCalls("Reply:", Gateway.Unsigned, ("call-1", "chat_message", Args(new { text = "We will call you on Monday." }))).Says("Done.");
        var tools = new WritingTools();
        using var agent = TestAgents.Build(gateway, tools);

        await RunAsync(agent);

        Assert.Single(tools.Plain);
        Assert.Contains(Assert.Single(tools.Placed).Layers, l => l is { Method: "signed-record", Reason: ProvenanceReasons.ServerUnavailable });
    }
}
