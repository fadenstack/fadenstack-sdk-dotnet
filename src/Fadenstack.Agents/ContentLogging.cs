using Microsoft.Extensions.Logging;

namespace Fadenstack.Agents;

/// <summary>Why an app could not switch content logging on (<see cref="ContentLoggingException.Reason"/>).</summary>
public enum ContentLoggingRefusal
{
    /// <summary>The app did not offer content logging (<see cref="AgentBuilder.UseHookContent"/> or <see cref="AgentOpenTelemetryOptions.EnableSensitiveData"/> was not set).</summary>
    NotOffered,

    /// <summary>The machine policy on this computer forbids it (<c>contentLogging: "forbidden"</c>).</summary>
    ForbiddenByPolicy,
}

/// <summary>
/// The agent would not switch content logging on: the app did not offer it, or the machine policy forbids it. Nothing was switched on.
/// TS: <c>ContentLoggingError</c>.
/// </summary>
public sealed class ContentLoggingException : FadenstackException
{
    public ContentLoggingException(ContentLoggingRefusal reason)
        : base(reason == ContentLoggingRefusal.ForbiddenByPolicy
            ? "The policy of this computer does not allow content logging."
            : "This app does not offer content logging.")
    {
        Reason = reason;
    }

    /// <summary>Why it was refused.</summary>
    public ContentLoggingRefusal Reason { get; }
}

/// <summary>
/// Whether the agent writes content (prompts, answers, tool arguments and results) into its logs and spans right now
/// (<see cref="FadenstackAgent.ContentLogging"/>). Content is personal data, so it is OFF by default and only the person switches it on, for a
/// set time (<see cref="MinDuration"/> to <see cref="MaxDuration"/>). It ends by itself at <see cref="Until"/>, it is not kept across restarts, and
/// a band in the chat says while it is on (OWASP ASVS 5.0 V16, security logging: no sensitive data in logs unless it is a visible, time-limited choice).
/// What the app sets at build time (<see cref="AgentBuilder.UseHookContent"/>, <see cref="AgentOpenTelemetryOptions.EnableSensitiveData"/>) only
/// means "this app offers content logging" (<see cref="Offered"/>). A development setup calls <see cref="FadenstackAgent.StartContentLogging"/>
/// itself. TS: <c>ContentLogging</c> (<c>agent.contentLogging</c>).
/// </summary>
public sealed class ContentLoggingState : IDisposable
{
    /// <summary>The shortest time content logging can be switched on for: 15 minutes.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(15);

    /// <summary>The longest time content logging can be switched on for: 24 hours.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    /// <summary>What the UI offers first: one hour.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Func<ILogger> _logger;
    private readonly string _agent;
    private DateTimeOffset? _until;
    private IDisposable? _timer;
    private bool _disposed;

    internal ContentLoggingState(string agent, bool offered, bool allowedByPolicy, Func<ILogger> logger,
        Func<DateTimeOffset>? clock = null, Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _agent = agent;
        Offered = offered;
        AllowedByPolicy = allowedByPolicy;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _schedule = schedule ?? DefaultSchedule;
    }

    /// <summary>Raised when content logging goes on or off (also when it ends by itself). It can come from a timer thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The app offers content logging (it asked for hook content or sensitive telemetry at build time).</summary>
    public bool Offered { get; }

    /// <summary>The machine policy on this computer does not forbid it (<c>contentLogging</c>).</summary>
    public bool AllowedByPolicy { get; }

    /// <summary>The person can switch it on: <see cref="Offered"/> and <see cref="AllowedByPolicy"/>. A control shows only then.</summary>
    public bool CanStart => Offered && AllowedByPolicy;

    /// <summary>Content flows into logs and spans now. Every call compares the clock with <see cref="Until"/>, so a machine that slept cannot overrun it.</summary>
    public bool IsOn
    {
        get
        {
            var ended = false;
            bool on;
            DateTimeOffset? was;
            lock (_gate)
            {
                was = _until;
                ended = ExpireIfDue();
                on = _until is not null;
            }
            if (ended)
            {
                Ended(was!.Value, "expired");
            }
            return on;
        }
    }

    /// <summary>When it ends (UTC); null while off.</summary>
    public DateTimeOffset? Until
    {
        get
        {
            _ = IsOn; // ends it first when the time is up
            lock (_gate)
            {
                return _until;
            }
        }
    }

    internal void Start(TimeSpan duration)
    {
        if (!Offered)
        {
            throw new ContentLoggingException(ContentLoggingRefusal.NotOffered);
        }
        if (!AllowedByPolicy)
        {
            throw new ContentLoggingException(ContentLoggingRefusal.ForbiddenByPolicy);
        }
        if (duration < MinDuration || duration > MaxDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Content logging can be switched on for 15 minutes to 24 hours.");
        }
        DateTimeOffset until;
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ContentLoggingState));
            }
            until = _clock() + duration;
            _until = until;
            Arm(duration);
        }
        Log.ContentLoggingStarted(_logger(), _agent, (int)Math.Round(duration.TotalMinutes), until);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Stop()
    {
        DateTimeOffset? was;
        lock (_gate)
        {
            was = _until;
            _until = null;
            _timer?.Dispose();
            _timer = null;
        }
        if (was is not null)
        {
            Ended(was.Value, "stopped");
        }
    }

    /// <summary>Stops the timer; content logging is off from here on.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _until = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Under the lock: switches off when the time is up. True when it did.</summary>
    private bool ExpireIfDue()
    {
        if (_until is { } until && _clock() >= until)
        {
            _until = null;
            _timer?.Dispose();
            _timer = null;
            return true;
        }
        return false;
    }

    /// <summary>Under the lock: the timer ends it at <see cref="_until"/>.</summary>
    private void Arm(TimeSpan after)
    {
        _timer?.Dispose();
        _timer = _schedule(after, Tick);
    }

    private void Tick()
    {
        DateTimeOffset? was;
        bool ended;
        lock (_gate)
        {
            was = _until;
            ended = ExpireIfDue();
            if (!ended && _until is { } until && !_disposed)
            {
                var left = until - _clock();
                Arm(left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1)); // fired early: once more
            }
        }
        if (ended)
        {
            Ended(was!.Value, "expired");
        }
    }

    private void Ended(DateTimeOffset until, string how)
    {
        Log.ContentLoggingStopped(_logger(), _agent, how, until);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IDisposable DefaultSchedule(TimeSpan after, Action action) =>
        new System.Threading.Timer(_ => action(), null, after, Timeout.InfiniteTimeSpan);
}
