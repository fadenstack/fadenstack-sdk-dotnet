using Microsoft.Extensions.Logging;

namespace Fadenstack.Agents;

/// <summary>What the before-tool hooks said together: the strictest answer, and the notes.</summary>
internal sealed class ToolHookAnswer
{
    internal static readonly ToolHookAnswer GoOn = new(false, false, null, Array.Empty<KeyValuePair<string, string>>());

    internal ToolHookAnswer(bool ask, bool deny, string? reason, IReadOnlyList<KeyValuePair<string, string>> notes)
    {
        Ask = ask;
        Deny = deny;
        Reason = reason;
        Notes = notes;
    }

    /// <summary>A hook asked: a call the policy would run unasked shows the card.</summary>
    internal bool Ask { get; }

    /// <summary>A hook denied: the call is refused.</summary>
    internal bool Deny { get; }

    /// <summary>The deny reason, or the reason the card shows.</summary>
    internal string? Reason { get; }

    internal IReadOnlyList<KeyValuePair<string, string>> Notes { get; }
}

/// <summary>
/// Runs the agent's hooks (<see cref="IAgentHooks"/>) in order and keeps them from breaking the run. The one place that turns a hook's
/// <see cref="ToolHookVerdict"/> into an answer, and it can only produce "go on", "ask" or "deny" (OWASP ASVS 5.0 V8): the caller never
/// reads a verdict as an approval.
/// </summary>
internal sealed class HookRunner
{
    private readonly IReadOnlyList<IAgentHooks> _hooks;
    private readonly Func<ILogger> _logger;

    private readonly Func<bool> _content;

    // content: asked at each call; content logging is on, so the hooks see message and answer text (8.1).
    internal HookRunner(IReadOnlyList<IAgentHooks> hooks, Func<bool> content, Func<ILogger> logger)
    {
        _hooks = hooks;
        _content = content;
        _logger = logger;
    }

    internal bool Any => _hooks.Count > 0;

    /// <summary>The app offered message and answer text in the model-call hooks and content logging is on now.</summary>
    internal bool Content => _content();

    internal async Task BeforeModelAsync(ModelCallContext context, CancellationToken cancellationToken)
    {
        foreach (var hook in _hooks)
        {
            try
            {
                await hook.BeforeModelCallAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Failed(hook, e);
            }
        }
    }

    internal async Task AfterModelAsync(ModelCallResult result, CancellationToken cancellationToken)
    {
        for (var i = _hooks.Count - 1; i >= 0; i--)
        {
            var hook = _hooks[i];
            try
            {
                await hook.AfterModelCallAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The turn was stopped: nothing to log.
            }
            catch (Exception e)
            {
                Failed(hook, e);
            }
        }
    }

    /// <summary>
    /// Asks each hook in order. A deny stops the line: later hooks are not asked. A throwing hook counts as "go on". When
    /// <paramref name="mayChange"/> is false (the policy already refused the call) the hooks are told and their answers are dropped.
    /// </summary>
    internal async Task<ToolHookAnswer> BeforeToolAsync(ToolCallContext context, bool mayChange, CancellationToken cancellationToken)
    {
        var (ask, askReason) = (false, (string?)null);
        var notes = new List<KeyValuePair<string, string>>();
        foreach (var hook in _hooks)
        {
            ToolHookVerdict? verdict = null;
            try
            {
                verdict = await hook.BeforeToolCallAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Failed(hook, e);
            }
            if (verdict is null)
            {
                continue; // nothing said: go on
            }
            foreach (var note in verdict.Notes)
            {
                if (notes.Count < ToolHookVerdict.MaxNotes)
                {
                    notes.Add(note);
                }
            }
            if (!mayChange)
            {
                continue;
            }
            switch (verdict.Kind)
            {
                case ToolHookVerdict.Kinds.Deny:
                    return new ToolHookAnswer(false, true, verdict.Reason, notes);
                case ToolHookVerdict.Kinds.Ask:
                    if (!ask)
                    {
                        (ask, askReason) = (true, verdict.Reason);
                    }
                    break;
            }
        }
        return new ToolHookAnswer(ask, false, askReason, notes);
    }

    internal async Task AfterToolAsync(ToolCallResult result, CancellationToken cancellationToken)
    {
        for (var i = _hooks.Count - 1; i >= 0; i--)
        {
            var hook = _hooks[i];
            try
            {
                await hook.AfterToolCallAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The turn was stopped: nothing to log.
            }
            catch (Exception e)
            {
                Failed(hook, e);
            }
        }
    }

    /// <summary>The hook's type and the exception's type: never the message, which may hold content (ASVS 5.0 V16).</summary>
    private void Failed(IAgentHooks hook, Exception error) => Log.HookFailed(_logger(), hook.GetType().Name, error.GetType().Name);
}
