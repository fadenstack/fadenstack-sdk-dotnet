using System.Net;
using System.Net.Http;
using System.Text.Json;
using Fadenstack.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fadenstack.Auth;

/// <summary>
/// Signs a person in to one agent on this device by pairing (the OAuth 2.0 Device Authorization Grant, RFC 8628):
/// the app shows a short code, the person approves it in the web console in their browser -- where they sign in however the
/// server lets them, including SSO and MFA -- and the app then receives the same agent tokens a sign-in issues. The app
/// never sees a password.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StartAsync"/> asks for a pairing; show <see cref="PairingStart.UserCode"/> and open
/// <see cref="PairingStart.VerificationUriComplete"/>. <see cref="WaitAsync"/> polls until the person approves or denies
/// it, or it expires, and saves the tokens to the <see cref="ITokenStore"/>. An <see cref="AgentTokenProvider"/> for the same
/// server and agent and store is signed in afterwards.
/// </para>
/// <para>
/// The pairing code (the secret the app polls with) is held in memory, in the <see cref="PairingStart"/>, and is never
/// stored, logged, put in an exception or printed. Tokens are never logged either.
/// </para>
/// </remarks>
public sealed class AgentPairing
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)];

    private static readonly TimeSpan SlowDown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Uri _server;
    private readonly HttpClient _http;
    private readonly ITokenStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    /// <param name="server">The backend (usually the server URL); the same one the <see cref="AgentTokenProvider"/> gets.</param>
    /// <param name="http">
    /// The client to reach the server with. For a server whose CA is not deployed, build it on
    /// <see cref="FadenstackTransport.CreateHandler"/> with the client's options, so the trusted CA applies.
    /// </param>
    /// <param name="store">Where the sign-in is kept: the one the <see cref="AgentTokenProvider"/> uses.</param>
    /// <param name="clock">The time, for tests.</param>
    /// <param name="delay">How to wait between polls, for tests; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> if not given.</param>
    /// <param name="allowInsecureHttp">
    /// Allow <c>http://</c> to another machine, for a development server. Off: https, or plain http only to this machine.
    /// </param>
    /// <param name="logger">Where progress is logged (no code, no token, ever); nowhere if not given.</param>
    public AgentPairing(Uri server, HttpClient http, ITokenStore store, Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, bool allowInsecureHttp = false, ILogger? logger = null)
    {
        if (server.Scheme != Uri.UriSchemeHttps && !(server.Scheme == Uri.UriSchemeHttp && (allowInsecureHttp || server.IsLoopback)))
        {
            throw new ArgumentException(
                "The server URL must be https unless allowInsecureHttp is set or it is this machine (localhost).", nameof(server));
        }
        _server = new Uri(server.AbsoluteUri.TrimEnd('/') + "/");
        _http = http;
        _store = store;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Asks the server for a pairing for <paramref name="agent"/> on this device. Show the returned code and open its link.
    /// </summary>
    /// <param name="agent">The agent's slug or id; use the same for the <see cref="AgentTokenProvider"/>.</param>
    /// <param name="deviceLabel">Names the sign-in in the person's list of sign-ins, e.g. "Desktop agent on SACHI-PC".</param>
    /// <param name="client">What the app says about itself; the approval page shows it as such.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="FadenstackPairingUnsupportedException">The server is older than this feature (404).</exception>
    /// <exception cref="FadenstackRateLimitedException">Too many pairings were started from this address (429).</exception>
    /// <exception cref="FadenstackException">The server could not be reached.</exception>
    public async Task<PairingStart> StartAsync(string agent, string deviceLabel, PairingClient client,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            agent,
            device_label = deviceLabel,
            client = new { name = client.Name, version = client.Version, os = client.Os },
        };
        var (status, text, response) = await SendAsync("api/agent-tokens/pairings", body, cancellationToken).ConfigureAwait(false);
        try
        {
            if (status == HttpStatusCode.NotFound)
            {
                Log.PairingEnded(_log, agent, "unsupported");
                throw new FadenstackPairingUnsupportedException(
                    "This server does not support signing in from the desktop app yet. Ask your IT to update Fadenstack.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw ErrorReader.ToException(response, text);
            }
        }
        finally
        {
            response.Dispose();
        }
        Started started;
        try
        {
            started = JsonSerializer.Deserialize<Started>(text, FadenstackJson.Options)!;
            if (string.IsNullOrEmpty(started.PairingCode) || string.IsNullOrEmpty(started.UserCode))
            {
                throw new JsonException("incomplete");
            }
        }
        catch (JsonException)
        {
            throw new FadenstackException("The server's answer to the pairing request was not understood.");
        }
        var uri = Web(started.VerificationUri);
        var complete = started.VerificationUriComplete is { Length: > 0 } c
            ? Web(c)
            : new Uri(uri.AbsoluteUri + (uri.Query.Length > 0 ? "&" : "?") + "code=" + Uri.EscapeDataString(started.UserCode));
        var expiresIn = started.ExpiresIn > 0 ? started.ExpiresIn : 600;
        var start = new PairingStart(agent, started.PairingCode, started.UserCode, uri, complete,
            _now().AddSeconds(expiresIn), TimeSpan.FromSeconds(Math.Max(1, started.Interval)));
        Log.PairingStarted(_log, agent, expiresIn);
        return start;
    }

    /// <summary>
    /// Polls until the person approves or denies the pairing, or it expires, and saves the tokens and who signed in to the store.
    /// It waits the server's interval between polls, five seconds longer each time the server says <c>slow_down</c>, and when the
    /// server cannot be reached it tries again after 5, 10, 20 and then 30 seconds for as long as the pairing is valid.
    /// </summary>
    /// <param name="start">What <see cref="StartAsync"/> returned.</param>
    /// <param name="progress">Told the state and the time left after every poll; may be null.</param>
    /// <param name="cancellationToken">Stops the wait; the pairing stays valid at the server until it expires.</param>
    /// <exception cref="FadenstackPairingDeniedException">The person denied the pairing in the console.</exception>
    /// <exception cref="FadenstackPairingExpiredException">The code expired, or the server no longer knows it.</exception>
    /// <exception cref="FadenstackPairingUnsupportedException">The server does not have the pairing routes.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task<PairingResult> WaitAsync(PairingStart start, IProgress<PairingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var interval = start.Interval;
        var failures = 0;
        var wait = interval;
        var state = PairingState.Waiting;
        Report(progress, start, state, interval);
        while (true)
        {
            await _delay(wait, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_now() >= start.ExpiresAt)
            {
                throw Expired(start, "The code expired.");
            }
            HttpStatusCode status;
            string text;
            HttpResponseMessage response;
            try
            {
                (status, text, response) = await SendAsync("api/agent-tokens/pairings/token",
                    new { pairing_code = start.PairingCode }, cancellationToken).ConfigureAwait(false);
            }
            catch (FadenstackException e)
            {
                wait = Backoff[Math.Min(failures++, Backoff.Length - 1)];
                Log.PairingRetrying(_log, start.Agent, e.InnerException?.GetType().Name ?? "network", (int)wait.TotalSeconds);
                Report(progress, start, PairingState.Retrying, interval);
                continue;
            }
            using (response)
            {
                var code = (int)status >= 400 ? ErrorCode(text) : null;
                if (response.IsSuccessStatusCode)
                {
                    return await FinishAsync(start, text, cancellationToken).ConfigureAwait(false);
                }
                if ((int)status >= 500)
                {
                    wait = Backoff[Math.Min(failures++, Backoff.Length - 1)];
                    Log.PairingRetrying(_log, start.Agent, $"status {(int)status}", (int)wait.TotalSeconds);
                    Report(progress, start, PairingState.Retrying, interval);
                    continue;
                }
                failures = 0;
                switch ((int)status == 429 ? "slow_down" : code)
                {
                    case "authorization_pending":
                        state = PairingState.Waiting;
                        break;
                    case "slow_down":
                        interval += SlowDown;
                        if (ErrorReader.RetryAfter(response) is { } retryAfter && retryAfter > interval)
                        {
                            interval = retryAfter;
                        }
                        state = PairingState.SlowedDown;
                        Log.PairingSlowedDown(_log, start.Agent, (int)interval.TotalSeconds);
                        break;
                    case "access_denied":
                        Log.PairingEnded(_log, start.Agent, "denied");
                        throw new FadenstackPairingDeniedException("The sign-in was denied in the browser.");
                    case "expired_token":
                        throw Expired(start, "The code expired.");
                    case "invalid_grant":
                        throw Expired(start, "The server no longer knows this code. Get a new one.");
                    default:
                        if (status == HttpStatusCode.NotFound)
                        {
                            throw new FadenstackPairingUnsupportedException(
                                "This server does not support signing in from the desktop app yet. Ask your IT to update Fadenstack.");
                        }
                        throw ErrorReader.ToException(response, text);
                }
            }
            wait = interval;
            Report(progress, start, state, interval);
        }
    }

    private async Task<PairingResult> FinishAsync(PairingStart start, string text, CancellationToken cancellationToken)
    {
        TokenSet tokens;
        try
        {
            tokens = AgentTokenProvider.FromWire(text, _now());
        }
        catch (JsonException)
        {
            throw new FadenstackException("The server's sign-in answer was not understood.");
        }
        await _store.SaveAsync(AgentTokenProvider.KeyOf(_server, start.Agent), tokens, cancellationToken).ConfigureAwait(false);
        Log.PairingEnded(_log, start.Agent, "signed in");
        return new PairingResult(string.IsNullOrEmpty(tokens.Agent) ? start.Agent : tokens.Agent,
            Guid.TryParse(tokens.DeviceId, out var device) ? device : Guid.Empty, tokens.User);
    }

    private FadenstackPairingExpiredException Expired(PairingStart start, string message)
    {
        Log.PairingEnded(_log, start.Agent, "expired");
        return new FadenstackPairingExpiredException(message);
    }

    private void Report(IProgress<PairingProgress>? progress, PairingStart start, PairingState state, TimeSpan interval)
    {
        var left = start.ExpiresAt - _now();
        progress?.Report(new PairingProgress(state, left > TimeSpan.Zero ? left : TimeSpan.Zero, interval));
    }

    /// <summary>One POST; a request that gets no answer is a <see cref="FadenstackException"/> (whose inner exception names why).</summary>
    private async Task<(HttpStatusCode Status, string Text, HttpResponseMessage Response)> SendAsync(string path, object body,
        CancellationToken cancellationToken)
    {
        using var request = FadenstackTransport.JsonRequest(HttpMethod.Post, new Uri(_server, path), body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or System.IO.IOException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The message of a network error can carry the address; the caller sees it, the log does not.
            throw new FadenstackException($"Fadenstack could not be reached: {e.Message}", e);
        }
        try
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (response.StatusCode, text, response);
        }
        catch (Exception e) when (e is HttpRequestException or System.IO.IOException)
        {
            response.Dispose();
            throw new FadenstackException($"Fadenstack could not be reached: {e.Message}", e);
        }
    }

    /// <summary>The RFC 8628 error code of an error body: <c>{"code": ...}</c>, or <c>error</c> / <c>detail.code</c> / <c>error.code</c>.</summary>
    private static string? ErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            foreach (var name in new[] { "code", "error", "detail" })
            {
                if (!root.TryGetProperty(name, out var found))
                {
                    continue;
                }
                if (found.ValueKind == JsonValueKind.String)
                {
                    return found.GetString();
                }
                if (found.ValueKind == JsonValueKind.Object && found.TryGetProperty("code", out var inner)
                    && inner.ValueKind == JsonValueKind.String)
                {
                    return inner.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // not JSON: the status says it
        }
        return null;
    }

    private Uri Web(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            uri = new Uri(_server, address);
        }
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new FadenstackException("The server's sign-in address is not a web address.");
        }
        return uri;
    }

    private sealed record Started(string PairingCode, string UserCode, string VerificationUri, string? VerificationUriComplete,
        int ExpiresIn = 600, int Interval = 5);
}

