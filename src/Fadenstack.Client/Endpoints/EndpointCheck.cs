using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fadenstack.Chat;
using Microsoft.Extensions.AI;

namespace Fadenstack.Endpoints;

/// <summary>What the check found wrong or worth saying, for the wizard (F15) to say in plain words.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EndpointProblem
{
    Unreachable,
    CertificateNotTrusted,
    PlainHttpRefused,
    KeyRejected,
    OriginRefused,
    NotCompatible,
    RateLimited,
    ModelNotFound,

    /// <summary>The model answered, but no tool call came back: the agent can only talk ("chat only").</summary>
    NoToolCalls,

    /// <summary>A stream brought no usage figures: the token counts stay unknown.</summary>
    NoUsage,

    /// <summary>The context is shorter than the instructions, tools and history need.</summary>
    ContextTooShort,

    /// <summary>Ollama runs the model with its default context length, which cuts long prompts without a word.</summary>
    OllamaDefaultContext,

    /// <summary>The server refused the request for its watermark; requests go without it.</summary>
    WatermarkRequestRefused,

    /// <summary>The key travels unencrypted, on this computer only (plain http to loopback; owner, 14 Nov).</summary>
    KeyUnencryptedOnThisComputer,

    Failed,
}

/// <summary>Where an endpoint's address points (<see cref="EndpointCheck.PlaceOfAsync"/>).</summary>
public enum EndpointPlace
{
    /// <summary>Loopback: 127.0.0.0/8, ::1, <c>localhost</c>.</summary>
    ThisComputer,

    /// <summary>A private range: RFC 1918, link-local, fc00::/7.</summary>
    LocalNetwork,

    /// <summary>Anything else, CGNAT included, or an address that cannot be resolved.</summary>
    Internet,
}

/// <summary>What the check is to do.</summary>
public sealed record EndpointCheckOptions
{
    /// <summary>
    /// Also send a small green picture and ask for its colour; the model sees pictures only if the answer says "green"
    /// (optional; the person may set it instead).
    /// </summary>
    public bool ProbeVision { get; init; }

    /// <summary>The context length the person set, which wins over what the server says.</summary>
    public int? ContextLength { get; init; }

    /// <summary>About how many tokens the instructions, the tools and the history need: below it the check warns.</summary>
    public int? RequiredContextTokens { get; init; }
}

/// <summary>The check's result (sdk/51 F03): serialisable, so the app keeps it with the connection and the records cite it (F10).</summary>
public sealed record EndpointReport
{
    public Uri BaseUrl { get; init; } = null!;
    public string Model { get; init; } = "";
    public DateTimeOffset CheckedAt { get; init; }
    public EndpointServer Server { get; init; }
    public string? ServerVersion { get; init; }

    /// <summary>The dialect the check found fits (a preset's, or the server's own when the check recognised it).</summary>
    public string Dialect { get; init; } = EndpointDialect.OpenAICompatible.Name;

    public bool Reachable { get; init; }

    /// <summary>Whether the key (or no key) was accepted; null when the check did not get that far.</summary>
    public bool? KeyAccepted { get; init; }

    /// <summary>The models <c>/models</c> lists; empty when it lists none or has no such route.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>Whether the chosen model answered a one-token streamed chat.</summary>
    public bool? Chat { get; init; }

    /// <summary>Whether a stream brought usage figures.</summary>
    public bool? Usage { get; init; }

    /// <summary>Whether the model calls tools; false means the agent can only talk.</summary>
    public bool? ToolCalls { get; init; }

    /// <summary>
    /// Whether the model saw a picture: its answer to a solid green one named "green". False for any other answer, an empty
    /// one, a refusal ("I can't see images") or an API error; null when not probed.
    /// </summary>
    public bool? Vision { get; init; }

    /// <summary>
    /// The context length that applies: the person's, else the server's running setting (Ollama: the model's <c>num_ctx</c>,
    /// else the length of the loaded model in <c>/api/ps</c>), else null (unknown).
    /// </summary>
    public int? ContextLength { get; init; }

