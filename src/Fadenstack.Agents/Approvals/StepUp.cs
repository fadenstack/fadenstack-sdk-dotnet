using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using Fadenstack.Models;

namespace Fadenstack.Agents;

/// <summary>
/// The second sign-in before Bypass is switched on (sdk/41; OWASP ASVS 5.0 7.5.3): the person signs in again in the
/// browser -- the account's password, or the organisation's sign-in forced fresh -- and approves there, as when the app was
/// paired. The app never sees a password. Only the Bypass card's Allow (or an app set to <see cref="PermissionMode.Bypass"/>)
/// starts one; never the model.
/// </summary>
public sealed record StepUpRequest
{
    /// <summary>The conversation whose Bypass it is for; null for one started before its conversation exists.</summary>
    public string? ConversationId { get; init; }

    /// <summary>The page to open, with its code in the fragment (no server sees it).</summary>
    public Uri Link { get; init; } = new("about:blank");

    /// <summary>The code the page shows (<c>XXXX-XXXX</c>), for the person to compare.</summary>
    public string UserCode { get; init; } = "";

    /// <summary>When the step-up stops working, by this device's clock.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>How a second sign-in stands.</summary>
public enum StepUpStage
{
    /// <summary>The page is open in the browser; the app waits for the person to sign in again and approve.</summary>
    Browser,

    /// <summary>Approved: the app has the proof and asks the server for the Bypass grant.</summary>
    Approved,

    /// <summary>The person denied it, signed in as someone else too often, or the server refused it.</summary>
    Denied,

    /// <summary>Nobody approved it in time (10 minutes).</summary>
    TimedOut,

    /// <summary>The browser could not be opened, or the server could not be reached.</summary>
    Failed,

    /// <summary>The app stopped waiting (the card withdrawn, the chat left).</summary>
    Cancelled,
}

/// <summary>
/// The app's side of the second sign-in: it opens the link in the system browser (<see cref="SystemBrowser"/>) and shows
/// that it waits. A UI's approval handler that also implements this is used for it (the chat's <c>ApprovalBroker</c>
/// does); an app without one gets no Bypass.
/// </summary>
public interface IStepUpHandler
{
    /// <summary>Opens the request's link in the person's browser; false when it could not (then no Bypass).</summary>
    Task<bool> OpenAsync(StepUpRequest request, CancellationToken cancellationToken);

    /// <summary>How the second sign-in stands now, for the app to show.</summary>
    void Report(StepUpRequest request, StepUpStage stage);
}

/// <summary>A Bypass that was not switched on because the second sign-in did not happen (<see cref="Stage"/> says why).</summary>
public sealed class BypassSignInException : Exception
{
    public BypassSignInException(StepUpStage stage, string message) : base(message) => Stage = stage;

    public StepUpStage Stage { get; }
}

/// <summary>Opens a page in the person's default browser, on Windows, macOS and Linux.</summary>
public static class SystemBrowser
{
    /// <summary>Opens <paramref name="link"/> (http or https only); false when the system could not.</summary>
    public static bool Open(Uri link)
    {
        if (link.Scheme != Uri.UriSchemeHttps && link.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }
        try
        {
            ProcessStartInfo start;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                start = new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true };
            }
            else
            {
                // One argument, quoted: an absolute URI escapes quotes and spaces, so nothing in it splits or ends it
                // (netstandard2.0 has no ArgumentList).
                start = new ProcessStartInfo(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open",
                    "\"" + link.AbsoluteUri + "\"")
                {
                    UseShellExecute = false,
                };
            }
            using var process = Process.Start(start);
            return process is not null || RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Trace.TraceWarning($"The browser could not be opened: {e.Message}");
            return false;
        }
    }
}

/// <summary>
/// The device's proof of a fresh sign-in for Bypass (sdk/41), in memory only: never logged, never written to disk. One
/// proof covers every Bypass the person switches on with this agent on this device while it is fresh (5 minutes by the
/// server's clock): parallel chats need one sign-in.
/// </summary>
internal sealed class StepUpProofs
{
    /// <summary>A proof this close to its end is not used: the request could arrive after it.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(15);

