using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fadenstack.Auth;
using Fadenstack.Chat;
using Fadenstack.Http;
using Fadenstack.Models;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>Content provenance in the client: the contract's types, how content is hashed, the routes, the receipts.</summary>
public class ProvenanceTests
{
    private const string Agent = "docs-assistant";

    private static string Contracts => Path.Combine(AppContext.BaseDirectory, "contracts", "provenance", "v1");

    private static JsonElement Schema(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Contracts, name + ".schema.json"))).RootElement;

    private static JsonElement Def(string schema, string def) => Schema(schema).GetProperty("$defs").GetProperty(def);

    private static (FadenstackClient Client, FakeServer Server) Make()
    {
        var server = new FakeServer();
        var client = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"), Agent = Agent, Credentials = new StaticTokenProvider("agent-token"),
            HttpClient = new HttpClient(server),
        });
        return (client, server);
    }

    /// <summary>The JSON names a type reads and writes, as FadenstackJson names them.</summary>
    private static List<string> JsonNames(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetMethod is not null && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name))
        .OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static void SameFields(Type type, JsonElement schema, params string[] leftOut)
    {
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Except(leftOut)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(properties, JsonNames(type));
        // Every required field is written by a fresh instance: none is null, so none is left out of a request.
        var written = JsonSerializer.SerializeToElement(Activator.CreateInstance(type), FadenstackJson.Options);
        var required = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).Except(leftOut).ToList() : new();
        Assert.All(required, name => Assert.True(written.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null,
            $"{type.Name} leaves out the required '{name}'"));
    }

    private static List<string> Enum(JsonElement property)
    {
        if (property.TryGetProperty("enum", out var values))
        {
            return values.EnumerateArray().Select(v => v.GetString()!).ToList();
        }
        if (property.TryGetProperty("items", out var items))
        {
            return Enum(items);
        }
        return property.GetProperty("anyOf").EnumerateArray().First(a => a.TryGetProperty("enum", out _)).GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()!).ToList();
    }

    private static List<string> Wire<T>() where T : struct, System.Enum =>
        System.Enum.GetValues(typeof(T)).Cast<T>().Select(v => JsonSerializer.Serialize(v, FadenstackJson.Options).Trim('"')).ToList();

    [Theory]
    [InlineData(typeof(ProvenanceRecord), "provenance-record")]
    [InlineData(typeof(SignedRecord), "signed-record")]
    [InlineData(typeof(PlacementReport), "placement-report")]
    [InlineData(typeof(LayerOutcome), "layer-outcome")]
    [InlineData(typeof(ProvenancePolicy), "provenance-policy")]
    [InlineData(typeof(GenerationReceipt), "generation-receipt")]
    [InlineData(typeof(SignedReceipt), "signed-receipt")]
    [InlineData(typeof(ReceiptWatermark), "watermark-status")]
    [InlineData(typeof(ProvenanceAnchors), "anchors")]
    [InlineData(typeof(VerifyRequest), "verify-request")]
    [InlineData(typeof(VerifyFinding), "verify-finding")]
    [InlineData(typeof(VerifyResult), "verify-result")]
    public void Each_contract_type_has_its_schemas_fields_and_writes_every_required_one(Type type, string schema) =>
        SameFields(type, Schema(schema));

    [Fact]
    public void A_placement_report_names_no_record_when_it_has_none_and_a_layer_says_whether_it_was_read_back()
    {
        var report = JsonSerializer.SerializeToElement(new PlacementReport
        {
            Tool = "t", SinkKind = "unmarked", MediaType = "text/plain",
            Layers = { new LayerOutcome { Layer = MarkLayer.Metadata, Method = "html-attribute", Applied = true, Verified = false } },
        }, FadenstackJson.Options);
        Assert.False(report.TryGetProperty("record_id", out _));
        Assert.False(report.GetProperty("layers")[0].GetProperty("verified").GetBoolean());
        Assert.DoesNotContain("record_id", Schema("placement-report").GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var watermark = JsonSerializer.Deserialize<ReceiptWatermark>("""{"applied":"model","algorithm":"gumbel","tokenizer":"Qwen/Qwen2.5-0.5B-Instruct"}""",
            FadenstackJson.Options)!;
        Assert.Equal("Qwen/Qwen2.5-0.5B-Instruct", watermark.Tokenizer);
    }

    [Fact]
    public void A_placement_report_leaves_written_and_failure_out_unless_its_insert_failed()
    {
        var plain = JsonSerializer.SerializeToElement(new PlacementReport { Tool = "t", SinkKind = "s", MediaType = "text/plain" }, FadenstackJson.Options);
        Assert.False(plain.TryGetProperty("written", out _) || plain.TryGetProperty("failure", out _));
        var failed = JsonSerializer.SerializeToElement(new PlacementReport
        {
            Tool = "t", SinkKind = "s", MediaType = "text/plain", Written = false, Failure = "InvalidOperationException",
        }, FadenstackJson.Options);
        Assert.Equal((false, "InvalidOperationException"), (failed.GetProperty("written").GetBoolean(), failed.GetProperty("failure").GetString()));
        var properties = Schema("placement-report").GetProperty("properties");
        Assert.True(properties.GetProperty("written").GetProperty("default").GetBoolean());
        var failure = properties.GetProperty("failure").GetProperty("anyOf")[0];
        Assert.Equal(PlacementReport.FailurePattern, failure.GetProperty("pattern").GetString());
    }

    [Fact]
    public void A_record_request_is_the_schemas_without_the_agent_which_the_route_sets() =>
        SameFields(typeof(RecordRequest), Schema("record-request"), "agent");

    [Fact]
    public void The_nested_objects_match_their_definitions()
    {
        SameFields(typeof(ProvenanceModel), Def("provenance-record", "ModelInfo"));
        SameFields(typeof(ProvenanceSystem), Def("provenance-record", "SystemInfo"));
        SameFields(typeof(DetectorInfo), Def("anchors", "DetectorInfo"));
    }

    [Fact]
    public void The_enumerations_have_the_contracts_values()
    {
        var properties = Schema("placement-report").GetProperty("properties");
        Assert.Equal(Enum(properties.GetProperty("edit_kind")), Wire<EditKind>());
        Assert.Equal(Enum(properties.GetProperty("edit_kind")), System.Enum.GetValues(typeof(EditKind)).Cast<EditKind>().Select(k => k.ToWire()));
        Assert.Equal(Enum(properties.GetProperty("length_class")), Wire<LengthClass>());
        var layer = Schema("layer-outcome").GetProperty("properties");
        Assert.Equal(Enum(layer.GetProperty("layer")).OrderBy(v => v), Wire<MarkLayer>().OrderBy(v => v));
        Assert.Equal(Enum(layer.GetProperty("reason")), ProvenanceReasons.All);
        Assert.Equal(Enum(Schema("watermark-status").GetProperty("properties").GetProperty("applied")).OrderBy(v => v),
            Wire<WatermarkApplied>().OrderBy(v => v));
        Assert.Equal(Enum(Schema("provenance-policy").GetProperty("properties").GetProperty("on_missing")), Wire<MissingLayerPolicy>());
        Assert.Equal(Enum(Schema("generation-receipt").GetProperty("properties").GetProperty("kind")), Wire<ReceiptKind>());
        Assert.Equal(Enum(Schema("provenance-policy").GetProperty("properties").GetProperty("exempt_edit_kinds")), Wire<EditKind>());
        Assert.Equal(Enum(Schema("verify-result").GetProperty("properties").GetProperty("verdict")), Wire<VerifyVerdict>());
        Assert.Equal(Enum(Schema("verify-finding").GetProperty("properties").GetProperty("layer")).OrderBy(v => v), Wire<MarkLayer>().OrderBy(v => v));
    }

    [Fact]
    public void A_fresh_policy_is_the_contracts_default()
    {
        var policy = new ProvenancePolicy();
        var properties = Schema("provenance-policy").GetProperty("properties");
        Assert.Equal(properties.GetProperty("on_missing").GetProperty("default").GetString(), policy.OnMissing.ToString().ToLowerInvariant());
        Assert.Equal(properties.GetProperty("short_text_tokens").GetProperty("default").GetInt32(), policy.ShortTextTokens);
        Assert.Equal(new[] { EditKind.Translated, EditKind.Corrected, EditKind.Formatted, EditKind.Extracted }, policy.ExemptEditKinds);
        Assert.False(policy.InternalTechnical || policy.VisibleLabel || policy.OutputLog);
    }

    [Fact]
    public void Text_hashes_as_the_engine_hashes_it()
    {
        var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(Contracts, "text-hash-vectors.json"))).RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(vectors);
        foreach (var v in vectors)
        {
            var input = v.GetProperty("input").GetString()!;
            Assert.Equal(v.GetProperty("normalized").GetString(), ContentHash.Normalize(input));
            Assert.Equal(v.GetProperty("sha256").GetString(), ContentHash.TextSha256(input));
        }
    }

    [Fact]
    public void Bytes_hash_as_they_are()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ContentHash.BytesSha256(Array.Empty<byte>()));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ContentHash.BytesSha256("abc"u8.ToArray()));
        Assert.Equal("a b", ContentHash.Normalize("\u001Ca\u001F\u0085b "));
    }

    private const string SignedRecordJson = """
        {"record":{"contract":"provenance/v1","id":"5a0e2b5c-7f3e-4a51-9a40-6a1c55e1d3f1",
          "digital_source_type":"http://cv.iptc.org/newscodes/digitalsourcetype/trainedAlgorithmicMedia","edit_kind":"generated",
          "system":{"name":"Fadenstack","version":"0.5.0"},"provider":"Example GmbH","model":{"id":"qwen3.8-27b","version":null},
          "created_at":"2026-10-18T09:30:00Z","receipt_id":"rc-1","media_type":"text/markdown",
          "content_hash":"0f0e","hash_key_version":2},
         "signature":"eyJhbGciOiJFUzI1NiJ9.e30.c2ln","timestamp":null}
        """;

    [Fact]
    public async Task An_agent_reads_its_policy_has_records_signed_and_reports_placements()
    {
        var (client, server) = Make();
        server.Json("GET", "/v1/agents/docs-assistant/provenance/policy",
            new { on_missing = "refuse", short_text_tokens = 120, exempt_edit_kinds = new[] { "translated" }, internal_technical = false, visible_label = true, output_log = false })
            .On("POST", "/v1/agents/docs-assistant/provenance/records", _ => FakeServer.Respond(HttpStatusCode.OK, SignedRecordJson))
            .Json("POST", "/v1/agents/docs-assistant/provenance/placements", new { stored = true }, HttpStatusCode.Accepted);

        var policy = await client.Agent!.Provenance.GetPolicyAsync();
        var signed = await client.Agent.Provenance.SignRecordAsync(new RecordRequest
        {
            ContentSha256 = ContentHash.TextSha256("Dear Ms Huber,"), MediaType = "text/markdown", EditKind = EditKind.Generated,
            ReceiptId = "rc-1", Model = "qwen3.8-27b",
        });
        await client.Agent.Provenance.ReportPlacementAsync(new PlacementReport
        {
            RecordId = signed.Record.Id, Tool = "insert_reply", SinkKind = "html.editor", MediaType = "text/html",
            EditKind = EditKind.Generated, LengthClass = LengthClass.Short,
            Layers = { new LayerOutcome { Layer = MarkLayer.Metadata, Method = "html-attribute", Applied = true },
                       new LayerOutcome { Layer = MarkLayer.Watermark, Method = "vllm-gumbel", Applied = false, Reason = ProvenanceReasons.ShortText } },
        });

        Assert.Equal((MissingLayerPolicy.Refuse, 120, true), (policy.OnMissing, policy.ShortTextTokens, policy.VisibleLabel));
        Assert.Equal(new[] { EditKind.Translated }, policy.ExemptEditKinds);
        Assert.Equal(("5a0e2b5c-7f3e-4a51-9a40-6a1c55e1d3f1", EditKind.Generated, "qwen3.8-27b", 2, "rc-1"),
            (signed.Record.Id, signed.Record.EditKind, signed.Record.Model!.Id, signed.Record.HashKeyVersion, signed.Record.ReceiptId));
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero), signed.Record.CreatedAt);
        var sign = server.Requests.Single(r => r.Path.EndsWith("/records")).Json;
        Assert.Equal(new[] { "content_sha256", "edit_kind", "media_type", "model", "receipt_id" },
            sign.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(ContentHash.TextSha256("Dear Ms Huber,"), sign.GetProperty("content_sha256").GetString());
        var report = server.Requests.Single(r => r.Path.EndsWith("/placements"));
        Assert.DoesNotContain("Huber", report.Body); // names and hashes, never content
        var layers = report.Json.GetProperty("layers").EnumerateArray().ToList();
        Assert.Equal(("metadata", "html-attribute", true), (layers[0].GetProperty("layer").GetString(), layers[0].GetProperty("method").GetString(),
            layers[0].GetProperty("applied").GetBoolean()));
        Assert.False(layers[0].TryGetProperty("reason", out _));
        Assert.Equal(("watermark", "short-text"), (layers[1].GetProperty("layer").GetString(), layers[1].GetProperty("reason").GetString()));
        Assert.Equal(("short", "html.editor"), (report.Json.GetProperty("length_class").GetString(), report.Json.GetProperty("sink_kind").GetString()));
    }

    [Fact]
    public async Task A_record_of_markup_sends_the_hash_of_the_text_a_reader_sees_and_its_record_keeps_it_keyed()
    {
        var (client, server) = Make();
        server.On("POST", "/v1/agents/docs-assistant/provenance/records",
            _ => FakeServer.Respond(HttpStatusCode.OK, SignedRecordJson.Replace("\"hash_key_version\":2", "\"hash_key_version\":2,\"text_hash\":\"7e47\"")));

        var signed = await client.Agent!.Provenance.SignRecordAsync(new RecordRequest
        {
            ContentSha256 = ContentHash.TextSha256("<p>Dear <b>Ms</b> Huber,</p>"), TextSha256 = ContentHash.TextSha256("Dear Ms Huber,"),
            MediaType = "text/html", EditKind = EditKind.Generated,
        });

        var sign = server.Requests.Single(r => r.Path.EndsWith("/records")).Json;
        Assert.Equal(ContentHash.TextSha256("Dear Ms Huber,"), sign.GetProperty("text_sha256").GetString());
        Assert.Equal("7e47", signed.Record.TextHash);
        Assert.Null(JsonSerializer.Deserialize<ProvenanceRecord>("""{"id":"x"}""", FadenstackJson.Options)!.TextHash);
    }

    [Fact]
    public async Task Anyone_reads_the_anchors_and_a_server_without_the_module_says_so()
    {
        var (client, server) = Make();
        server.Json("GET", "/v1/provenance/anchors", new
        {
            contract = "provenance/v1", signing_chains = new[] { "-----BEGIN CERTIFICATE-----\nleaf" },
            trust_anchors = new[] { "-----BEGIN CERTIFICATE-----\nca" }, detector = new { id = "fadenstack-provenance/0.5.0", methods = new[] { "c2pa", "record" } },
        }).Json("GET", "/v1/agents/docs-assistant/provenance/policy",
            new { error = new { message = "Content provenance is not switched on here.", type = "provenance_unavailable", code = "provenance_off" } },
            HttpStatusCode.ServiceUnavailable);

        var anchors = await client.Provenance.GetAnchorsAsync();
        var off = await Assert.ThrowsAsync<FadenstackApiException>(() => client.Agent!.Provenance.GetPolicyAsync());

        Assert.Equal(("fadenstack-provenance/0.5.0", 1, 1), (anchors.Detector.Id, anchors.SigningChains.Count, anchors.TrustAnchors.Count));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "provenance_off"), (off.Status, off.Code));
    }

    private static object Receipt(string id = "rc-7", string applied = "none", string? reason = "runtime-without-watermark") => new
    {
        receipt = new
        {
            contract = "provenance/v1", id, kind = "text", model = new { id = "qwen3.8-27b", version = (string?)null },
            created_at = "2026-10-18T09:30:00Z", watermark = new { applied, reason }, output_hash = "ab12", hash_key_version = 1,
        },
        signature = "eyJ.e30.sig",
        timestamp = (string?)null,
    };

    private static async Task<List<AIContent>> StreamAsync(FadenstackClient client)
    {
        var contents = new List<AIContent>();
        await foreach (var update in client.Chat.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "Write the reply.") }))
        {
            contents.AddRange(update.Contents);
        }
        return contents;
    }

    [Fact]
    public async Task A_streamed_answers_receipt_comes_as_typed_content_before_the_stream_ends()
    {
        var (client, server) = Make();
        server.On("POST", "/v1/agents/docs-assistant/chat/completions", _ => FakeServer.Sse(
            FakeServer.Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { content = "Dear Ms Huber," } } } }),
            FakeServer.Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } } }),
            FakeServer.Fadenstack(new { type = "CUSTOM", name = "faden.provenance", value = Receipt() }),
            FakeServer.Done));

        var contents = await StreamAsync(client);

        var receipt = Assert.Single(contents.OfType<ProvenanceReceiptContent>());
        Assert.Equal(("rc-7", WatermarkApplied.None, ProvenanceReasons.RuntimeWithoutWatermark, "eyJ.e30.sig", (string?)null),
            (receipt.ReceiptId, receipt.Signed!.Receipt.Watermark.Applied, receipt.Signed.Receipt.Watermark.Reason, receipt.Signed.Signature, receipt.Reason));
        Assert.Contains(contents.OfType<FadenstackEventContent>(), e => e.Kind == "faden.provenance"); // the raw event stays
    }

    [Fact]
    public async Task A_streamed_answer_the_server_could_not_sign_says_so()
    {
        var (client, server) = Make();
        server.On("POST", "/v1/agents/docs-assistant/chat/completions", _ => FakeServer.Sse(
            FakeServer.Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { content = "Done." }, finish_reason = "stop" } } }),
            FakeServer.Fadenstack(new { type = "CUSTOM", name = "faden.provenance", value = new { receipt = (object?)null, status = "unsigned", reason = "server-unavailable" } }),
            FakeServer.Done));

        var receipt = Assert.Single((await StreamAsync(client)).OfType<ProvenanceReceiptContent>());

        Assert.Equal(((string?)null, (SignedReceipt?)null, ProvenanceReasons.ServerUnavailable), (receipt.ReceiptId, receipt.Signed, receipt.Reason));
    }

    [Fact]
    public async Task A_whole_answer_names_its_receipts_in_the_header()
    {
        var (client, server) = Make();
        server.On("POST", "/v1/agents/docs-assistant/chat/completions", _ =>
        {
            var answer = FakeServer.Respond(HttpStatusCode.OK, new
            {
                id = "c1", model = "qwen3.8-27b",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "Here is the chart." }, finish_reason = "stop" } },
            });
            answer.Headers.Add("x-faden-provenance", "rc-1, rp-2");
            return answer;
        });

        var response = await client.Chat.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Chart it.") });

        Assert.Equal(new[] { "rc-1", "rp-2" }, response.GetProvenanceReceiptIds());
        var receipt = response.GetProvenanceReceipt()!;
        Assert.Equal(("rc-1", (SignedReceipt?)null), (receipt.ReceiptId, receipt.Signed));
    }

    [Fact]
    public async Task A_whole_answer_with_events_carries_the_signed_receipt()
    {
        var (client, server) = Make();
        server.On("POST", "/v1/agents/docs-assistant/chat/completions", _ =>
        {
            var answer = FakeServer.Respond(HttpStatusCode.OK, new
            {
                id = "c1", model = "qwen3.8-27b",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "Done." }, finish_reason = "stop" } },
                faden = new { events = new object[] { new { type = "CUSTOM", name = "faden.provenance", value = Receipt("rc-9") } } },
            });
            answer.Headers.Add("x-faden-provenance", "rc-9");
            return answer;
        });

        var response = await client.Chat.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Go.") });

        var receipt = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<ProvenanceReceiptContent>());
        Assert.Equal(("rc-9", "eyJ.e30.sig"), (receipt.ReceiptId, receipt.Signed!.Signature));
    }

    [Fact]
    public async Task An_answer_without_text_gets_no_receipt_of_its_own_from_the_header()
    {
        var (client, server) = Make();
        server.On("POST", "/v1/agents/docs-assistant/chat/completions", _ =>
        {
            var answer = FakeServer.Respond(HttpStatusCode.OK, new
            {
                id = "c1", model = "m",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = (string?)null }, finish_reason = "stop" } },
            });
            answer.Headers.Add("x-faden-provenance", "rp-1");
            return answer;
        });

        var response = await client.Chat.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Draw.") });

        Assert.Null(response.GetProvenanceReceipt()); // the one id is a picture's
        Assert.Equal(new[] { "rp-1" }, response.GetProvenanceReceiptIds());
    }

    [Fact]
    public async Task Pictures_come_with_their_receipts()
    {
        var (client, server) = Make();
        server.On("POST", "/v1/agents/docs-assistant/images/generations", _ =>
        {
            var answer = FakeServer.Respond(HttpStatusCode.OK, new { created = 1, data = new[] { new { b64_json = "AAAA" }, new { b64_json = "AQID" } } });
            answer.Headers.Add("X-Faden-Provenance", "rp-1,rp-2");
            return answer;
        });

        var images = await client.Agent!.GenerateImagesAsync(new ImageRequest { Prompt = "Two charts", N = 2 });

        Assert.Equal(new[] { "rp-1", "rp-2" }, images.Receipts);
        Assert.Equal(2, images.Pictures.Count);
    }
}