    /// <summary>The model's own maximum, where the server says (Ollama's model_info, vLLM's max_model_len).</summary>
    public int? ModelContextLength { get; init; }

    /// <summary>Pictures: off in standalone until D17 (owner, 14 Nov), so always false here.</summary>
    public bool Pictures { get; init; }

    public EndpointWatermarkStatus Watermark { get; init; } = new();

    public IReadOnlyList<EndpointProblem> Problems { get; init; } = [];

    /// <summary>The agent can only talk: the model calls no tools.</summary>
    [JsonIgnore]
    public bool ChatOnly => ToolCalls == false;

    /// <summary>Everything the agent needs worked: reachable, the key, a chat.</summary>
    [JsonIgnore]
    public bool Usable => Reachable && KeyAccepted != false && Chat == true;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static EndpointReport FromJson(string json) =>
        JsonSerializer.Deserialize<EndpointReport>(json, Json) ?? throw new FormatException("Not an endpoint report.");

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

/// <summary>A server the local search found on this computer.</summary>
public sealed record LocalEndpoint
{
    public Uri BaseUrl { get; init; } = null!;
    public EndpointPreset Preset { get; init; } = EndpointPreset.Ollama;
    public IReadOnlyList<string> Models { get; init; } = [];
}

/// <summary>
/// Checks an endpoint step by step (sdk/51 F03): <c>/models</c> (reachable, the key, the models), a one-token streamed chat
/// (usage figures), the tool-call probe (a dummy function with <c>tool_choice: "required"</c>), optionally a picture, the
/// context length (Ollama's <c>/api/show</c> for the maximum and <c>/api/ps</c> for the length the loaded model runs with, vLLM's <c>/models</c>, LM Studio's own API, or the person's setting), and the
/// text watermark status from the sourced table. Every request goes to the endpoint's own origin and nowhere else.
/// </summary>
public sealed class EndpointCheck
{
    private const string ProbeTool = "report_ready";

    private readonly ModelEndpoint _endpoint;

