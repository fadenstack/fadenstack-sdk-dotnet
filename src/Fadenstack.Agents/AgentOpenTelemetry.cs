using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

/// <summary>
/// What <see cref="AgentBuilder.UseOpenTelemetry"/> switches on. The GenAI semantic conventions are in "Development" status:
/// attribute and span names may change between versions of the conventions, and so between versions of this SDK.
/// </summary>
public sealed class AgentOpenTelemetryOptions
{
    /// <summary>The <see cref="System.Diagnostics.ActivitySource"/> name of the model-call spans (Microsoft.Extensions.AI's OpenTelemetry chat client).</summary>
    public const string DefaultModelSourceName = "Fadenstack.Agents.Models";

    /// <summary>
    /// Off by default: spans carry names, ids, counts and outcomes, never content. On: the app OFFERS content logging, it does not switch it on.
    /// Only while content logging is on (<see cref="FadenstackAgent.StartContentLogging"/>, a time-limited choice of the person, 8.1)
    /// do the model-call spans carry the messages and the answer, and the tool-call spans the arguments and, for the app's own (<c>host</c>)
    /// tools, the result. The flag is read at each call, so a span made while it is off has no content. Prompts and
    /// answers are the person's data: a development setup calls <see cref="FadenstackAgent.StartContentLogging"/> itself.
    /// </summary>
    public bool EnableSensitiveData { get; set; }

    /// <summary>The source name of the model-call spans; subscribe to it (and to <see cref="FadenstackTelemetry.AgentsSource"/>) in the app's exporter setup.</summary>
    public string ModelSourceName { get; set; } = DefaultModelSourceName;
}

/// <summary>
/// Sets the OpenTelemetry chat client's <c>EnableSensitiveData</c> to the agent's content logging before each call and whenever it
/// changes, so a span made while content logging is off has no content and one made while it is on has it, with no rebuild (8.1).
/// </summary>
internal sealed class ContentFollowingChatClient : DelegatingChatClient
{
    private readonly OpenTelemetryChatClient _telemetry;
    private readonly ContentLoggingState _state;

    internal ContentFollowingChatClient(OpenTelemetryChatClient telemetry, ContentLoggingState state) : base(telemetry)
    {
        _telemetry = telemetry;
        _state = state;
        state.Changed += (_, _) => Follow();
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Follow();
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Follow();
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    private void Follow() => _telemetry.EnableSensitiveData = _state.IsOn;
}

/// <summary>
/// The OpenTelemetry part that runs on the hook points: it adds GenAI attributes to the SDK's <c>execute_tool</c> span (the policy's
/// decision, the tool's type) and, only while content logging is on, the arguments and the app's own tools' results.
/// It never changes a tool call: it always answers "go on".
/// </summary>
internal sealed class OpenTelemetryHooks : AgentHooks
{
    private const int MaxContentChars = 4000;
    private readonly Func<bool> _sensitive;

    /// <param name="sensitive">Asked at each call: content logging is on now (<see cref="ContentLoggingState.IsOn"/>).</param>
    internal OpenTelemetryHooks(Func<bool> sensitive) => _sensitive = sensitive;

    public override Task<ToolHookVerdict> BeforeToolCallAsync(ToolCallContext context, CancellationToken cancellationToken)
    {
        if (ToolSpan() is { } span)
        {
            span.SetTag("gen_ai.tool.type", context.Source == "host" ? "function" : "extension");
            span.SetTag("fadenstack.tool.policy_decision", context.Decision.ToString().ToLowerInvariant());
            if (_sensitive())
            {
                span.SetTag("gen_ai.tool.call.arguments", Cut(JsonSerializer.Serialize(context.Arguments)));
            }
        }
        return Task.FromResult(ToolHookVerdict.Continue);
    }

    public override Task AfterToolCallAsync(ToolCallResult result, CancellationToken cancellationToken)
    {
        if (_sensitive() && result.ResultText is { } text && ToolSpan() is { } span)
        {
            span.SetTag("gen_ai.tool.call.result", Cut(text));
        }
        return Task.CompletedTask;
    }

    /// <summary>The tool call's own span; null when nobody listens (then <see cref="Activity.Current"/> is the turn's span, which must not get a tool's tags).</summary>
    private static Activity? ToolSpan() =>
        Activity.Current is { } current && current.OperationName.StartsWith("execute_tool", StringComparison.Ordinal) ? current : null;

    private static string Cut(string text) => text.Length <= MaxContentChars ? text : text.Substring(0, MaxContentChars);
}