/// <summary>What the app says about itself when it asks for a pairing. The approval page shows it as the app's own word.</summary>
/// <param name="Name">The app's name, e.g. "Desktop assistant".</param>
/// <param name="Version">The app's version.</param>
/// <param name="Os">The operating system, e.g. "Windows 11".</param>
public sealed record PairingClient(string Name, string Version, string Os)
{
    /// <summary>The app's name and version, with the operating system this process runs on.</summary>
    public static PairingClient ForThisDevice(string name, string version) =>
        new(name, version, System.Runtime.InteropServices.RuntimeInformation.OSDescription);
}

/// <summary>
/// A pairing the server started: what to show the person. The pairing code the app polls with stays inside; it is not a
/// property and not in <see cref="ToString"/>.
/// </summary>
public sealed class PairingStart
{
    internal PairingStart(string agent, string pairingCode, string userCode, Uri verificationUri, Uri verificationUriComplete,
        DateTimeOffset expiresAt, TimeSpan interval)
    {
        Agent = agent;
        PairingCode = pairingCode;
        UserCode = userCode;
        VerificationUri = verificationUri;
        VerificationUriComplete = verificationUriComplete;
        ExpiresAt = expiresAt;
        Interval = interval;
    }

    internal string Agent { get; }

    internal string PairingCode { get; }