    public EndpointCheck(ModelEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    /// <summary>The ports the local search tries on this computer: Ollama, LM Studio, vLLM, llama.cpp.</summary>
    public static IReadOnlyList<(int Port, EndpointPreset Preset)> LocalPorts { get; } =
    [
        (11434, EndpointPreset.Ollama), (1234, EndpointPreset.LMStudio), (8000, EndpointPreset.Vllm), (8080, EndpointPreset.LlamaCpp),
    ];

    /// <summary>
    /// Looks for model servers on this computer (loopback only, short timeouts), only when the person agreed: without
    /// <paramref name="personConsented"/> nothing is sent at all.
    /// </summary>
    public static async Task<IReadOnlyList<LocalEndpoint>> FindLocalAsync(bool personConsented, TimeSpan? timeout = null,
        HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        if (!personConsented)
        {
            return [];
        }
        var wait = timeout ?? TimeSpan.FromSeconds(1);
        var probes = LocalPorts.Select(async p =>
        {
            var baseUrl = new Uri($"http://127.0.0.1:{p.Port}/v1");
            using var transport = new EndpointTransport(new ModelEndpoint { BaseUrl = baseUrl, Timeout = wait, Handler = handler });
            try
            {
                var (models, owner) = await ModelsAsync(transport, cancellationToken).ConfigureAwait(false);
                var preset = Recognise(owner, p.Preset.Server) switch
                {
                    EndpointServer.Sglang => EndpointPreset.Sglang,
                    EndpointServer.Vllm => EndpointPreset.Vllm,
                    EndpointServer.LlamaCpp => EndpointPreset.LlamaCpp,
                    _ => p.Preset,
                };
                return new LocalEndpoint { BaseUrl = baseUrl, Preset = preset, Models = models };
            }
            catch (FadenstackException)
            {
                return null; // nothing there, or not a model server
            }
        });
        return (await Task.WhenAll(probes).ConfigureAwait(false)).Where(e => e is not null).ToList()!;
    }

    public async Task<EndpointReport> RunAsync(EndpointCheckOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new EndpointCheckOptions();
        var problems = new List<EndpointProblem>();
        var server = ByHost(_endpoint.BaseUrl) ?? ByDialect(_endpoint.Dialect);
        var report = new EndpointReport
        {
            BaseUrl = _endpoint.BaseUrl, Model = _endpoint.Model, CheckedAt = DateTimeOffset.UtcNow, Server = server,
            Dialect = _endpoint.Dialect.Name, Watermark = TextWatermarks.For(server, null, _endpoint.Dialect.RequestWatermark, null),
        };
        using var listed = new EndpointTransport(_endpoint);
        var transport = listed;
        if (_endpoint.Key is not null && _endpoint.BaseUrl.Scheme == Uri.UriSchemeHttp)
        {
            problems.Add(EndpointProblem.KeyUnencryptedOnThisComputer); // said, even where it is allowed
        }

        // 1. /models: reachable, the key, the models.
        JsonElement? listing = null;
        try
        {
            var (models, owner, raw) = await ModelsWithRawAsync(transport, cancellationToken).ConfigureAwait(false);
            listing = raw;
            server = server == EndpointServer.Unknown ? Recognise(owner, server) : server;
            report = report with { Reachable = true, KeyAccepted = true, Models = models, Server = server };
            if (models.Count > 0 && _endpoint.Model.Length > 0 && !models.Contains(_endpoint.Model, StringComparer.Ordinal))
            {
                problems.Add(EndpointProblem.ModelNotFound);
            }
        }
        catch (EndpointNotCompatibleException)
        {
            report = report with { Reachable = true }; // some servers have no /models; the chat decides
        }
        catch (Exception e) when (Stop(e, problems) is { } stop)
        {
            return Finish(report with { Reachable = stop.Reachable, KeyAccepted = stop.KeyAccepted }, problems);
        }

        // No model named yet (the wizard checks before the person picks one, F15): the first listed model stands in for the
        // probes, and the report names it.
        var endpoint = _endpoint;
        EndpointTransport? chosen = null;
        if (endpoint.Model.Length == 0 && report.Models.Count > 0)
        {
            endpoint = endpoint with { Model = report.Models[0] };
            report = report with { Model = endpoint.Model };
            transport = chosen = new EndpointTransport(endpoint);
        }
        using var chosenTransport = chosen;

        // The server's version where it says (vLLM's and SGLang's /version), for the watermark table.
        string? version = null;
        if (server is EndpointServer.Vllm or EndpointServer.Sglang)
        {
            version = await VersionAsync(transport, cancellationToken).ConfigureAwait(false);
        }

        // 2. A one-token streamed chat: does it answer, do usage figures come back?
        var client = new DirectChatClient(transport, ownsTransport: false);
        var watermarkRequested = _endpoint.Dialect.RequestWatermark;
        bool? confirmed = null;
        try
        {
            var (_, usage, marking) = await ProbeChatAsync(client, cancellationToken).ConfigureAwait(false);
            confirmed = marking?.Confirmed;
            report = report with { Reachable = true, KeyAccepted = true, Chat = true, Usage = usage };
            if (!usage)
            {
                problems.Add(EndpointProblem.NoUsage);
            }
        }
        catch (FadenstackApiException e) when (watermarkRequested && (int)e.Status is 400 or 422 && Mentions(e, "watermark"))
        {
            // The server refused the request for its mark (not configured): requests go without it, the report says so.
            watermarkRequested = false;
            problems.Add(EndpointProblem.WatermarkRequestRefused);
            client = new DirectChatClient(new EndpointTransport(endpoint with { Dialect = endpoint.Dialect with { RequestWatermark = false } }),
                ownsTransport: true);
            try
            {
                var (_, usage, marking) = await ProbeChatAsync(client, cancellationToken).ConfigureAwait(false);
                confirmed = marking?.Confirmed;
                report = report with { Reachable = true, KeyAccepted = true, Chat = true, Usage = usage };
            }
            catch (Exception again) when (Stop(again, problems) is { } stop)
            {
                return Finish(report with { Reachable = stop.Reachable, KeyAccepted = stop.KeyAccepted, Chat = false }, problems,
                    version, watermarkRequested, confirmed);
            }
        }
        catch (Exception e) when (Stop(e, problems) is { } stop)
        {
            return Finish(report with { Reachable = stop.Reachable, KeyAccepted = stop.KeyAccepted, Chat = false }, problems,
                version, watermarkRequested, confirmed);
        }

        // 3. The tool-call probe.
        try
        {
            var called = await ProbeToolsAsync(client, cancellationToken).ConfigureAwait(false);
            report = report with { ToolCalls = called };
        }
        catch (FadenstackApiException e) when ((int)e.Status is 400 or 422 or 500)
        {
            report = report with { ToolCalls = false }; // "does not support tools"
        }
        if (report.ToolCalls == false)
        {
            problems.Add(EndpointProblem.NoToolCalls);
        }

        // 4. A picture, when asked for: only an answer that names the colour (green) counts; a model that cannot see answers anyway.
        if (options.ProbeVision)
        {
            try
            {
                var answer = await client.GetResponseAsync([new ChatMessage(ChatRole.User,
                    [new TextContent("What colour is this picture? One word."), new DataContent(GreenPicture, "image/png")])],
                    new ChatOptions { MaxOutputTokens = VisionMaxTokens }, cancellationToken).ConfigureAwait(false);
                report = report with { Vision = NamesGreen(answer.Text) }; // the answer text only, not the reasoning
            }
            catch (FadenstackApiException)
            {
                report = report with { Vision = false };
            }
        }

        // 5. The context length.
        var (running, maximum) = await ContextAsync(transport, server, listing, cancellationToken).ConfigureAwait(false);
        var context = options.ContextLength ?? running;
        report = report with { ContextLength = context, ModelContextLength = maximum };
        if (server == EndpointServer.Ollama && options.ContextLength is null && running is null)
        {
            problems.Add(EndpointProblem.OllamaDefaultContext);
        }
        if (options.RequiredContextTokens is { } needed && (context ?? maximum) is { } have && have < needed)
        {
            problems.Add(EndpointProblem.ContextTooShort);
        }
        if (client.Endpoint != endpoint)
        {
            client.Dispose();
        }
        return Finish(report, problems, version, watermarkRequested, confirmed);
    }

    private static EndpointReport Finish(EndpointReport report, List<EndpointProblem> problems, string? version = null,
        bool? requested = null, bool? confirmed = null) => report with
    {
        ServerVersion = version ?? report.ServerVersion,
        Watermark = TextWatermarks.For(report.Server, version, requested ?? report.Watermark.Requested, confirmed),
        Problems = problems.Distinct().ToList(),
        Pictures = false, // standalone has no pictures until D17 (owner, 14 Nov)
    };

    /// <summary>The problem an exception means, and whether the check goes on; null for an exception the check does not know.</summary>
    private static (bool Reachable, bool? KeyAccepted)? Stop(Exception e, List<EndpointProblem> problems)
    {
        switch (e)
        {
            case EndpointUnreachableException:
                problems.Add(EndpointProblem.Unreachable);
                return (false, null);
            case ServerCertificateException:
                problems.Add(EndpointProblem.CertificateNotTrusted);
                return (false, null);
            case EndpointPlainHttpRefusedException:
                problems.Add(EndpointProblem.PlainHttpRefused);
                return (false, null);
            case EndpointKeyRejectedException:
                problems.Add(EndpointProblem.KeyRejected);
                return (true, false);
            case EndpointOriginRefusedException:
                problems.Add(EndpointProblem.OriginRefused);
                return (true, null);
            case EndpointModelNotFoundException:
                problems.Add(EndpointProblem.ModelNotFound);
                return (true, true);
            case EndpointNotCompatibleException:
                problems.Add(EndpointProblem.NotCompatible);
                return (true, null);
            case FadenstackRateLimitedException:
                problems.Add(EndpointProblem.RateLimited);
                return (true, true);
            case FadenstackException:
                problems.Add(EndpointProblem.Failed);
                return (true, null);
            default:
                return null;
        }
    }

    private static bool Mentions(Exception e, string word) => e.Message.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0
        || (e is FadenstackApiException api && api.Code?.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);

    private static async Task<(string Text, bool Usage, EndpointMarking? Marking)> ProbeChatAsync(DirectChatClient client,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var usage = false;
        EndpointMarking? marking = null;
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Reply with OK.")],
            new ChatOptions { MaxOutputTokens = 1 }, cancellationToken).ConfigureAwait(false))
        {
            text.Append(update.Text);
            usage |= update.Contents.OfType<UsageContent>().Any();
            if (update.AdditionalProperties?.TryGetValue(FadenstackChatOptions.EndpointMarking, out var m) == true)
            {
                marking = m as EndpointMarking;
            }
        }
        return (text.ToString(), usage, marking);
    }

