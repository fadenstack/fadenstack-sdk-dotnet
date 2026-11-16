using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Fadenstack.Auth;
using Fadenstack.Endpoints;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>sdk/51 F03, I03 commit 7: the endpoint check, the presets, the local search and the watermark status.</summary>
public class EndpointCheckTests
{
    private static (EndpointCheck Check, OpenAiFake Fake) Make(string baseUrl = "http://localhost:11434/v1",
        EndpointDialect? dialect = null, ITokenProvider? key = null, string model = "qwen3:14b")
    {
        var fake = new OpenAiFake();
        var check = new EndpointCheck(new ModelEndpoint
        {
            BaseUrl = new Uri(baseUrl), Model = model, Key = key, Dialect = dialect ?? EndpointDialect.Ollama, Handler = fake,
        });
        return (check, fake);
    }

    [Fact]
    public async Task Without_consent_the_local_search_sends_nothing()
    {
        var fake = new OpenAiFake();

        var found = await EndpointCheck.FindLocalAsync(personConsented: false, handler: fake);

        Assert.Empty(found);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task With_consent_it_tries_the_four_loopback_ports_and_reports_what_answers()
    {
        var fake = new OpenAiFake { Models = ["llama3.2:3b", "qwen3:14b"] };
        fake.Intercept = r => r.Url.Port == 11434 ? null : throw new HttpRequestException("refused");

        var found = await EndpointCheck.FindLocalAsync(personConsented: true, TimeSpan.FromMilliseconds(500), fake);

        var ollama = Assert.Single(found);
        Assert.Same(EndpointPreset.Ollama, ollama.Preset);
        Assert.Equal(new[] { "llama3.2:3b", "qwen3:14b" }, ollama.Models);
        Assert.Equal(new[] { 1234, 8000, 8080, 11434 }, fake.Requests.Select(r => r.Url.Port).OrderBy(p => p));
        Assert.All(fake.Requests, r => Assert.Equal("127.0.0.1", r.Url.Host));
        Assert.All(fake.Requests, r => Assert.Equal("/v1/models", r.Url.AbsolutePath));
    }

    [Fact]
    public async Task Each_step_runs_against_the_endpoint_and_nothing_else()
    {
        var (check, fake) = Make();
        fake.Answers.Enqueue(OpenAiFake.Text("OK"));
        fake.Answers.Enqueue(OpenAiFake.Calls(("call-1", "report_ready", "{}")));
        fake.OllamaShow = new { model_info = new Dictionary<string, object> { ["qwen3.context_length"] = 40960 }, parameters = "num_ctx 16384\ntemperature 0.6" };

        var report = await check.RunAsync(new EndpointCheckOptions { RequiredContextTokens = 8000 });

        Assert.True(report.Reachable);
        Assert.True(report.KeyAccepted);
        Assert.Equal(new[] { "qwen3:14b" }, report.Models);
        Assert.True(report.Chat);
        Assert.True(report.Usage);
        Assert.True(report.ToolCalls);
        Assert.False(report.ChatOnly);
        Assert.Equal(16384, report.ContextLength);
        Assert.Equal(40960, report.ModelContextLength);
        Assert.False(report.Pictures);
        Assert.Empty(report.Problems);
        Assert.True(report.Usable);
        Assert.All(fake.Requests, r => Assert.Equal("localhost:11434", r.Url.Authority));
        var probe = fake.Requests.Single(r => r.Body.Contains("report_ready"));
        Assert.Equal("required", probe.Json.GetProperty("tool_choice").GetString());
        Assert.Equal(1, fake.Requests.First(r => r.Url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            .Json.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task An_ollama_endpoint_does_not_mark_text_and_its_default_context_is_said()
    {
        var (check, fake) = Make();
        fake.OllamaShow = new { model_info = new Dictionary<string, object> { ["qwen3.context_length"] = 40960 }, parameters = "temperature 0.6" };

        var report = await check.RunAsync();

        Assert.Equal(EndpointServer.Ollama, report.Server);
        Assert.Equal(TextWatermark.Never, report.Watermark.Status);
        Assert.NotNull(report.Watermark.Source);
        Assert.Equal("2026-11-14", report.Watermark.Checked);
        Assert.Contains(EndpointProblem.OllamaDefaultContext, report.Problems);
        Assert.Null(report.ContextLength);
    }

    [Fact]
    public async Task A_model_that_cannot_call_tools_is_chat_only()
    {
        var (check, fake) = Make();
        fake.Answers.Enqueue(OpenAiFake.Text("OK"));
        fake.Answers.Enqueue(OpenAiFake.Text("I am ready."));

        var report = await check.RunAsync();

        Assert.False(report.ToolCalls);
        Assert.True(report.ChatOnly);
        Assert.Contains(EndpointProblem.NoToolCalls, report.Problems);
        Assert.True(report.Usable); // it can still talk
    }

    [Fact]
    public async Task A_model_the_server_says_has_no_tools_is_chat_only()
    {
        var (check, fake) = Make();
        fake.Intercept = r => r.Body.Contains("report_ready")
            ? OpenAiFake.Error(400, new { error = new { message = "registry.ollama.ai/library/gemma:2b does not support tools" } })
            : null;

        var report = await check.RunAsync();

        Assert.True(report.ChatOnly);
    }

    [Fact]
    public async Task A_bad_key_stops_the_check_there()
    {
        var (check, fake) = Make("https://api.example.test/v1", EndpointDialect.OpenAI, new StaticTokenProvider("k-bad"));
        fake.Intercept = _ => OpenAiFake.Error(401, new { error = new { message = "Incorrect API key" } });

        var report = await check.RunAsync();

        Assert.True(report.Reachable);
        Assert.False(report.KeyAccepted);
        Assert.Equal(new[] { EndpointProblem.KeyRejected }, report.Problems);
        Assert.False(report.Usable);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task A_key_over_plain_http_to_this_computer_is_said()
    {
        var (check, fake) = Make("http://127.0.0.1:4000/v1", EndpointDialect.LiteLlm, new StaticTokenProvider("k"));

        var report = await check.RunAsync();

        Assert.True(report.Usable);
        Assert.Contains(EndpointProblem.KeyUnencryptedOnThisComputer, report.Problems);
        Assert.Contains("\"KeyUnencryptedOnThisComputer\"", report.ToJson());
        Assert.All(fake.Requests, r => Assert.Equal("127.0.0.1", r.Url.Host));
    }

    [Fact]
    public async Task Nothing_listening_is_unreachable()
    {
        var (check, fake) = Make();
        fake.Intercept = _ => throw new HttpRequestException("No connection could be made");

        var report = await check.RunAsync();

        Assert.False(report.Reachable);
        Assert.Equal(new[] { EndpointProblem.Unreachable }, report.Problems);
    }

    [Fact]
    public async Task A_model_the_server_does_not_list_is_named()
    {
        var (check, fake) = Make(model: "qwen9:1t");

        var report = await check.RunAsync();

        Assert.Contains(EndpointProblem.ModelNotFound, report.Problems);
    }

    [Fact]
    public async Task A_stream_without_usage_figures_is_said_and_a_short_context_warned()
    {
        var (check, fake) = Make(dialect: EndpointDialect.OpenAICompatible);
        fake.StreamUsage = false;

        var report = await check.RunAsync(new EndpointCheckOptions { ContextLength = 4096, RequiredContextTokens = 12000 });

        Assert.False(report.Usage);
        Assert.Equal(4096, report.ContextLength);
        Assert.Contains(EndpointProblem.NoUsage, report.Problems);
        Assert.Contains(EndpointProblem.ContextTooShort, report.Problems);
    }

    [Theory]
    [InlineData("0.29.1", TextWatermark.Never)]
    [InlineData("0.30.0", TextWatermark.IfOperatorConfigured)]
    [InlineData("0.33.0", TextWatermark.IfOperatorConfigured)]
    [InlineData(null, TextWatermark.IfOperatorConfigured)]
    public async Task A_vllm_server_marks_only_from_0_30_and_only_if_configured(string? version, TextWatermark status)
    {
        var (check, fake) = Make("http://localhost:8000/v1", EndpointDialect.OpenAICompatible);
        fake.VllmVersion = version;
        fake.Intercept = r => r.Url.AbsolutePath == "/v1/models"
            ? OpenAiFake.Json(new { @object = "list", data = new[] { new { id = "qwen3:14b", owned_by = "vllm", max_model_len = 32768 } } })
            : null;

        var report = await check.RunAsync();

        Assert.Equal(EndpointServer.Vllm, report.Server);
        Assert.Equal(version, report.ServerVersion);
        Assert.Equal(status, report.Watermark.Status);
        Assert.Equal(32768, report.ContextLength);
    }

    [Fact]
    public async Task An_sglang_server_is_asked_for_the_mark_and_a_refusal_is_said()
    {
        var (check, fake) = Make("http://localhost:30000/v1", EndpointDialect.Sglang);
        fake.Intercept = r => r.Body.Contains("\"watermark\"")
            ? OpenAiFake.Error(400, new { @object = "error", message = "Watermarking is not enabled on this server.", type = "BadRequestError" })
            : null;

        var report = await check.RunAsync();

        Assert.True(report.Chat);
        Assert.Contains(EndpointProblem.WatermarkRequestRefused, report.Problems);
        Assert.False(report.Watermark.Requested);
        Assert.Contains(fake.Requests, r => r.Body.Contains("\"watermark\""));
        Assert.Contains(fake.Requests, r => r.Url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) && !r.Body.Contains("\"watermark\""));
    }

    [Fact]
    public async Task An_sglang_server_that_takes_the_request_keeps_it()
    {
        var (check, _) = Make("http://localhost:30000/v1", EndpointDialect.Sglang);

        var report = await check.RunAsync();

        Assert.True(report.Watermark.Requested);
        Assert.Equal(TextWatermark.IfOperatorConfigured, report.Watermark.Status);
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1", TextWatermark.Marks)]
    [InlineData("https://api.openai.com/v1", TextWatermark.IfAccountEnabled)]
    [InlineData("https://api.x.ai/v1", TextWatermark.IfAccountEnabled)]
    [InlineData("https://res.openai.azure.com/openai/v1", TextWatermark.Unknown)]
    [InlineData("https://api.mistral.ai/v1", TextWatermark.Unknown)]
    [InlineData("https://openrouter.ai/api/v1", TextWatermark.Unknown)]
    public async Task The_cloud_is_known_by_its_host(string baseUrl, TextWatermark status)
    {
        var (check, _) = Make(baseUrl, EndpointDialect.OpenAICompatible, new StaticTokenProvider("k"));

        var report = await check.RunAsync();

        Assert.Equal(status, report.Watermark.Status);
    }

    [Fact]
    public async Task The_x_watermark_header_is_read()
    {
        var (check, fake) = Make("https://api.x.ai/v1", EndpointDialect.OpenAICompatible, new StaticTokenProvider("k"));
        fake.ChatHeaders["x-watermark"] = "true";

        var report = await check.RunAsync();

        Assert.True(report.Watermark.Confirmed);
    }

    [Fact]
    public async Task The_vision_probe_runs_only_when_asked()
    {
        var (check, fake) = Make();
        var without = await check.RunAsync();
        Assert.Null(without.Vision);
        Assert.DoesNotContain(fake.Requests, r => r.Body.Contains("image_url"));

        var (asked, fake2) = Make();
        fake2.Answers.Enqueue(OpenAiFake.Text("OK"));
        fake2.Answers.Enqueue(OpenAiFake.Calls(("call-1", "report_ready", "{}")));
        fake2.Answers.Enqueue(OpenAiFake.Text("Green."));
        var with = await asked.RunAsync(new EndpointCheckOptions { ProbeVision = true });
        Assert.True(with.Vision);
        Assert.Contains(fake2.Requests, r => r.Body.Contains("image_url"));
    }

    private static async Task<(EndpointReport Report, OpenAiFake Fake)> SeeingAsync(string answer, string? reasoning = null)
    {
        var (check, fake) = Make();
        fake.Answers.Enqueue(OpenAiFake.Text("OK"));
        fake.Answers.Enqueue(OpenAiFake.Calls(("call-1", "report_ready", "{}")));
        fake.Answers.Enqueue(OpenAiFake.Text(answer, reasoning));
        return (await check.RunAsync(new EndpointCheckOptions { ProbeVision = true }), fake);
    }

    [Theory]
    [InlineData("Green.", true)]
    [InlineData("green", true)]
    [InlineData("It is GREEN", true)]
    [InlineData("Blue", false)]
    [InlineData("I cannot see images.", false)]
    [InlineData("", false)]
    [InlineData("greenish", false)]
    public async Task The_vision_probe_says_true_only_for_an_answer_naming_green(string answer, bool expected)
    {
        var (report, _) = await SeeingAsync(answer);
        Assert.Equal(expected, report.Vision);
    }

    [Fact]
    public async Task The_vision_probe_reads_the_answer_not_the_reasoning()
    {
        Assert.False((await SeeingAsync("", "The picture is green, I think.")).Report.Vision);
        Assert.True((await SeeingAsync("Green", "Hmm, blue?")).Report.Vision);
    }

    [Fact]
    public async Task The_vision_probe_sends_a_64x64_green_png_as_an_image_part_and_leaves_room_for_a_reasoning_model()
    {
        var (_, fake) = await SeeingAsync("Green");
        var request = fake.Requests.Single(r => r.Body.Contains("image_url"));
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal(256, json.RootElement.GetProperty("max_tokens").GetInt32());
        var parts = json.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal("What colour is this picture? One word.", parts.Single(p => p.GetProperty("type").GetString() == "text").GetProperty("text").GetString());
        var url = parts.Single(p => p.GetProperty("type").GetString() == "image_url").GetProperty("image_url").GetProperty("url").GetString()!;
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, url, StringComparison.Ordinal);
        var bytes = Convert.FromBase64String(url.Substring(prefix.Length));
        Assert.Equal(64, (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19]); // width
        Assert.Equal(64, (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]); // height
        // The same literal as in the TypeScript tests: the two SDKs send the same picture.
        using var sha = SHA256.Create();
        Assert.Equal("931d03be1da351bf941b2244ee433b9e69215f8294432340c1c64ab818d2a119",
            BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant());
    }

    [Fact]
    public async Task The_vision_probe_is_false_on_an_api_error()
    {
        var (check, fake) = Make();
        fake.Answers.Enqueue(OpenAiFake.Text("OK"));
        fake.Answers.Enqueue(OpenAiFake.Calls(("call-1", "report_ready", "{}")));
        fake.Intercept = r => r.Body.Contains("image_url") ? OpenAiFake.Error(400, "images are not supported") : null;
        Assert.False((await check.RunAsync(new EndpointCheckOptions { ProbeVision = true })).Vision);
    }

    private static object Loaded(string name, int length) => new { models = new[] { new { name, model = name, context_length = length } } };

    [Fact]
    public async Task Ollamas_num_ctx_wins_over_api_ps()
    {
        var (check, fake) = Make();
        fake.OllamaShow = new { model_info = new Dictionary<string, object> { ["qwen3.context_length"] = 40960 }, parameters = "num_ctx 16384" };
        fake.OllamaPs = Loaded("qwen3:14b", 4096);
        var report = await check.RunAsync();
        Assert.Equal(16384, report.ContextLength);
        Assert.DoesNotContain(EndpointProblem.OllamaDefaultContext, report.Problems);
    }

    [Fact]
    public async Task Without_num_ctx_the_loaded_models_length_in_api_ps_is_the_running_one()
    {
        var (check, fake) = Make();
        fake.OllamaShow = new { model_info = new Dictionary<string, object> { ["qwen3.context_length"] = 40960 }, parameters = "temperature 0.6" };
        fake.OllamaPs = new { models = new object[] { new { name = "other:1b", context_length = 2048 }, new { name = "x", model = "qwen3:14b", context_length = 8192 } } };
        var report = await check.RunAsync();
        Assert.Equal(8192, report.ContextLength);
        Assert.Equal(40960, report.ModelContextLength);
        Assert.DoesNotContain(EndpointProblem.OllamaDefaultContext, report.Problems);
        var paths = fake.Requests.Select(r => r.Url.AbsolutePath).ToList(); // after the chat probe loaded the model
        Assert.True(paths.IndexOf("/api/ps") > paths.FindLastIndex(p => p.EndsWith("/chat/completions", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_persons_context_setting_wins_over_api_ps()
    {
        var (check, fake) = Make();
        fake.OllamaShow = new { parameters = "temperature 0.6" };
        fake.OllamaPs = Loaded("qwen3:14b", 8192);
        Assert.Equal(32000, (await check.RunAsync(new EndpointCheckOptions { ContextLength = 32000 })).ContextLength);
    }

    [Fact]
    public async Task Api_ps_without_the_model_leaves_the_length_unknown_and_the_problem()
    {
        var (check, fake) = Make();
        fake.OllamaShow = new { model_info = new Dictionary<string, object> { ["qwen3.context_length"] = 40960 }, parameters = "temperature 0.6" };
        fake.OllamaPs = Loaded("other:1b", 8192);
        var report = await check.RunAsync();
        Assert.Null(report.ContextLength);
        Assert.Contains(EndpointProblem.OllamaDefaultContext, report.Problems);
    }

    [Fact]
    public async Task A_failing_api_ps_leaves_the_length_unknown_and_the_problem()
    {
        var (check, fake) = Make();
        fake.OllamaShow = new { model_info = new Dictionary<string, object> { ["qwen3.context_length"] = 40960 }, parameters = "temperature 0.6" };
        var report = await check.RunAsync(); // the fake answers 404
        Assert.Null(report.ContextLength);
        Assert.Equal(40960, report.ModelContextLength);
        Assert.Contains(EndpointProblem.OllamaDefaultContext, report.Problems);
    }

    [Fact]
    public async Task The_report_round_trips_as_json()
    {
        var (check, fake) = Make();
        fake.OllamaShow = new { parameters = "num_ctx 8192" };
        var report = await check.RunAsync();

        var back = EndpointReport.FromJson(report.ToJson());

        Assert.Equal(report.ToJson(), back.ToJson());
        Assert.Contains("\"watermark\":{\"status\":\"Never\"", report.ToJson());
        Assert.Equal(8192, back.ContextLength);
    }

    [Fact]
    public void Every_preset_has_its_dialect_and_the_local_ones_their_loopback_base()
    {
        Assert.Equal(new[] { "openai", "azure-openai", "ollama", "lm-studio", "vllm", "sglang", "llama.cpp", "litellm", "openrouter", "mistral" },
            EndpointPreset.All.Select(p => p.Id));
        Assert.All(EndpointPreset.All, p => Assert.Equal(p.Id, p.Dialect.Name));
        Assert.All(EndpointPreset.All.Where(p => p.Local), p => Assert.Equal("localhost", p.BaseUrl!.Host));
        Assert.All(EndpointPreset.All.Where(p => p.BaseUrl is { Scheme: "https" }), p => Assert.True(p.NeedsKey));
        Assert.Same(EndpointPreset.Mistral, EndpointPreset.Named("MISTRAL"));
        Assert.Null(EndpointPreset.Named("nope"));
    }

    [Theory]
    [InlineData("0.30.0", 0, 30, 0)]
    [InlineData("v0.5.22", 0, 5, 22)]
    [InlineData("0.33.0rc1+cu130", 0, 33, 0)]
    [InlineData("nightly", -1, 0, 0)]
    public void Versions_read_as_numbers(string text, int major, int minor, int patch)
    {
        var v = TextWatermarks.Parse(text);
        if (major < 0)
        {
            Assert.Null(v);
            return;
        }
        Assert.Equal(new Version(major, minor, patch), v);
    }

    [Fact]
    public async Task An_endpoint_with_no_model_is_probed_with_the_first_model_the_server_lists()
    {
        var (check, fake) = Make(model: "");
        fake.Models = ["llama3.2:3b", "qwen3:14b"];
        fake.Answers.Enqueue(OpenAiFake.Text("OK"));
        fake.Answers.Enqueue(OpenAiFake.Calls(("call-1", "report_ready", "{}")));

        var report = await check.RunAsync();

        Assert.Equal("llama3.2:3b", report.Model);
        Assert.Equal(new[] { "llama3.2:3b", "qwen3:14b" }, report.Models);
        Assert.True(report.Usable);
        Assert.True(report.ToolCalls);
        var chats = fake.Requests.Where(r => r.Url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)).ToList();
        Assert.True(chats.Count >= 2, "the chat probe and the tool-call probe both ask");
        Assert.All(chats, r => Assert.Equal("llama3.2:3b", r.Json.GetProperty("model").GetString()));
    }

    [Fact]
    public async Task An_endpoint_with_a_model_keeps_it_whatever_the_server_lists_first()
    {
        var (check, fake) = Make(model: "qwen3:14b");
        fake.Models = ["llama3.2:3b", "qwen3:14b"];

        var report = await check.RunAsync();

        Assert.Equal("qwen3:14b", report.Model);
        Assert.Equal(new[] { "llama3.2:3b", "qwen3:14b" }, report.Models);
        var chats = fake.Requests.Where(r => r.Url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(chats);
        Assert.All(chats, r => Assert.Equal("qwen3:14b", r.Json.GetProperty("model").GetString()));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1", EndpointPlace.ThisComputer)]
    [InlineData("http://[::1]:1234", EndpointPlace.ThisComputer)]
    [InlineData("http://localhost:8000", EndpointPlace.ThisComputer)]
    [InlineData("http://192.168.1.20", EndpointPlace.LocalNetwork)]
    [InlineData("http://10.0.0.5", EndpointPlace.LocalNetwork)]
    [InlineData("http://[fe80::1]", EndpointPlace.LocalNetwork)]
    [InlineData("http://8.8.8.8", EndpointPlace.Internet)]
    [InlineData("http://100.64.0.1", EndpointPlace.Internet)]
    public async Task PlaceOf_decides_on_the_address(string address, EndpointPlace expected)
    {
        Assert.Equal(expected, await EndpointCheck.PlaceOfAsync(new Uri(address)));
    }

    [Fact]
    public async Task PlaceOf_needs_an_address()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => EndpointCheck.PlaceOfAsync(null!));
    }
}
