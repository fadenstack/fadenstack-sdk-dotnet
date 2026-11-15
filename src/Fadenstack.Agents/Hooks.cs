using Fadenstack.Tools;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

/// <summary>How a hooked call ended.</summary>
public enum HookOutcome
{
    /// <summary>The call ran and returned.</summary>
    Ok,

    /// <summary>The call failed.</summary>
    Error,

    /// <summary>The call was refused: by the policy, the user, a hook, or (for a model call) a governance block.</summary>
    Denied,

    /// <summary>The turn was stopped while the call ran.</summary>
    Cancelled,
}

/// <summary>What the agent's policy decided about a tool call, before a hook is asked: the hook can only make it stricter.</summary>
public enum ToolPolicyDecision
{
    /// <summary>The policy runs the call without asking the user (a grant or an earlier "allow" may still apply as before).</summary>
    Runs,

    /// <summary>The policy asks the user first (the approval card).</summary>
    Asks,

    /// <summary>The policy refuses the call. A hook is told, and cannot change it.</summary>
    Refused,
}

/// <summary>
/// The hook API of the agent (sdk/51 F07): code of the app that watches the agent's model calls and tool calls, and may make a tool
/// call stricter. Add hooks with <see cref="AgentBuilder.AddHooks"/>. Derive from <see cref="AgentHooks"/> to override only
/// the points you need.
/// <para>
/// <b>The rule that keeps the floor</b> (OWASP ASVS 5.0 V8, authorization enforced in a trusted layer): <see cref="BeforeToolCallAsync"/> runs
/// after the policy has decided. Its answer, <see cref="ToolHookVerdict"/>, can only be to go on as the policy decided, to
/// ask (a call the policy would run unasked then shows the approval card), or to deny (the call is refused, and the model is
/// told). There is no answer that approves a call the policy asks about or refuses, and nothing in what a hook is given can
/// be set: a hook cannot lower or change a tool's class.
/// </para>
/// <para>
/// Order: before-hooks run in ascending <c>order</c> (then in the order they were added), after-hooks in the reverse order.
/// A hook that throws never breaks the run: the SDK logs it (event 2060: the hook's type name and the exception's type, never
/// its message) and goes on as if the hook had said "go on" (V16 logging: names, never content).
/// </para>
/// </summary>
public interface IAgentHooks
{
    /// <summary>Before each model call of a turn.</summary>
    Task BeforeModelCallAsync(ModelCallContext context, CancellationToken cancellationToken);

    /// <summary>After each model call of a turn, however it ended.</summary>
    Task AfterModelCallAsync(ModelCallResult result, CancellationToken cancellationToken);

    /// <summary>Before a tool call runs, after the policy decided (see <see cref="ToolCallContext.Decision"/>).</summary>
    Task<ToolHookVerdict> BeforeToolCallAsync(ToolCallContext context, CancellationToken cancellationToken);

    /// <summary>After a tool call, however it ended (also when the policy or a hook refused it).</summary>
    Task AfterToolCallAsync(ToolCallResult result, CancellationToken cancellationToken);
}

