using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Fadenstack.Chat;
using Fadenstack.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fadenstack.Endpoints;

/// <summary>
/// A person's own OpenAI-compatible endpoint as an <see cref="IChatClient"/> (sdk/51 F03): <c>{base}/chat/completions</c>,
/// with the endpoint's model and dialect, under the transport rules of <see cref="ModelEndpoint"/>. Unlike the gateway's
/// client it sends no <c>X-Faden-*</c> header and none of the gateway's fields (<c>session_id</c>, <c>skills</c>, the person's
/// name), and it reads nothing of the gateway's from the answer: an endpoint cannot hand the agent a refusal, an approval, an
/// event or a provenance receipt. The system message comes from <see cref="ChatOptions.Instructions"/>, which the agent's
/// session sets above the chat stages.
/// </summary>
public sealed class DirectChatClient : IChatClient
{
    private readonly EndpointTransport _transport;
    private readonly bool _ownsTransport;
    private readonly ChatClientMetadata _metadata;
    private readonly ILogger _log;

    public DirectChatClient(ModelEndpoint endpoint) : this(new EndpointTransport(endpoint), ownsTransport: true)
    {
    }

    internal DirectChatClient(EndpointTransport transport, bool ownsTransport)
    {
        _transport = transport;
        _ownsTransport = ownsTransport;
        _log = (transport.Endpoint.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger(Log.Category);
        _metadata = new ChatClientMetadata("openai-compatible", transport.Base, transport.Endpoint.Model);
    }

    /// <summary>The endpoint this client talks to.</summary>
    public ModelEndpoint Endpoint => _transport.Endpoint;

    private EndpointDialect Dialect => _transport.Endpoint.Dialect;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = EndpointWire.Request(messages, options, Model(options), stream: false, Dialect).ToJsonString();
        using var response = await _transport.SendAsync(EndpointUrl.ChatCompletions(_transport.Base), url => Build(url, body),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var text = await EndpointTransport.TextOf(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw EndpointTransport.ToException(response, text, Endpoint.Key is not null);
        }
        var answer = Answer(text);
        Mark(answer.AdditionalProperties ??= new AdditionalPropertiesDictionary(), response, options);
        return answer;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = EndpointWire.Request(messages, options, Model(options), stream: true, Dialect).ToJsonString();
        using var response = await _transport.SendAsync(EndpointUrl.ChatCompletions(_transport.Base), url => Build(url, body),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await EndpointTransport.TextOf(response, cancellationToken).ConfigureAwait(false);
            throw EndpointTransport.ToException(response, error, Endpoint.Key is not null);
        }
        var marking = new AdditionalPropertiesDictionary();
        Mark(marking, response, options);
        if (response.Content?.Headers.ContentType?.MediaType is "application/json")
        {
            // A server that answers a streamed request with one whole answer (review R2): read it as one, never as nothing.
            var whole = Answer(await EndpointTransport.TextOf(response, cancellationToken).ConfigureAwait(false));
            var updates = whole.ToChatResponseUpdates();
            foreach (var update in updates)
            {
                yield return update;
            }
            yield return new ChatResponseUpdate { AdditionalProperties = marking };
            yield break;
        }
        // A read on a dead connection cannot be cancelled: the watchdog gives the stream up when it stays silent.
        using var stream = new IdleTimeoutStream(response.Content is null ? Stream.Null
            : await response.Content.ReadAsStreamAsync().ConfigureAwait(false), Endpoint.StreamIdleTimeout);
        using var closeOnCancel = cancellationToken.Register(() => stream.Dispose());
        var assembler = new StreamAssembler(_log, gateway: false);
        await foreach (var sse in SseReader.ReadAsync(stream, SseLimits.Endpoint, cancellationToken).ConfigureAwait(false))
        {
            foreach (var update in assembler.Read(sse))
            {
                yield return update;
            }
            if (assembler.Done)
            {
                break;
            }
        }
        foreach (var update in assembler.Flush())
        {
            yield return update;
        }
        yield return new ChatResponseUpdate { AdditionalProperties = marking };
    }

    /// <summary>A whole answer, read as an OpenAI chat completion; anything else is not compatible.</summary>
    private static ChatResponse Answer(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new EndpointNotCompatibleException(System.Net.HttpStatusCode.OK,
                "The endpoint's answer is not an OpenAI chat completion (not JSON): check the base, which includes /v1.");
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("choices", out _))
            {
                throw new EndpointNotCompatibleException(System.Net.HttpStatusCode.OK,
                    "The endpoint's answer is not an OpenAI chat completion (no choices): check the base, which includes /v1.");
            }
            return ChatWire.Response(doc.RootElement, gateway: false);
        }
    }

    private void Mark(AdditionalPropertiesDictionary properties, HttpResponseMessage response, ChatOptions? options) =>
        properties[FadenstackChatOptions.EndpointMarking] = new EndpointMarking
        {
            Requested = Dialect.RequestWatermark && !EndpointWire.IsUnread(options),
            Confirmed = EndpointMarking.Header(FadenstackTransport.HeaderValue(response, "x-watermark")),
            Deterministic = EndpointWire.Deterministic(options),
        };

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? _metadata
        : serviceType == typeof(ModelEndpoint) ? Endpoint
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }

    /// <summary>The request's model, else the endpoint's; the gateway's <c>"agent"</c> never goes to an endpoint.</summary>
    private string Model(ChatOptions? options) =>
        options?.ModelId is { Length: > 0 } id ? id
        : Endpoint.Model is { Length: > 0 } model ? model
        : throw new InvalidOperationException("Set ModelEndpoint.Model (or ChatOptions.ModelId): the endpoint needs to know which model.");

    private static HttpRequestMessage Build(Uri url, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}
