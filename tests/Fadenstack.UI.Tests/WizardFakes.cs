using System.Globalization;
using System.Runtime.ExceptionServices;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Endpoints;
using Fadenstack.UI.Services;
using Fadenstack.UI.Wizard;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>A call the test answers later (cancel and stale answers): complete it, fail it, or let the wizard cancel it.</summary>
public sealed class PendingAnswer<T>
{
    public TaskCompletionSource<T> Source { get; } = new();

    /// <summary>The token the call was given (null until it is called).</summary>
    public CancellationToken? Token { get; internal set; }

    public bool IsCompleted => Source.Task.IsCompleted;

    public void Complete(T value) => WithoutContext(() => Source.TrySetResult(value));

    public void Fail(Exception error) => WithoutContext(() => Source.TrySetException(error));

    internal void Cancel(CancellationToken token) => WithoutContext(() => Source.TrySetCanceled(token));

    /// <summary>
    /// The wizard goes on right where the answer is given, so a test reads the state at once: a thread with a synchronization
    /// context (the test runner's) would hand the continuation to the thread pool instead.
    /// </summary>
    private static void WithoutContext(Action action)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }
}

/// <summary>
/// The answers of one fake method: results, exceptions or a call the test completes later, in the order they were
/// scripted; then the fallback, or an error when there is none.
/// </summary>
public sealed class Scripted<T>
{
    private readonly Queue<Func<CancellationToken, Task<T>>> _answers = new();
    private readonly Func<CancellationToken, Task<T>>? _fallback;

    public Scripted(Func<CancellationToken, Task<T>>? fallback = null)
    {
        _fallback = fallback;
    }

    /// <summary>How often the method was called.</summary>
    public int Calls { get; private set; }

    public bool HasScript => _answers.Count > 0;

    public Scripted<T> Returns(T value)
    {
        _answers.Enqueue(_ => Task.FromResult(value));
        return this;
    }

    public Scripted<T> Throws(Exception error)
    {
        _answers.Enqueue(_ => Task.FromException<T>(error));
        return this;
    }

    /// <summary>A call that stays open until the test completes it; with <paramref name="honourCancel"/> it ends when its token is cancelled.</summary>
    public PendingAnswer<T> Pending(bool honourCancel = true)
    {
        var pending = new PendingAnswer<T>();
        _answers.Enqueue(token =>
        {
            pending.Token = token;
            if (honourCancel)
            {
                token.Register(() => pending.Cancel(token));
            }
            return pending.Source.Task;
        });
        return pending;
    }

    public Task<T> Next(CancellationToken cancellationToken)
    {
        Calls++;
        if (_answers.Count > 0)
        {
            return _answers.Dequeue()(cancellationToken);
        }
        return _fallback is null
            ? Task.FromException<T>(new InvalidOperationException("No answer was scripted for this call."))
            : _fallback(cancellationToken);
    }

    /// <summary>A call that never ends by itself (a wait for an approval), but ends when it is cancelled.</summary>
    public static Task<T> Forever(CancellationToken cancellationToken)
    {
        var pending = new PendingAnswer<T>();
        cancellationToken.Register(() => pending.Cancel(cancellationToken));
        return pending.Source.Task;
    }
}

public static class Tickets
{
    public static PairingTicket Make(DateTimeOffset expiresAt, string code = "BDWP-HTQX", params string[] agents) => new(code,
        new Uri("https://ai.example.test/pair"), new Uri("https://ai.example.test/pair?code=" + code), expiresAt,
        agents.Length == 0 ? new[] { "main-agent" } : agents);
}

public static class Reports
{
    public static EndpointReport Usable(Uri baseUrl, string model = "m1", params string[] models) => new()
    {
        BaseUrl = baseUrl, Model = model, Reachable = true, KeyAccepted = true, Chat = true, Usage = true, ToolCalls = true,
        Models = models.Length > 0 ? models : new[] { model },
    };

    /// <summary>A report with these problems that is not usable.</summary>
    public static EndpointReport Failing(Uri baseUrl, params EndpointProblem[] problems) => new()
    {
        BaseUrl = baseUrl, Reachable = !problems.Contains(EndpointProblem.Unreachable),
        KeyAccepted = problems.Contains(EndpointProblem.KeyRejected) ? false : null, Chat = null, Problems = problems,
    };
}