    private readonly FadenstackAgent _agent;
    private readonly SemaphoreSlim _one = new(1, 1);
    private string? _proof;
    private DateTimeOffset _until;

    internal StepUpProofs(FadenstackAgent agent) => _agent = agent;

    /// <summary>Waits between polls; a test makes it instant.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>The proof while it is fresh; null otherwise.</summary>
    internal string? Fresh => _proof is not null && _agent.Clock() < _until ? _proof : null;

    /// <summary>Forgets the proof (the server refused it).</summary>
    internal void Forget() => (_proof, _until) = (null, DateTimeOffset.MinValue);

    /// <summary>
    /// A fresh proof: the one held while it lasts, else a new second sign-in through <paramref name="handler"/>. Parallel
    /// chats asking at once share one sign-in. Throws <see cref="BypassSignInException"/> when none came of it.
    /// </summary>
    internal async Task<string> ObtainAsync(IStepUpHandler handler, string? conversationId, int? longestMinutes,
        CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh is { } held)
            {
                return held;
            }
            return await SignInAgainAsync(handler, conversationId, longestMinutes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<string> SignInAgainAsync(IStepUpHandler handler, string? conversationId, int? longestMinutes,
        CancellationToken cancellationToken)
    {
        var agent = _agent.Client.Agent!;
        StepUpStarted started;
        try
        {
            started = await agent.StartStepUpAsync(longestMinutes, AgentGrants.ClientVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FadenstackException or HttpRequestException)
        {
            throw new BypassSignInException(StepUpStage.Failed, $"The second sign-in could not be started: {e.Message}");
        }
        var request = new StepUpRequest
        {
            ConversationId = conversationId,
            Link = new Uri(started.VerificationUriComplete),
            UserCode = started.UserCode,
            ExpiresAt = _agent.Clock() + TimeSpan.FromSeconds(started.ExpiresIn),
        };
        if (!await handler.OpenAsync(request, cancellationToken).ConfigureAwait(false))
        {
            handler.Report(request, StepUpStage.Failed);
            throw new BypassSignInException(StepUpStage.Failed, "The browser could not be opened for the second sign-in.");
        }
        handler.Report(request, StepUpStage.Browser);
        var interval = TimeSpan.FromSeconds(Math.Max(1, started.Interval));
        try
        {
            while (_agent.Clock() < request.ExpiresAt)
            {
                await Delay(interval, cancellationToken).ConfigureAwait(false);
                var (proof, error, slower) = await agent.PollStepUpAsync(started.StepUpCode, cancellationToken).ConfigureAwait(false);
                if (proof is not null && proof.Proof.Length > 0)
                {
                    (_proof, _until) = (proof.Proof, _agent.Clock() + TimeSpan.FromSeconds(proof.ExpiresIn) - Margin);
                    handler.Report(request, StepUpStage.Approved);
                    return proof.Proof;
                }
                switch (error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval = TimeSpan.FromSeconds(Math.Max(interval.TotalSeconds + 5, slower ?? 0));
                        continue;
                    case "expired_token":
                        handler.Report(request, StepUpStage.TimedOut);
                        throw new BypassSignInException(StepUpStage.TimedOut, "The second sign-in took too long.");
                    default:
                        handler.Report(request, StepUpStage.Denied);
                        throw new BypassSignInException(StepUpStage.Denied, "The second sign-in was denied.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            handler.Report(request, StepUpStage.Cancelled);
            throw;
        }
        catch (Exception e) when (e is FadenstackException or HttpRequestException)
        {
            handler.Report(request, StepUpStage.Failed);
            throw new BypassSignInException(StepUpStage.Failed, $"The second sign-in could not be checked: {e.Message}");
        }
        handler.Report(request, StepUpStage.TimedOut);
        throw new BypassSignInException(StepUpStage.TimedOut, "The second sign-in took too long.");
    }
}
