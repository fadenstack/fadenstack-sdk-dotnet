using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Fadenstack.Governance;
using Fadenstack.Http;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Chat;

/// <summary>
/// The gateway as an <see cref="IChatClient"/>, over <see cref="HttpClient"/> -- not the OpenAI SDK: the
/// gateway's events (tool rounds, approvals, notices) arrive as <see cref="FadenstackEventContent"/>, and a
/// refused prompt or answer as a <see cref="GovernanceNoticeContent"/> on a <c>content_filter</c> response.
/// For an agent it calls <c>/v1/agents/{agent}/chat/completions</c>; otherwise <c>/v1/chat/completions</c>.
/// </summary>
public sealed class FadenstackChatClient : IChatClient
{
    private readonly FadenstackTransport _transport;
    private readonly FadenstackClientOptions _options;
    private readonly Uri _endpoint;
    private readonly ChatClientMetadata _metadata;
    private readonly ILogger _log;

    internal FadenstackChatClient(FadenstackTransport transport, FadenstackClientOptions options)
    {
        _transport = transport;
        _options = options;
        _log = options.LoggerFactory.CreateLogger(Log.Category);
        _endpoint = options.Agent is { } agent
            ? transport.Gateway($"v1/agents/{Uri.EscapeDataString(agent)}/chat/completions")
            : transport.Gateway("v1/chat/completions");
        _metadata = new ChatClientMetadata("fadenstack", _endpoint, options.Model);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = ChatWire.Request(messages, options, Model(options), stream: false).ToJsonString();
        using var response = await _transport.SendAsync(() => Build(body, options),
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var text = await FadenstackTransport.TextOf(response).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Refused(response, text) ?? throw ErrorReader.ToException(response, text);
        }
        using var doc = JsonDocument.Parse(text);
        var answer = ChatWire.Response(doc.RootElement);
        WithReceipts(answer, ProvenanceHeader.Parse(FadenstackTransport.HeaderValue(response, ProvenanceHeader.Name)));
        return answer;
    }

    /// <summary>
    /// The receipts a whole answer names in <c>X-Faden-Provenance</c>; the first is its text's when it has text, so
    /// without a <c>faden.provenance</c> event the answer gets a receipt with that id.
    /// </summary>
    private static void WithReceipts(ChatResponse answer, IReadOnlyList<string> receipts)
    {
        if (receipts.Count == 0)
        {
            return;
        }
        answer.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        answer.AdditionalProperties[FadenstackChatOptions.Provenance] = receipts;
        if (answer.GetProvenanceReceipt() is null && HasText(answer.Text) && answer.Messages.Count > 0)
        {
            answer.Messages[answer.Messages.Count - 1].Contents.Add(new ProvenanceReceiptContent(null, receipts[0]));
        }
    }

    /// <summary>Whether a text holds anything once normalised, as the gateway decides whether an answer gets a receipt.</summary>
    private static bool HasText(string? text) => Models.ContentHash.Normalize(text ?? "").Length > 0;

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = ChatWire.Request(messages, options, Model(options), stream: true).ToJsonString();
        using var response = await _transport.SendAsync(() => Build(body, options),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await FadenstackTransport.TextOf(response).ConfigureAwait(false);
            var refused = Refused(response, error) ?? throw ErrorReader.ToException(response, error);
            foreach (var update in refused.ToChatResponseUpdates())
            {
                yield return update;
            }
            yield break;
        }
        // A read on a dead connection cannot be cancelled: the watchdog gives the stream up when it stays silent.
        using var stream = new IdleTimeoutStream(response.Content is null ? Stream.Null : await response.Content.ReadAsStreamAsync().ConfigureAwait(false),
            _options.StreamIdleTimeout);
        using var closeOnCancel = cancellationToken.Register(() => stream.Dispose());
        var assembler = new StreamAssembler(_log);
        await foreach (var sse in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
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
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? _metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
        // The transport belongs to the FadenstackClient.
    }

    private string Model(ChatOptions? options) =>
        options?.ModelId ?? _options.Model ?? (_options.Agent is not null
            ? "agent" // the agent's model is the agent's; the field is required by the API
            : throw new InvalidOperationException("Set FadenstackClientOptions.Model or ChatOptions.ModelId."));

    private HttpRequestMessage Build(string body, ChatOptions? options)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_options.GatewayEvents)
        {
            request.Headers.TryAddWithoutValidation("X-Faden-Events", "ag-ui");
        }
        var extra = options?.AdditionalProperties;
        var mode = extra?.TryGetValue(FadenstackChatOptions.ToolMode, out var m) == true
            ? m switch { ToolMode t => t, string s => ToolModes.ParseMode(s) ?? _options.ToolMode, _ => _options.ToolMode }
            : _options.ToolMode;
        request.Headers.TryAddWithoutValidation(ToolModes.Header, mode.ToWire());
        if (extra?.TryGetValue(FadenstackChatOptions.ConversationId, out var conversation) == true && conversation is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Faden-Conversation-Id", conversation.ToString());
        }
        if (extra?.TryGetValue(FadenstackChatOptions.TurnId, out var turn) == true && turn is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Faden-Turn-Id", turn.ToString());
        }
        return request;
    }

    /// <summary>
    /// A prompt the gateway refused (422 policy_blocked) is an answer, not an error. The status and code say it is a
    /// prompt refusal, whatever outcome the notice names.
    /// </summary>
    private static ChatResponse? Refused(HttpResponseMessage response, string body)
    {
        if (response.StatusCode != (HttpStatusCode)422)
        {
            return null;
        }
        var parsed = ErrorReader.Parse(body, "The prompt was refused.");
        if (parsed.Code != "policy_blocked")
        {
            return null;
        }
        var notice = parsed.Fadenstack is { } faden && faden.TryGetProperty("governance", out var g)
            ? ChatWire.Notice(g)
            : new GovernanceNotice { Stage = "input", Message = parsed.Message };
        notice = notice with { Outcome = "prompt_blocked" };
        if (string.IsNullOrEmpty(notice.Message))
        {
            notice = notice with { Message = parsed.Message };
        }
        var refused = new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>()));
        ChatWire.Refused(refused, notice);
        return refused;
    }
}