public sealed record WaitCall(PairingTicket Ticket, IProgress<bool> Progress, CancellationToken Token);

public sealed record PasswordCall(Uri Server, string Email, string Password);

/// <summary>The server side of path A, scripted. Without a script: the health check passes, a ticket is given, the wait never ends.</summary>
public sealed class FakeServerSignIn : IServerSignIn
{
    public FakeServerSignIn(Func<DateTimeOffset> now)
    {
        Start = new Scripted<PairingTicket>(_ => Task.FromResult(Tickets.Make(now().AddMinutes(10))));
    }

    public Scripted<bool> Check { get; } = new(_ => Task.FromResult(true));

    public Scripted<PairingTicket> Start { get; }

    public Scripted<PairingApproval> Wait { get; } = new(Scripted<PairingApproval>.Forever);

    /// <summary>Without a script an agent is approved.</summary>
    public Scripted<AgentSignIn> Redeem { get; } = new();

    public Scripted<PairingApproval> Password { get; } = new();

    public List<Uri> Checked { get; } = new();

    public List<Uri> Started { get; } = new();

    public List<WaitCall> Waited { get; } = new();

    public List<string> Redeemed { get; } = new();

    public List<PasswordCall> PasswordsTried { get; } = new();

    public Task CheckAsync(Uri server, CancellationToken cancellationToken)
    {
        Checked.Add(server);
        return Check.Next(cancellationToken);
    }

    public Task<PairingTicket> StartAsync(Uri server, CancellationToken cancellationToken)
    {
        Started.Add(server);
        return Start.Next(cancellationToken);
    }

    public Task<PairingApproval> WaitAsync(PairingTicket ticket, IProgress<bool> progress, CancellationToken cancellationToken)
    {
        Waited.Add(new WaitCall(ticket, progress, cancellationToken));
        return Wait.Next(cancellationToken);
    }

    public Task<AgentSignIn> RedeemAsync(PairingTicket ticket, string agent, CancellationToken cancellationToken)
    {
        Redeemed.Add(agent);
        return Redeem.HasScript ? Redeem.Next(cancellationToken) : Task.FromResult(new AgentSignIn(agent, AgentAccess.Approved));
    }

    public Task<PairingApproval> SignInWithPasswordAsync(Uri server, string email, string password, CancellationToken cancellationToken)
    {
        PasswordsTried.Add(new PasswordCall(server, email, password));
        return Password.Next(cancellationToken);
    }
}

/// <summary>Path B's checks, scripted. Without a script: nothing is found, a usable report names the endpoint's model (or "m1").</summary>
public sealed class FakeEndpointChecker : IEndpointChecker
{
    public FakeEndpointChecker()
    {
        Places["localhost"] = EndpointPlace.ThisComputer;
        Places["127.0.0.1"] = EndpointPlace.ThisComputer;
    }

    public Scripted<IReadOnlyList<LocalEndpoint>> Find { get; } =
        new(_ => Task.FromResult<IReadOnlyList<LocalEndpoint>>(Array.Empty<LocalEndpoint>()));

    /// <summary>Where a host points; the internet when not listed.</summary>
    public Dictionary<string, EndpointPlace> Places { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Scripted<EndpointReport> Report { get; } = new();

    public List<Uri> PlaceAsked { get; } = new();

    /// <summary>When set, telling where an address points fails with it.</summary>
    public Exception? PlaceFailure { get; set; }

    public List<ModelEndpoint> Checked { get; } = new();

    public Task<IReadOnlyList<LocalEndpoint>> FindLocalAsync(CancellationToken cancellationToken) => Find.Next(cancellationToken);

    public Task<EndpointPlace> PlaceOfAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        PlaceAsked.Add(baseUrl);
        if (PlaceFailure is { } failure)
        {
            return Task.FromException<EndpointPlace>(failure);
        }
        return Task.FromResult(Places.TryGetValue(baseUrl.Host, out var place) ? place : EndpointPlace.Internet);
    }

    public Task<EndpointReport> CheckAsync(ModelEndpoint endpoint, CancellationToken cancellationToken)
    {
        Checked.Add(endpoint);
        return Report.HasScript
            ? Report.Next(cancellationToken)
            : Task.FromResult(Reports.Usable(endpoint.BaseUrl, endpoint.Model.Length > 0 ? endpoint.Model : "m1"));
    }
}

