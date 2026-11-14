using System.Runtime.CompilerServices;
using System.Text.Json;
using Fadenstack.Governance;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

/// <summary>
/// The agent as a Microsoft Agent Framework <see cref="AIAgent"/> (sdk/51 F02, H1): MAF's workflows and hosting run it, and
/// MAF's parts plug in through MAF's documented contract. It is an additional face, not a new loop: a run is a turn of a
/// <see cref="ChatSession"/>, with the same tools, approvals, policy and local transcript as a native one.
/// </summary>
public sealed partial class FadenstackAgent
{
    /// <summary>The state bag key that holds our session's id: the only thing a serialized MAF session carries.</summary>
    internal const string SessionIdKey = "fadenstack.session_id";

    /// <summary>The key of a response's <see cref="ChatOutcome"/>, as text, in its <c>AdditionalProperties</c>.</summary>
    public const string OutcomeProperty = "fadenstack.outcome";

    /// <summary>The key of a response's whole <see cref="AgentResult"/> in its <c>AdditionalProperties</c>.</summary>
    public const string ResultProperty = "fadenstack.result";

    /// <summary>The agent's slug: its id for MAF.</summary>
    protected override string? IdCore => Slug;

    /// <summary>The agent's name, from its profile once started; its slug before.</summary>
    public override string? Name => _profile?.Name is { Length: > 0 } name ? name : Slug;