    private static async Task<bool> ProbeToolsAsync(DirectChatClient client, CancellationToken cancellationToken)
    {
        var tool = AIFunctionFactory.Create(() => "ready", ProbeTool, "Reports that the assistant is ready. Call it now.");
        var options = new ChatOptions
        {
            Tools = [tool], ToolMode = ChatToolMode.RequireAny, MaxOutputTokens = 256,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
        };
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, $"Call the tool {ProbeTool} now, with no arguments.")], options, cancellationToken)
            .ConfigureAwait(false))
        {
            if (update.Contents.OfType<FunctionCallContent>().Any(c => c.Name == ProbeTool))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<(List<string> Models, string? Owner)> ModelsAsync(EndpointTransport transport,
        CancellationToken cancellationToken)
    {
        var (models, owner, _) = await ModelsWithRawAsync(transport, cancellationToken).ConfigureAwait(false);
        return (models, owner);
    }

    private static async Task<(List<string> Models, string? Owner, JsonElement Raw)> ModelsWithRawAsync(EndpointTransport transport,
        CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync(transport, EndpointUrl.Models(transport.Base), cancellationToken).ConfigureAwait(false);
        if (json is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new EndpointNotCompatibleException(System.Net.HttpStatusCode.OK, "The endpoint's /models is not an OpenAI model list.");
        }
        var models = new List<string>();
        string? owner = null;
        foreach (var model in data.EnumerateArray())
        {
            if (model.ValueKind == JsonValueKind.Object && model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                models.Add(id.GetString()!);
                owner ??= model.TryGetProperty("owned_by", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            }
        }
        return (models, owner, root.Clone());
    }

    private static async Task<JsonElement?> GetJsonAsync(EndpointTransport transport, Uri url, CancellationToken cancellationToken,
        object? body = null)
    {
        using var response = await transport.SendAsync(url, u =>
        {
            var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, u);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
            return request;
        }, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var text = await EndpointTransport.TextOf(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw EndpointTransport.ToException(response, text, transport.Endpoint.Key is not null);
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new EndpointNotCompatibleException(response.StatusCode, "The endpoint's answer is not JSON.");
        }
    }

    private static async Task<string?> VersionAsync(EndpointTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            var json = await GetJsonAsync(transport, EndpointUrl.Root(transport.Base, "version"), cancellationToken).ConfigureAwait(false);
            return json is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty("version", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;
        }
        catch (FadenstackException)
        {
            return null;
        }
    }

    /// <summary>The length Ollama runs the loaded model with (<c>/api/ps</c>); null when it does not say.</summary>
    private static async Task<int?> OllamaRunningAsync(EndpointTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            var ps = await GetJsonAsync(transport, EndpointUrl.Root(transport.Base, "api/ps"), cancellationToken).ConfigureAwait(false);
            if (ps is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var wanted = transport.Endpoint.Model;
            bool Same(JsonElement entry, string name) => entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                && (v.GetString() == wanted || v.GetString() == wanted + ":latest");
            foreach (var entry in models.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && (Same(entry, "name") || Same(entry, "model"))
                    && entry.TryGetProperty("context_length", out var length) && length.TryGetInt32(out var n))
                {
                    return n;
                }
            }
            return null;
        }
        catch (FadenstackException)
        {
            return null;
        }
    }

    /// <summary>The running context length and the model's maximum, where the server says.</summary>
    private async Task<(int? Running, int? Maximum)> ContextAsync(EndpointTransport transport, EndpointServer server, JsonElement? listing,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (server)
            {
                case EndpointServer.Ollama:
                {
                    var show = await GetJsonAsync(transport, EndpointUrl.Root(transport.Base, "api/show"), cancellationToken,
                        new { model = transport.Endpoint.Model }).ConfigureAwait(false);
                    if (show is not { ValueKind: JsonValueKind.Object } s)
                    {
                        return (await OllamaRunningAsync(transport, cancellationToken).ConfigureAwait(false), null);
                    }
                    int? maximum = null;
                    if (s.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in info.EnumerateObject())
                        {
                            if (p.Name.EndsWith(".context_length", StringComparison.Ordinal) && p.Value.TryGetInt32(out var n))
                            {
                                maximum = n;
                            }
                        }
                    }
                    int? running = null;
                    if (s.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.String)
                    {
                        foreach (var line in parameters.GetString()!.Split('\n'))
                        {
                            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 2 && parts[0] == "num_ctx" && int.TryParse(parts[1], out var n))
                            {
                                running = n;
                            }
                        }
                    }
                    // No num_ctx in the model: the server's own default (OLLAMA_CONTEXT_LENGTH) applies, which only /api/ps of the
                    // loaded model shows (the chat probe loaded it).
                    running ??= await OllamaRunningAsync(transport, cancellationToken).ConfigureAwait(false);
                    return (running, maximum);
                }
                case EndpointServer.Vllm or EndpointServer.Sglang when listing is { } l:
                {
                    foreach (var model in l.GetProperty("data").EnumerateArray())
                    {
                        if (model.TryGetProperty("id", out var id) && id.GetString() == transport.Endpoint.Model
                            && model.TryGetProperty("max_model_len", out var max) && max.TryGetInt32(out var n))
                        {
                            return (n, n); // the server runs with it
                        }
                    }
                    return (null, null);
                }
                case EndpointServer.LMStudio:
                {
                    var model = await GetJsonAsync(transport, EndpointUrl.Root(transport.Base,
                        "api/v0/models/" + Uri.EscapeDataString(transport.Endpoint.Model)), cancellationToken).ConfigureAwait(false);
                    int? Read(string name) => model is { ValueKind: JsonValueKind.Object } m && m.TryGetProperty(name, out var v)
                        && v.TryGetInt32(out var n) ? n : null;
                    return (Read("loaded_context_length"), Read("max_context_length"));
                }
                default:
                    return (null, null);
            }
        }
        catch (FadenstackException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The text watermark status of an endpoint from the sourced table alone, by its host and dialect, with no request: what
    /// applies when no check report is at hand (a vLLM or SGLang server of unknown version: only if its operator configured it).
    /// </summary>
    public static EndpointWatermarkStatus WatermarkOf(ModelEndpoint endpoint)
    {
        if (endpoint is null)
        {
            throw new ArgumentNullException(nameof(endpoint));
        }
        return TextWatermarks.For(ServerOf(endpoint), null, endpoint.Dialect.RequestWatermark, null);
    }

    /// <summary>
    /// Where an endpoint's address points, for the wizard's plain-http step (sdk/51 I15 §3, D3): decided on the resolved
    /// addresses, not the name, so a name that resolves to one public address is the internet. An address that cannot be
    /// resolved is the internet too. The transport decides again on the address it connects to.
    /// </summary>
    public static async Task<EndpointPlace> PlaceOfAsync(Uri baseUrl, CancellationToken cancellationToken = default)
    {
        if (baseUrl is null)
        {
            throw new ArgumentNullException(nameof(baseUrl));
        }
        return await NetworkAddress.OfHostAsync(baseUrl.Host, cancellationToken).ConfigureAwait(false) switch
        {
            AddressScope.Loopback => EndpointPlace.ThisComputer,
            AddressScope.Private => EndpointPlace.LocalNetwork,
            _ => EndpointPlace.Internet,
        };
    }

    /// <summary>The kind of server behind an endpoint by its host and dialect alone, with no request.</summary>
    public static EndpointServer ServerOf(ModelEndpoint endpoint) =>
        (endpoint?.BaseUrl is null ? null : ByHost(endpoint.BaseUrl)) ?? ByDialect(endpoint?.Dialect ?? EndpointDialect.OpenAICompatible);

    /// <summary>The server by the endpoint's host, for the clouds whose host says it.</summary>
    internal static EndpointServer? ByHost(Uri baseUrl)
    {
        var host = baseUrl.Host.ToLowerInvariant();
        bool Is(string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        return host switch
        {
            _ when Is("api.openai.com") => EndpointServer.OpenAI,
            _ when Is("openai.azure.com") || Is("cognitiveservices.azure.com") || Is("services.ai.azure.com") => EndpointServer.AzureOpenAI,
            _ when Is("api.anthropic.com") => EndpointServer.Anthropic,
            _ when Is("api.x.ai") => EndpointServer.XAi,
            _ when Is("generativelanguage.googleapis.com") => EndpointServer.Google,
            _ when Is("api.mistral.ai") => EndpointServer.Mistral,
            _ when Is("openrouter.ai") => EndpointServer.OpenRouter,
            _ => null,
        };
    }

    internal static EndpointServer ByDialect(EndpointDialect dialect) => dialect.Name switch
    {
        "openai" => EndpointServer.OpenAI,
        "azure-openai" => EndpointServer.AzureOpenAI,
        "ollama" => EndpointServer.Ollama,
        "lm-studio" => EndpointServer.LMStudio,
        "vllm" => EndpointServer.Vllm,
        "sglang" => EndpointServer.Sglang,
        "llama.cpp" => EndpointServer.LlamaCpp,
        "litellm" => EndpointServer.LiteLlm,
        "openrouter" => EndpointServer.OpenRouter,
        "mistral" => EndpointServer.Mistral,
        _ => EndpointServer.Unknown,
    };

    /// <summary>The server by <c>/models</c>' <c>owned_by</c>, where it says one.</summary>
    private static EndpointServer Recognise(string? owner, EndpointServer fallback) => owner?.ToLowerInvariant() switch
    {
        "vllm" => EndpointServer.Vllm,
        "sglang" => EndpointServer.Sglang,
        "llamacpp" or "llama.cpp" => EndpointServer.LlamaCpp,
        _ => fallback,
    };

    /// <summary>
    /// A 64x64 PNG of one solid green (#00A000) for the vision probe (SHA-256 of the bytes:
    /// 931d03be1da351bf941b2244ee433b9e69215f8294432340c1c64ab818d2a119; the TypeScript SDK carries the same bytes). A model
    /// that cannot see the picture has nothing to name, and the probe asks it for the colour.
    /// </summary>
    internal static readonly byte[] GreenPicture = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAATElEQVR42u3PQQkAAAgAseufwqhG8C0MVmA1/SYgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgcFlT0gCXrcLQywAAAABJRU5ErkJggg==");

    /// <summary>Enough for a reasoning model to think and then answer; only the answer text is read.</summary>
    private const int VisionMaxTokens = 256;

    /// <summary>The model saw the picture when its answer names the colour: the word "green", whole, in any case.</summary>
    internal static bool NamesGreen(string? answer) => !string.IsNullOrEmpty(answer)
        && System.Text.RegularExpressions.Regex.IsMatch(answer, @"\bgreen\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