public sealed class FakeConnectionStore : IConnectionStore
{
    public List<Uri> Servers { get; } = new();

    public List<ConnectionResult> Saved { get; } = new();

    public List<AcceptanceRecord> Acceptances { get; } = new();

    public List<bool> SignOuts { get; } = new();

    /// <summary>When set, every call fails with it and keeps nothing (the app's settings could not be written).</summary>
    public Exception? Failure { get; set; }

    public Task SaveServerAsync(Uri server, CancellationToken cancellationToken) => Keep(() => Servers.Add(server));

    public Task SaveAsync(ConnectionResult result, CancellationToken cancellationToken) => Keep(() => Saved.Add(result));

    public Task SaveAcceptanceAsync(AcceptanceRecord record, CancellationToken cancellationToken) => Keep(() => Acceptances.Add(record));

    public Task SignOutAsync(bool deleteLocalChats, CancellationToken cancellationToken) => Keep(() => SignOuts.Add(deleteLocalChats));

    private Task Keep(Action keep)
    {
        if (Failure is { } failure)
        {
            return Task.FromException(failure);
        }
        keep();
        return Task.CompletedTask;
    }
}

public sealed class FakeKeyStore : IEndpointKeyStore
{
    public List<(Uri Endpoint, string Key)> Saved { get; } = new();

    public List<ITokenProvider> Providers { get; } = new();

    public List<Uri> Removed { get; } = new();

    public Task<ITokenProvider> SaveAsync(Uri endpoint, string key, CancellationToken cancellationToken)
    {
        Saved.Add((endpoint, key));
        var provider = new StaticTokenProvider(key);
        Providers.Add(provider);
        return Task.FromResult<ITokenProvider>(provider);
    }

    public Task RemoveAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        Removed.Add(endpoint);
        return Task.CompletedTask;
    }
}

public sealed class FakeLinks : ILinkOpener
{
    public List<Uri> Opened { get; } = new();

    public bool TryOpen(Uri address)
    {
        Opened.Add(address);
        return true;
    }
}

public sealed class FakeClipboard : IClipboard
{
    public List<string> Texts { get; } = new();

    public void SetText(string text) => Texts.Add(text);
}

/// <summary>An app step with a point and a scripted result (true unless scripted); it keeps what it was shown.</summary>
public sealed class FakeStep : IWizardStep
{
    private readonly string _name;
    private readonly List<string>? _log;

    public FakeStep(WizardPoint point, string name = "step", List<string>? log = null)
    {
        Point = point;
        _name = name;
        _log = log;
    }

    public WizardPoint Point { get; }

    public Scripted<bool> Script { get; } = new(_ => Task.FromResult(true));

    public List<WizardStepContext> Contexts { get; } = new();

    public Task<bool> RunAsync(WizardStepContext context, CancellationToken cancellationToken)
    {
        Contexts.Add(context);
        _log?.Add(_name);
        return Script.Next(cancellationToken);
    }
}

/// <summary>Time by hand: <see cref="Advance"/> moves <see cref="Now"/> and runs the timers that fall due, in order, those they start too.</summary>
public sealed class ManualClock : IWizardClock
{
    private readonly List<Timer> _timers = new();

    public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The timers that are neither run nor disposed.</summary>
    public int ActiveTimers => _timers.Count(t => !t.Stopped);

    public IDisposable After(TimeSpan delay, Action action)
    {
        var timer = new Timer(Now + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay), action);
        _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan span)
    {
        var target = Now + span;
        while (_timers.Where(t => !t.Stopped && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault() is { } due)
        {
            if (due.Due > Now)
            {
                Now = due.Due;
            }
            due.Stopped = true;
            due.Action();
        }
        Now = target;
    }

    private sealed class Timer : IDisposable
    {
        public Timer(DateTimeOffset due, Action action)
        {
            Due = due;
            Action = action;
        }

        public DateTimeOffset Due { get; }

        public Action Action { get; }

        public bool Stopped { get; set; }

        public void Dispose() => Stopped = true;
    }
}