    protected override async ValueTask<Microsoft.Agents.AI.AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
        new FadenstackAgentSession(await StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false));

    /// <summary>Only our session's id: the transcript stays encrypted on the device and is never put into the state.</summary>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(Microsoft.Agents.AI.AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
        new(Ours(session).StateBag.Serialize());

    /// <summary>Reopens the session from this device's store by its id; one this device does not have throws.</summary>
    protected override async ValueTask<Microsoft.Agents.AI.AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
    {
        var bag = AgentSessionStateBag.Deserialize(serializedState);
        if (!bag.TryGetValue<string>(SessionIdKey, out var id) || string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("This is not a Fadenstack agent's session: it names no session.", nameof(serializedState));
        }
        return new FadenstackAgentSession(await OpenSessionAsync(id!, cancellationToken).ConfigureAwait(false));
    }

    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, Microsoft.Agents.AI.AgentSession? session,
        AgentRunOptions? options, CancellationToken cancellationToken)
    {
        var chat = await ChatOfAsync(session, cancellationToken).ConfigureAwait(false);
        var (text, attachments) = Input(messages);
        var result = await chat.SendAsync(text, attachments, cancellationToken).ConfigureAwait(false);
        var message = new ChatMessage(ChatRole.Assistant, result.Text) { MessageId = result.TurnId };
        return new AgentResponse(message)
        {
            AgentId = Id,
            ResponseId = result.TurnId,
            Usage = result.Usage,
            FinishReason = Finish(result.Outcome),
            AdditionalProperties = Properties(result),
        };
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        Microsoft.Agents.AI.AgentSession? session, AgentRunOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chat = await ChatOfAsync(session, cancellationToken).ConfigureAwait(false);
        var (text, attachments) = Input(messages);
        await foreach (var e in chat.StreamAsync(text, attachments, cancellationToken).ConfigureAwait(false))
        {
            AgentResponseUpdate? update = e switch
            {
                TextDelta t => new AgentResponseUpdate(ChatRole.Assistant, t.Text),
                ReasoningDelta r => new AgentResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(r.Text)]),
                // The agent runs its tools itself: the calls are for showing, never for the caller to run again.
                ToolCallStarted s => new AgentResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(s.CallId, s.Name, s.Arguments.ToDictionary(p => p.Key, p => p.Value)) { InformationalOnly = true }]),
                ToolCallCompleted c => new AgentResponseUpdate(ChatRole.Tool, [new FunctionResultContent(c.CallId, c.Result)]),
                TurnCompleted done => new AgentResponseUpdate(ChatRole.Assistant,
                    done.Result.Usage is { } usage ? [new UsageContent(usage)] : new List<AIContent>())
                {
                    FinishReason = Finish(done.Result.Outcome),
                    AdditionalProperties = Properties(done.Result),
                },
                _ => null,
            };
            if (update is not null)
            {
                update.AgentId = Id;
                update.ResponseId = e.TurnId;
                update.MessageId = e.TurnId;
                yield return update;
            }
        }
    }

    /// <summary>
    /// The session to run in: the one given, which must be this agent's; or a new one. A run through MAF needs the app's
    /// approval handler: without one every call that needs a person would be declined unseen.
    /// </summary>
    private async Task<ChatSession> ChatOfAsync(Microsoft.Agents.AI.AgentSession? session, CancellationToken cancellationToken)
    {
        if (_settings.Approvals is DenyAllApprovals)
        {
            throw new InvalidOperationException(
                "Give the agent an approval handler (AgentBuilder.UseApprovals) before running it through the Agent Framework: " +
                "without one, every call that needs a person is declined.");
        }
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return session switch
        {
            null => await StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false),
            FadenstackAgentSession { Chat: var chat } when ReferenceEquals(chat.Agent, this) => chat,
            _ => throw new ArgumentException("The session is not one of this agent's: create it with this agent.", nameof(session)),
        };
    }

    private static FadenstackAgentSession Ours(Microsoft.Agents.AI.AgentSession session) =>
        session as FadenstackAgentSession
        ?? throw new ArgumentException("The session is not a Fadenstack agent's.", nameof(session));

    /// <summary>
    /// The person's new message from MAF's messages: the text of the user messages, and their pictures as attachments. The
    /// agent keeps its own history and instructions, so other roles are not taken.
    /// </summary>
    private static (string Text, IReadOnlyList<MessageAttachment> Attachments) Input(IEnumerable<ChatMessage> messages)
    {
        var users = (messages ?? throw new ArgumentNullException(nameof(messages))).Where(m => m.Role == ChatRole.User).ToList();
        var text = string.Join("\n\n", users.Select(m => m.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        var pictures = users.SelectMany(m => m.Contents.OfType<DataContent>())
            .Where(d => MessageAttachment.IsPictureType(d.MediaType))
            .Select((d, i) => new MessageAttachment { Title = $"Picture {i + 1}", Content = d.Uri, MediaType = d.MediaType, Kind = "picture" })
            .ToList();
        if (text.Length == 0 && pictures.Count == 0)
        {
            throw new ArgumentException("A run needs a user message with text or a picture.", nameof(messages));
        }
        return (text, pictures);
    }

    private static ChatFinishReason? Finish(ChatOutcome outcome) => outcome switch
    {
        ChatOutcome.Completed or ChatOutcome.ToolDenied => ChatFinishReason.Stop,
        ChatOutcome.PromptBlocked or ChatOutcome.ResponseBlocked => ChatFinishReason.ContentFilter,
        _ => null,
    };

    private static AdditionalPropertiesDictionary Properties(AgentResult result) => new()
    {
        [OutcomeProperty] = result.Outcome.ToString(),
        [ResultProperty] = result,
    };
}

/// <summary>
/// A <see cref="ChatSession"/> as a Microsoft Agent Framework session: what <see cref="FadenstackAgent"/> hands MAF. Its state
/// bag holds only the session's id; the transcript stays in the device's encrypted store. <c>GetService(typeof(ChatSession))</c>
/// gives the session itself.
/// </summary>
public sealed class FadenstackAgentSession : Microsoft.Agents.AI.AgentSession
{
    internal FadenstackAgentSession(ChatSession chat)
    {
        Chat = chat;
        StateBag.SetValue(FadenstackAgent.SessionIdKey, chat.Id);
    }

    /// <summary>The agent's own session.</summary>
    public ChatSession Chat { get; }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ChatSession) ? Chat : base.GetService(serviceType, serviceKey);
}
