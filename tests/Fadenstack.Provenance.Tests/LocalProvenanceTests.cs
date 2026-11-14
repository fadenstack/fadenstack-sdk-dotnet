using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Agents.Tests;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Endpoints;
using Fadenstack.Models;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Provenance.Tests;

/// <summary>sdk/51 F10: the device's own provenance backend, records signed on the device, the time-stamp, the local log.</summary>
public class LocalProvenanceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "faden-local-prov-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private static RecordRequest Request() => new()
    {
        ContentSha256 = new string('a', 64), MediaType = "text/plain", EditKind = EditKind.Generated, Model = "qwen3:14b",
    };

    private LocalProvenanceBackend Backend(TsaFake? tsa = null, bool timestamping = true, DeviceSigner? signer = null) => new(new LocalProvenanceOptions
    {
        Folder = _folder, Signer = signer ?? DeviceSigner.Ephemeral(), Handler = tsa, Timestamping = timestamping,
        TimestampAuthority = new Uri("https://tsa.example.test/"),
    });

    private static FadenstackAgent Agent() => FadenstackAgent.CreateBuilder()
        .UseChatClient(new NoModel())
        .UseLocalProfile(AgentDefinition.FromJson("""{"slug":"docs-assistant"}"""))
        .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
        .DescribeApp(new AppDescription { Name = "Word add-in", Version = "0.3.0" })
        .Build();

    // ---- signing ----

    [Fact]
    public void A_record_verifies_with_its_own_public_key_in_the_engines_format()
    {
        using var signer = DeviceSigner.Ephemeral();
        var payload = new JsonObject { ["id"] = "r-1", ["content_hash"] = "ab", ["b"] = 1, ["a"] = "Grüße" };

        var jws = signer.Sign(payload);

        var parts = jws.Split('.');
        Assert.Equal(3, parts.Length);
        var header = JsonDocument.Parse(FromBase64Url(parts[0])).RootElement;
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Equal(DeviceSigner.Typ, header.GetProperty("typ").GetString());
        Assert.StartsWith(DeviceSigner.KidPrefix, header.GetProperty("kid").GetString());
        // The payload is canonical: keys sorted, no spaces, characters as they are.
        Assert.Equal("{\"a\":\"Grüße\",\"b\":1,\"content_hash\":\"ab\",\"id\":\"r-1\"}", Encoding.UTF8.GetString(FromBase64Url(parts[1])));
        Assert.Equal(64, FromBase64Url(parts[2]).Length); // ES256: r || s
#if NET9_0_OR_GREATER
        using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(header.GetProperty("x5c")[0].GetString()!));
#else
        using var certificate = new X509Certificate2(Convert.FromBase64String(header.GetProperty("x5c")[0].GetString()!));