/// <summary>
/// A UI thread that is the thread that made it: <see cref="CheckAccess"/> is false anywhere else, and what is posted waits
/// until <see cref="Pump"/> runs it there.
/// </summary>
public sealed class QueuedDispatcher : IUiDispatcher
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Queue<Action> _queue = new();

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _thread;

    public void Post(Action action)
    {
        lock (_queue)
        {
            _queue.Enqueue(action);
        }
    }

    public int Queued
    {
        get
        {
            lock (_queue)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>Runs what was posted (and what that posts) on the calling thread; says how many actions ran.</summary>
    public int Pump()
    {
        var ran = 0;
        while (true)
        {
            Action? next;
            lock (_queue)
            {
                next = _queue.Count > 0 ? _queue.Dequeue() : null;
            }
            if (next is null)
            {
                return ran;
            }
            ran++;
            next();
        }
    }
}

/// <summary>A wizard with all its fakes; every call a test makes completes on the calling thread unless a fake keeps it open.</summary>
internal sealed class WizardRig : IDisposable
{
    public static WizardOptions DefaultOptions { get; } = new()
    {
        AppName = "Test app", TermsVersion = "2026-11", Agents = new[] { "main-agent" },
    };

    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public WizardRig(Func<WizardOptions, WizardOptions>? configure = null, IUiDispatcher? dispatcher = null)
    {
        // The test runner's synchronization context would hand every continuation to another thread; the wizard's awaits
        // (which come back to where they began) then go on right where the fake answers, and a test reads the state at once.
        SynchronizationContext.SetSynchronizationContext(null);
        Options = configure is null ? DefaultOptions : configure(DefaultOptions);
        Sign = new FakeServerSignIn(() => Clock.Now);
        Wizard = new ConnectionWizardViewModel(Options, Store, Sign, Keys, Checker, Clock, dispatcher, Clipboard, Links,
            () => Strings);
        Wizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionWizardViewModel.Step))
            {
                Steps.Add(Wizard.Step);
            }
        };
        Wizard.Completed += (_, outcome) => Completed.Add(outcome);
    }

    public WizardOptions Options { get; }

    public FakeConnectionStore Store { get; } = new();

    public FakeServerSignIn Sign { get; }

    public FakeKeyStore Keys { get; } = new();

    public FakeEndpointChecker Checker { get; } = new();

    public ManualClock Clock { get; } = new();

    public FakeLinks Links { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public UiStrings Strings { get; } = UiStrings.For(CultureInfo.GetCultureInfo("de"));

    public ConnectionWizardViewModel Wizard { get; }

    /// <summary>Every step the wizard came to, in order.</summary>
    public List<WizardStep> Steps { get; } = new();

    public List<WizardOutcome> Completed { get; } = new();

    public void Start() => Settle(Wizard.StartAsync());

    /// <summary>Presses a command as a skin would: it must be available.</summary>
    public void Press(IRelayCommand command, object? parameter = null)
    {
        if (command is not IAsyncRelayCommand { IsRunning: true })
        {
            Assert.True(command.CanExecute(parameter), "The command is not available at " + Wizard.Step + ".");
        }
        if (command is IAsyncRelayCommand async)
        {
            Settle(async.ExecuteAsync(parameter));
        }
        else
        {
            command.Execute(parameter);
        }
    }

    public static void Unavailable(IRelayCommand command, string? why = null) =>
        Assert.False(command.CanExecute(null), why ?? "The command should not be available.");

    /// <summary>A call that failed on the spot is a test failure, not a swallowed task.</summary>
    private static void Settle(Task task)
    {
        if (task.IsFaulted)
        {
            ExceptionDispatchInfo.Capture(task.Exception!.InnerException!).Throw();
        }
    }

    /// <summary>The wizard came through exactly these steps, one after the other, at some point.</summary>
    public void AssertPassed(params WizardStep[] path)
    {
        for (var i = 0; i + path.Length <= Steps.Count; i++)
        {
            if (Steps.Skip(i).Take(path.Length).SequenceEqual(path))
            {
                return;
            }
        }
        Assert.Fail("Expected the path " + string.Join(" > ", path) + " but the wizard went " + string.Join(" > ", Steps));
    }

    public void Dispose()
    {
        Wizard.Dispose();
        SynchronizationContext.SetSynchronizationContext(_context);
    }
}