    /// <summary>The short code to show, e.g. <c>BDWP-HTQX</c>; the person compares it in the console. Show it, do not log it.</summary>
    public string UserCode { get; }

    /// <summary>The console page where the person enters the code.</summary>
    public Uri VerificationUri { get; }

    /// <summary>The same page with the code filled in: open this in the browser. Opening it approves nothing.</summary>
    public Uri VerificationUriComplete { get; }

    /// <summary>When the code expires (the server's 10 minutes, counted on this device's clock).</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>How often the server wants to be asked.</summary>
    public TimeSpan Interval { get; }

    /// <summary>No code: only that this is a pairing and when it ends.</summary>
    public override string ToString() => $"PairingStart(expires {ExpiresAt:u})";
}

/// <summary>A finished pairing: the sign-in is saved in the store.</summary>
/// <param name="AgentSlug">The agent the person signed in to.</param>
/// <param name="DeviceId">This device's sign-in; the server lists and revokes sign-ins by it.</param>
/// <param name="SignedInUser">Who approved it; null if the server did not say.</param>
public sealed record PairingResult(string AgentSlug, Guid DeviceId, SignedInUser? SignedInUser);

/// <summary>Where a wait for approval stands.</summary>
public enum PairingState
{
    /// <summary>The person has not decided yet.</summary>
    Waiting,