#endif
        Assert.Equal("CN=Fadenstack device signer", certificate.Subject);
        Assert.NotNull(certificate.GetECDsaPublicKey());
        var verified = DeviceSigner.Verify(jws);
        Assert.Equal("r-1", verified!.Value.GetProperty("id").GetString());
    }

    /// <summary>
    /// An ECDSA r or s, like a nonce, starts with a zero byte about once in 256: the certificate and the time-stamp request must
    /// encode it all the same (a redundant leading zero is no valid DER integer).
    /// </summary>
    [Fact]
    public async Task Thousands_of_keys_and_records_never_fail_on_a_leading_zero()
    {
        for (var i = 0; i < 2000; i++)
        {
            using var signer = DeviceSigner.Ephemeral();
            Assert.NotNull(DeviceSigner.Verify(signer.Sign(new JsonObject { ["i"] = i })));
        }
        var tsa = new TsaFake();
        using var backend = Backend(tsa);
        using var agent = Agent();
        for (var i = 0; i < 600; i++)
        {
            Assert.NotNull((await backend.SignRecordAsync(agent, Request(), default)).Timestamp);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_flipped_byte_anywhere_fails(int part)
    {
        using var signer = DeviceSigner.Ephemeral();
        var parts = signer.Sign(new JsonObject { ["id"] = "r-1" }).Split('.');
        var bytes = FromBase64Url(parts[part]);
        bytes[bytes.Length / 2] ^= 0x01;
        parts[part] = ToBase64Url(bytes);

        Assert.Null(DeviceSigner.Verify(string.Join(".", parts)));
    }

    [Fact]
    public void Another_keys_signature_does_not_verify_under_this_certificate()
    {
        using var one = DeviceSigner.Ephemeral();
        using var other = DeviceSigner.Ephemeral();
        var mine = one.Sign(new JsonObject { ["id"] = "r-1" }).Split('.');
        var theirs = other.Sign(new JsonObject { ["id"] = "r-1" }).Split('.');

        Assert.Null(DeviceSigner.Verify($"{mine[0]}.{mine[1]}.{theirs[2]}"));
        Assert.NotEqual(one.KeyId, other.KeyId);
    }

    [Fact]
    public void The_key_is_kept_only_encrypted_and_read_back_as_the_same_key()
    {
        var file = Path.Combine(_folder, "keys", "signing.key");
        var protector = new XorProtector();

        string keyId;
        byte[] d;
        using (var first = DeviceSigner.ForInstallation(file, protector))
        {
            keyId = first.KeyId;
            Assert.True(first.Persisted);
            d = PrivateKeyOf(first);
        }
        using var again = DeviceSigner.ForInstallation(file, protector);

        Assert.Equal(keyId, again.KeyId);
        var stored = File.ReadAllBytes(file);
        Assert.False(Contains(stored, d)); // the private key is never in the file as it is
        Assert.False(Contains(stored, Encoding.UTF8.GetBytes(Convert.ToBase64String(d))));
        using var kept = JsonDocument.Parse(protector.Unprotect(stored)); // what the OS key store decrypts
        Assert.Equal(Convert.ToBase64String(d), kept.RootElement.GetProperty("d").GetString());
    }

    [Fact]
    public void Without_a_protector_that_encrypts_the_key_lives_in_memory_and_nothing_is_written()
    {
        var file = Path.Combine(_folder, "keys", "signing.key");

        using var signer = DeviceSigner.ForInstallation(file, new PlainProtector());

        Assert.False(signer.Persisted);
        Assert.False(File.Exists(file));
        Assert.NotNull(DeviceSigner.Verify(signer.Sign(new JsonObject { ["id"] = "x" })));
    }

    [Fact]
    public void A_key_file_this_user_cannot_read_is_replaced()
    {
        var file = Path.Combine(_folder, "keys", "signing.key");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

        using var signer = DeviceSigner.ForInstallation(file, new FailingProtector());

        Assert.True(signer.Persisted);
        Assert.NotEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
    }

    // ---- the record ----

    [Fact]
    public async Task The_record_is_the_servers_format_signed_by_the_device()
    {
        using var backend = Backend(timestamping: false);
        using var agent = Agent();

        var signed = await backend.SignRecordAsync(agent, Request(), default);

        var r = signed.Record;
        Assert.True(Guid.TryParse(r.Id, out _));
        Assert.Equal("http://cv.iptc.org/newscodes/digitalsourcetype/trainedAlgorithmicMedia", r.DigitalSourceType);
        Assert.Equal(("Word add-in", "0.3.0"), (r.System.Name, r.System.Version));
        Assert.Equal("standalone", r.Provider);
        Assert.Equal(new string('a', 64), r.ContentHash);
        Assert.Equal(LocalProvenanceBackend.PlainHashKeyVersion, r.HashKeyVersion);
        Assert.Equal("qwen3:14b", r.Model!.Id);
        Assert.Null(signed.Timestamp); // the device's clock
        var payload = DeviceSigner.Verify(signed.Signature)!.Value;
        Assert.Equal(r.Id, payload.GetProperty("id").GetString());
        Assert.Equal(r.ContentHash, payload.GetProperty("content_hash").GetString());
        Assert.Equal(0, payload.GetProperty("hash_key_version").GetInt32());
    }

    [Fact]
    public async Task Online_the_signature_gets_a_time_stamp_and_only_its_hash_leaves()
    {
        var tsa = new TsaFake();
        using var backend = Backend(tsa);
        using var agent = Agent();

        var signed = await backend.SignRecordAsync(agent, Request(), default);

        Assert.NotNull(signed.Timestamp);
        var sent = Assert.Single(tsa.Requests);
        Assert.Equal("application/timestamp-query", sent.ContentType);
        using var sha = SHA256.Create();
        Assert.Equal(sha.ComputeHash(DeviceSigner.SignatureBytes(signed.Signature)), sent.Hash);
        Assert.Equal(32, sent.Hash.Length);
        Assert.False(Contains(sent.Body, Encoding.UTF8.GetBytes(signed.Record.ContentHash))); // no record field, just the hash
    }

    [Fact]
    public async Task Offline_or_with_a_wrong_answer_the_record_goes_without_a_token()
    {
        using var agent = Agent();
        using var offline = Backend(new TsaFake { Down = true });
        using var wrong = Backend(new TsaFake { WrongNonce = true });
        using var refused = Backend(new TsaFake { Status = 2 });

        Assert.Null((await offline.SignRecordAsync(agent, Request(), default)).Timestamp);
        Assert.Null((await wrong.SignRecordAsync(agent, Request(), default)).Timestamp);
        Assert.Null((await refused.SignRecordAsync(agent, Request(), default)).Timestamp);
    }

    [Fact]
    public async Task Time_stamping_switched_off_sends_nothing()
    {
        var tsa = new TsaFake();
        using var backend = Backend(tsa, timestamping: false);
        using var agent = Agent();

        await backend.SignRecordAsync(agent, Request(), default);

        Assert.Empty(tsa.Requests);
    }

    [Fact]
    public async Task The_machine_policy_can_switch_time_stamps_off()
    {
        var tsa = new TsaFake();
        using var backend = Backend(tsa);
        using var agent = FadenstackAgent.CreateBuilder()
            .UseChatClient(new NoModel())
            .UseLocalProfile(AgentDefinition.FromJson("""{"slug":"docs-assistant"}"""))
            .UseMachinePolicy(MachinePolicy.FromJson("""{"timestamping":"forbidden"}"""))
            .Build();

        var signed = await backend.SignRecordAsync(agent, Request(), default);

        Assert.Null(signed.Timestamp);
        Assert.Empty(tsa.Requests);
        Assert.NotNull(DeviceSigner.Verify(signed.Signature)); // still signed
    }

    [Fact]
    public async Task The_policy_is_the_local_default()
    {
        using var backend = Backend();
        using var agent = Agent();

        var policy = await backend.GetPolicyAsync(agent, default);

        Assert.Equal(MissingLayerPolicy.Record, policy.OnMissing);
        Assert.Equal(200, policy.ShortTextTokens);
        Assert.Equal(EditKinds.ExemptByDefault.OrderBy(k => k), policy.ExemptEditKinds.OrderBy(k => k));
    }

    /// <summary>Opt-in: <c>FADEN_LIVE_TSA=http://timestamp.digicert.com</c> asks a real authority (only a hash is sent).</summary>
    [Fact]
    public async Task Live_a_public_authority_grants_a_token_the_device_accepts()
    {
        if (Environment.GetEnvironmentVariable("FADEN_LIVE_TSA") is not { Length: > 0 } url)
        {
            return;
        }
        using var backend = new LocalProvenanceBackend(new LocalProvenanceOptions
        {
            Folder = _folder, Signer = DeviceSigner.Ephemeral(), TimestampAuthority = new Uri(url), TimestampTimeout = TimeSpan.FromSeconds(15),
        });
        using var agent = Agent();

        var signed = await backend.SignRecordAsync(agent, Request(), default);

        Assert.NotNull(signed.Timestamp);
    }

    // ---- the log ----

    [Fact]
    public async Task Placements_go_to_a_local_log_that_rotates()
    {
        using var backend = new LocalProvenanceBackend(new LocalProvenanceOptions
        {
            Folder = _folder, Signer = DeviceSigner.Ephemeral(), Timestamping = false, MaxLogBytes = 400, KeepLogs = 2,
        });
        using var agent = Agent();

        for (var i = 0; i < 12; i++)
        {
            await backend.ReportPlacementAsync(agent, new PlacementReport { RecordId = $"rec-{i:00}" }, default);
        }

        var files = Directory.GetFiles(_folder).Select(Path.GetFileName).OrderBy(f => f).ToList();
        Assert.Equal(new[] { "placements.jsonl", "placements.jsonl.1", "placements.jsonl.2" }, files);
        Assert.All(Directory.GetFiles(_folder), f => Assert.True(new FileInfo(f).Length <= 400));
        Assert.Contains("rec-11", File.ReadAllText(Path.Combine(_folder, "placements.jsonl")));
        Assert.DoesNotContain(Directory.GetFiles(_folder).SelectMany(File.ReadAllLines), l => l.Contains("rec-00"));
    }

    // ---- which backend ----

    [Fact]
    public void The_backend_follows_the_agents_connection_unless_the_app_set_one()
    {
        var options = new ProvenanceOptions();
        using var standalone = Agent();
        var gateway = new Gateway();
        using var governed = gateway.Builder().Build();

        Assert.Same(options.LocalBackend, options.BackendFor(standalone));
        Assert.Same(ServerProvenanceBackend.Instance, options.BackendFor(governed));
        var own = new LocalProvenanceBackend();
        options.UseBackend(own);
        Assert.Same(own, options.BackendFor(standalone));
        Assert.Same(own, options.BackendFor(governed));
    }

    [Fact]
    public async Task A_standalone_placement_is_signed_on_the_device_and_never_server_unavailable()
    {
        var fake = new OpenAiFake();
        fake.Answers.Enqueue(OpenAiFake.Calls(("call-1", "chat_message", "{\"text\":\"We will call you on Monday.\"}")));
        fake.Answers.Enqueue(OpenAiFake.Text("Done."));
        var tools = new WritingTools();
        using var agent = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "qwen3:14b", Handler = fake })
            .UseLocalProfile(AgentDefinition.FromJson("{\"slug\":\"docs-assistant\",\"version\":{\"host_tool_contract\":" + WritingTools.Contract + "}}"))
            .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve()))
            .AddTools(tools)
            .Use(new ProvenanceModule(o => o.UseLocalBackend(new LocalProvenanceOptions
            {
                Folder = _folder, Signer = DeviceSigner.Ephemeral(), Timestamping = false,
            })))
            .Build();

        var session = await agent.StartSessionAsync();
        await session.SendAsync("Tell the customer.");

        var placed = Assert.Single(tools.Placed);
        Assert.NotNull(placed.Record);
        Assert.NotNull(DeviceSigner.Verify(placed.Record!.Signature));
        Assert.DoesNotContain(placed.Layers, l => l.Reason == ProvenanceReasons.ServerUnavailable);
        Assert.Contains(placed.Layers, l => l is { Method: "signed-record", Applied: true });
        Assert.True(File.Exists(Path.Combine(_folder, "placements.jsonl")));
        Assert.All(fake.Requests, r => Assert.EndsWith("/chat/completions", r.Url.AbsolutePath)); // nothing else left the device
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", "ollama", null, false, "endpoint-ollama", false, ProvenanceReasons.RuntimeWithoutWatermark)]
    [InlineData("https://api.anthropic.com/v1", "openai-compatible", null, false, "endpoint-anthropic", true, null)]
    [InlineData("https://api.x.ai/v1", "openai-compatible", "true", false, "endpoint-xai", true, null)]
    [InlineData("https://api.x.ai/v1", "openai-compatible", "false", false, "endpoint-xai", false, ProvenanceReasons.NoWatermarkRemoteModel)]
    [InlineData("https://api.openai.com/v1", "openai", null, false, "endpoint-openai", false, null)]
    [InlineData("http://localhost:8000/v1", "vllm", null, false, "endpoint-vllm", false, null)]
    [InlineData("https://api.anthropic.com/v1", "openai-compatible", null, true, "endpoint-anthropic", false, ProvenanceReasons.NoWatermarkGreedy)]
    public async Task The_watermark_layer_relies_on_the_endpoint_and_never_claims_more(string baseUrl, string dialect, string? header,
        bool greedy, string method, bool applied, string? reason)
    {
        var fake = new OpenAiFake();
        if (header is not null)
        {
            fake.ChatHeaders["x-watermark"] = header;
        }
        fake.Answers.Enqueue(OpenAiFake.Calls(("call-1", "chat_message", JsonSerializer.Serialize(new { text = TestAgents.LongText }))));
        fake.Answers.Enqueue(OpenAiFake.Text("Done."));
        var tools = new WritingTools();
        var builder = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint
            {
                BaseUrl = new Uri(baseUrl), Model = "m", Dialect = EndpointDialect.Named(dialect), Handler = fake,
                Key = baseUrl.StartsWith("https", StringComparison.Ordinal) ? new StaticTokenProvider("k") : null,
            })
            .UseLocalProfile(AgentDefinition.FromJson("{\"slug\":\"docs-assistant\",\"version\":{\"host_tool_contract\":" + WritingTools.Contract + "}}"))
            .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Approve()))
            .AddTools(tools)
            .Use(new ProvenanceModule(o => o.UseLocalBackend(new LocalProvenanceOptions
            {
                Folder = _folder, Signer = DeviceSigner.Ephemeral(), Timestamping = false,
            })));
        if (greedy)
        {
            builder.AddChatStage(StageOrder.Custom, inner => new GreedyStage(inner));
        }
        using var agent = builder.Build();

        var session = await agent.StartSessionAsync();
        await session.SendAsync("Tell the customer.");

        var layer = Assert.Single(Assert.Single(tools.Placed).Layers, l => l.Layer == MarkLayer.Watermark);
        Assert.Equal((method, applied, reason), (layer.Method, layer.Applied, layer.Reason));
    }

    /// <summary>An app that sets temperature 0: the endpoint's watermark may not apply.</summary>
    private sealed class GreedyStage : Microsoft.Extensions.AI.DelegatingChatClient
    {
        public GreedyStage(Microsoft.Extensions.AI.IChatClient inner) : base(inner)
        {
        }

        public override IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new Microsoft.Extensions.AI.ChatOptions();
            options.Temperature = 0;
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
    }

    // ---- helpers ----

    private static byte[] PrivateKeyOf(DeviceSigner signer)
    {
        var field = typeof(DeviceSigner).GetField("_key", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return ((ECDsa)field.GetValue(signer)!).ExportParameters(true).D!;
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }
        return false;
    }

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    private static string ToBase64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class XorProtector : ISecretProtector
    {
        public bool Encrypts => true;
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => Protect(data);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public bool Encrypts => false;
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }

    private sealed class FailingProtector : ISecretProtector
    {
        public bool Encrypts => true;
        public byte[] Protect(byte[] data) => Enumerable.Reverse(data).ToArray();
        public byte[] Unprotect(byte[] data) => throw new CryptographicException("not this user's");
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

    /// <summary>A time-stamp authority: reads the request, answers a granted token over its hash and nonce (or not).</summary>
    private sealed class TsaFake : HttpMessageHandler
    {
        public List<(string? ContentType, byte[] Body, byte[] Hash)> Requests { get; } = new();

        public bool Down { get; init; }

        public bool WrongNonce { get; init; }

        public int Status { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("offline");
            }
            var body = await request.Content!.ReadAsByteArrayAsync();
            var req = new AsnReader(body, AsnEncodingRules.DER).ReadSequence();
            req.ReadInteger();
            var imprint = req.ReadSequence();
            imprint.ReadSequence();
            var hash = imprint.ReadOctetString();
            var nonce = req.ReadInteger();
            Requests.Add((request.Content.Headers.ContentType?.MediaType, body, hash));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Response(hash, WrongNonce ? nonce + 1 : nonce)) };
        }

        private byte[] Response(byte[] hash, System.Numerics.BigInteger nonce)
        {
            var info = new AsnWriter(AsnEncodingRules.DER);
            info.PushSequence();
            info.WriteInteger(1);
            info.WriteObjectIdentifier("1.2.3.4");
            info.PushSequence();
            info.PushSequence();
            info.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
            info.PopSequence();
            info.WriteOctetString(hash);
            info.PopSequence();
            info.WriteInteger(42);
            info.WriteGeneralizedTime(DateTimeOffset.UtcNow);
            info.WriteInteger(nonce);
            info.PopSequence();
            var w = new AsnWriter(AsnEncodingRules.DER);
            w.PushSequence();
            w.PushSequence();
            w.WriteInteger(Status);
            w.PopSequence();
            w.PushSequence(); // ContentInfo
            w.WriteObjectIdentifier("1.2.840.113549.1.7.2");
            var explicit0 = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            w.PushSequence(explicit0);
            w.PushSequence(); // SignedData
            w.WriteInteger(3);
            w.PushSetOf();
            w.PopSetOf();
            w.PushSequence();
            w.WriteObjectIdentifier("1.2.840.113549.1.9.16.1.4");
            w.PushSequence(explicit0);
            w.WriteOctetString(info.Encode());
            w.PopSequence(explicit0);
            w.PopSequence();
            w.PopSequence();
            w.PopSequence(explicit0);
            w.PopSequence();
            w.PopSequence();
            return w.Encode();
        }
    }
}