/// <summary>An <see cref="IAgentHooks"/> that does nothing at every point: override what you need.</summary>
public class AgentHooks : IAgentHooks
{
    /// <inheritdoc />
    public virtual Task BeforeModelCallAsync(ModelCallContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task AfterModelCallAsync(ModelCallResult result, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task<ToolHookVerdict> BeforeToolCallAsync(ToolCallContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ToolHookVerdict.Continue);

    /// <inheritdoc />
    public virtual Task AfterToolCallAsync(ToolCallResult result, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>What a hook sees before a model call. No prompt text unless the app asked for it (<see cref="AgentBuilder.UseHookContent"/>).</summary>
public sealed class ModelCallContext
{
    internal ModelCallContext(string agentName, string sessionId, string turnId, int round, string? model, int messageCount, int toolCount,
        IReadOnlyList<ChatMessage>? messages)
    {
        AgentName = agentName;
        SessionId = sessionId;
        TurnId = turnId;
        Round = round;
        Model = model;
        MessageCount = messageCount;
        ToolCount = toolCount;
        Messages = messages;
    }

    /// <summary>The agent's name (its slug).</summary>
    public string AgentName { get; }

    /// <summary>The session (conversation) the call belongs to.</summary>
    public string SessionId { get; }

    /// <summary>The turn the call belongs to.</summary>
    public string TurnId { get; }

    /// <summary>Which model call of the turn this is, from 1.</summary>
    public int Round { get; }

    /// <summary>The model's name as the agent's profile gives it, or null when the profile does not say.</summary>
    public string? Model { get; }

    /// <summary>How many messages go to the model.</summary>
    public int MessageCount { get; }

    /// <summary>How many tools the model is offered.</summary>
    public int ToolCount { get; }

    /// <summary>The messages, only when the app asked for content; otherwise null.</summary>
    public IReadOnlyList<ChatMessage>? Messages { get; }
}

/// <summary>What a hook sees after a model call. No answer text unless the app asked for it (<see cref="AgentBuilder.UseHookContent"/>).</summary>
public sealed class ModelCallResult
{
    internal ModelCallResult(string agentName, string sessionId, string turnId, int round, string? model, HookOutcome outcome, TimeSpan duration,
        long? inputTokens, long? outputTokens, int toolCallCount, string? text)
    {
        AgentName = agentName;
        SessionId = sessionId;
        TurnId = turnId;
        Round = round;
        Model = model;
        Outcome = outcome;
        Duration = duration;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        ToolCallCount = toolCallCount;
        Text = text;
    }

    /// <summary>The agent's name (its slug).</summary>
    public string AgentName { get; }

    /// <summary>The session the call belongs to.</summary>
    public string SessionId { get; }

    /// <summary>The turn the call belongs to.</summary>
    public string TurnId { get; }

    /// <summary>Which model call of the turn this was, from 1.</summary>
    public int Round { get; }

    /// <summary>The model's name as the agent's profile gives it, or null.</summary>
    public string? Model { get; }

    /// <summary>How the call ended.</summary>
    public HookOutcome Outcome { get; }

    /// <summary>How long the call took.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Tokens sent, when the model said; otherwise null.</summary>
    public long? InputTokens { get; }

    /// <summary>Tokens received, when the model said; otherwise null.</summary>
    public long? OutputTokens { get; }

    /// <summary>How many tool calls the model asked for.</summary>
    public int ToolCallCount { get; }

    /// <summary>The answer text, only when the app asked for content; otherwise null.</summary>
    public string? Text { get; }
}

/// <summary>
/// What a hook sees before a tool call, after the policy decided. Everything here is read-only: a hook cannot change the tool,
/// its class or the policy's decision (V8).
/// </summary>
public sealed class ToolCallContext
{
    internal ToolCallContext(string agentName, string sessionId, string turnId, string toolName, string source, string? server,
        ToolClass toolClass, string callId, IReadOnlyDictionary<string, object?> arguments, ToolPolicyDecision decision)
    {
        AgentName = agentName;
        SessionId = sessionId;
        TurnId = turnId;
        ToolName = toolName;
        Source = source;
        Server = server;
        Class = toolClass;
        CallId = callId;
        Arguments = arguments;
        Decision = decision;
    }

    /// <summary>The agent's name (its slug).</summary>
    public string AgentName { get; }

    /// <summary>The session the call belongs to.</summary>
    public string SessionId { get; }

    /// <summary>The turn the call belongs to.</summary>
    public string TurnId { get; }

    /// <summary>The tool's name.</summary>
    public string ToolName { get; }

    /// <summary>Where the tool comes from: <c>host</c> (the app's own), <c>mcp</c>, <c>gateway</c>, <c>skills</c> and so on.</summary>
    public string Source { get; }

    /// <summary>The server the tool belongs to (a local MCP server's name), or null.</summary>
    public string? Server { get; }

    /// <summary>The tool's class, as the policy sees it (the stricter of the tool's own and the gateway's). A copy: it cannot be set.</summary>
    public ToolClass Class { get; }

    /// <summary>The call's id.</summary>
    public string CallId { get; }

    /// <summary>The arguments the model gave (a copy). For a tool of the app's own (<c>host</c>) they are the app's; others' may hold the person's content.</summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; }

    /// <summary>What the policy decided: runs, asks, or refused.</summary>
    public ToolPolicyDecision Decision { get; }
}

/// <summary>What a hook sees after a tool call.</summary>
public sealed class ToolCallResult
{
    internal ToolCallResult(string agentName, string sessionId, string turnId, string toolName, string source, string? server,
        ToolClass toolClass, string callId, ToolPolicyDecision decision, HookOutcome outcome, TimeSpan duration, string? resultText)
    {
        AgentName = agentName;
        SessionId = sessionId;
        TurnId = turnId;
        ToolName = toolName;
        Source = source;
        Server = server;
        Class = toolClass;
        CallId = callId;
        Decision = decision;
        Outcome = outcome;
        Duration = duration;
        ResultText = resultText;
    }

    /// <summary>The agent's name (its slug).</summary>
    public string AgentName { get; }

    /// <summary>The session the call belongs to.</summary>
    public string SessionId { get; }

    /// <summary>The turn the call belongs to.</summary>
    public string TurnId { get; }

    /// <summary>The tool's name.</summary>
    public string ToolName { get; }

    /// <summary>Where the tool comes from (<c>host</c>, <c>mcp</c>, <c>gateway</c>, ...).</summary>
    public string Source { get; }

    /// <summary>The server the tool belongs to, or null.</summary>
    public string? Server { get; }

    /// <summary>The tool's class.</summary>
    public ToolClass Class { get; }

    /// <summary>The call's id.</summary>
    public string CallId { get; }

    /// <summary>What the policy decided before the call.</summary>
    public ToolPolicyDecision Decision { get; }

    /// <summary>How the call ended.</summary>
    public HookOutcome Outcome { get; }

    /// <summary>How long the tool ran, not counting the time the user took to answer an approval card.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The result text, only for the app's own (<c>host</c>) tools; null for every other source.</summary>
    public string? ResultText { get; }
}

/// <summary>
/// A hook's answer before a tool call. It can only go on, ask, or deny; there is no way to approve (V8: a hook may make a call
/// stricter, never looser). Optionally it carries short notes for the transcript.
/// </summary>
public sealed class ToolHookVerdict
{
    /// <summary>The most characters of a deny reason the model is told.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>The most notes a call keeps (all hooks together).</summary>
    public const int MaxNotes = 8;

    /// <summary>The most characters of a note's key.</summary>
    public const int MaxNoteKeyLength = 64;

    /// <summary>The most characters of a note's value.</summary>
    public const int MaxNoteValueLength = 200;

    internal enum Kinds
    {
        Continue,
        Ask,
        Deny,
    }

    private ToolHookVerdict(Kinds kind, string? reason, IReadOnlyList<KeyValuePair<string, string>> notes)
    {
        Kind = kind;
        Reason = reason;
        Notes = notes;
    }

    internal Kinds Kind { get; }

    internal string? Reason { get; }

    internal IReadOnlyList<KeyValuePair<string, string>> Notes { get; }

    /// <summary>Go on as the policy decided.</summary>
    public static ToolHookVerdict Continue { get; } = new(Kinds.Continue, null, Array.Empty<KeyValuePair<string, string>>());

    /// <summary>
    /// Ask the user first. A call the policy would run unasked then shows the approval card (once only: no earlier "allow" and no
    /// grant answers it); a call the policy already asks about is asked as before. <paramref name="reason"/> (cut to 500 characters) is shown on the card.
    /// </summary>
    public static ToolHookVerdict Ask(string? reason = null) => new(Kinds.Ask, Cut(reason, MaxReasonLength), Array.Empty<KeyValuePair<string, string>>());

    /// <summary>Refuse the call. <paramref name="reason"/> (cut to 500 characters) reaches the model as a declined call.</summary>
    public static ToolHookVerdict Deny(string reason) =>
        new(Kinds.Deny, Cut(string.IsNullOrWhiteSpace(reason) ? "declined by the app" : reason, MaxReasonLength), Array.Empty<KeyValuePair<string, string>>());

    /// <summary>
    /// The same answer with a note added: a short key and value kept in the transcript's entry of this call, never sent to the
    /// server. A call keeps at most <see cref="MaxNotes"/> notes; keys and values are cut to <see cref="MaxNoteKeyLength"/> and <see cref="MaxNoteValueLength"/> characters.
    /// </summary>
    public ToolHookVerdict WithNote(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A note needs a key.", nameof(key));
        }
        var notes = new List<KeyValuePair<string, string>>(Notes)
        {
            new(Cut(key.Trim(), MaxNoteKeyLength)!, Cut(value ?? "", MaxNoteValueLength)!),
        };
        return new ToolHookVerdict(Kind, Reason, notes);
    }

    internal static string? Cut(string? text, int max)
    {
        if (text is null || text.Length <= max)
        {
            return text;
        }
        var length = max;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--; // never half a pair
        }
        return text.Substring(0, length);
    }
}