    /// <summary>The server asked for fewer polls; the interval grew.</summary>
    SlowedDown,

    /// <summary>The server could not be reached; the next try comes after a pause.</summary>
    Retrying,
}

/// <summary>Reported while waiting, after every poll.</summary>
/// <param name="State">What the last poll found.</param>
/// <param name="TimeLeft">How long the code is still valid (never negative).</param>
/// <param name="Interval">The wait between polls now.</param>
public sealed record PairingProgress(PairingState State, TimeSpan TimeLeft, TimeSpan Interval);

/// <summary>The person denied the pairing in the console.</summary>
public sealed class FadenstackPairingDeniedException : FadenstackException
{
    /// <summary>Creates the exception.</summary>
    public FadenstackPairingDeniedException(string message) : base(message)
    {
    }
}

/// <summary>The code expired before it was approved, or the server no longer knows it. Start a new pairing.</summary>
public sealed class FadenstackPairingExpiredException : FadenstackException
{
    /// <summary>Creates the exception.</summary>
    public FadenstackPairingExpiredException(string message) : base(message)
    {
    }
}

/// <summary>The server has no pairing routes: it is older than this feature.</summary>
public sealed class FadenstackPairingUnsupportedException : FadenstackException
{
    /// <summary>Creates the exception.</summary>
    public FadenstackPairingUnsupportedException(string message) : base(message)
    {
    }
}
