using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.UI.Services;
using Fadenstack.UI.Skills;

namespace Fadenstack.UI.Settings;

/// <summary>
/// Where content logging's band and setting read and write its state: an agent (<see cref="ContentLoggingSource.For"/>), or a test's.
/// TS: <c>ContentLoggingSource</c>.
/// </summary>
public interface IContentLoggingSource
{
    /// <summary>The app offers content logging and the machine policy allows it: only then is there anything to show.</summary>
    bool CanStart { get; }

    /// <summary>Content logging is on now.</summary>
    bool IsOn { get; }

    /// <summary>When it ends (UTC); null while off.</summary>
    DateTimeOffset? Until { get; }

    /// <summary>Switches it on for a time (15 minutes to 24 hours).</summary>
    void Start(TimeSpan duration);

    /// <summary>Switches it off.</summary>
    void Stop();

    /// <summary>It went on or off, also by itself at its end; can come from a timer thread.</summary>
    event EventHandler? Changed;
}

/// <summary>The agent as a <see cref="IContentLoggingSource"/>.</summary>
public static class ContentLoggingSource
{
    /// <summary>The content logging of <paramref name="agent"/> (<see cref="FadenstackAgent.ContentLogging"/>).</summary>
    public static IContentLoggingSource For(FadenstackAgent agent) => new AgentSource((agent ?? throw new ArgumentNullException(nameof(agent))).ContentLogging, agent);

    private sealed class AgentSource : IContentLoggingSource
    {
        private readonly ContentLoggingState _state;
        private readonly FadenstackAgent _agent;

        internal AgentSource(ContentLoggingState state, FadenstackAgent agent)
        {
            _state = state;
            _agent = agent;
        }

        public bool CanStart => _state.CanStart;

        public bool IsOn => _state.IsOn;

        public DateTimeOffset? Until => _state.Until;

        public void Start(TimeSpan duration) => _agent.StartContentLogging(duration);

        public void Stop() => _agent.StopContentLogging();

        public event EventHandler? Changed
        {
            add => _state.Changed += value;
            remove => _state.Changed -= value;
        }
    }
}

/// <summary>The end time as the person reads it: the time of day in their locale, with the date when it is not today.</summary>
internal static class ContentLoggingText
{
    internal static string Time(DateTimeOffset until, DateTimeOffset now, UiStrings strings)
    {
        var local = until.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("t", strings.FormatCulture)
            : local.ToString("g", strings.FormatCulture);
    }
}

/// <summary>One of the times the person can switch content logging on for.</summary>
/// <param name="Duration">How long.</param>
/// <param name="Text">"1 hour", "4 hours", "24 hours" in the app's language.</param>
public sealed record ContentLoggingChoice(TimeSpan Duration, string Text);

/// <summary>
/// The setting for content logging, for an app's settings page (8.1): only when the app offers it and the machine policy allows it
/// (<see cref="IsAvailable"/>), a choice of 1 hour, 4 hours or 24 hours, Start and Stop, the end time while it is on, and a sentence saying
/// what is logged and that it ends by itself. The band in the chat (<see cref="ContentLoggingBandViewModel"/>) shows it while it is on. A view
/// binds to this class and holds no logic: the texts come from <see cref="Strings"/> (say them again on <see cref="Retext"/>). TS:
/// <c>ContentLoggingSettingsViewModel</c>.
/// </summary>
public sealed class ContentLoggingSettingsViewModel : ObservableObject, ISkillTexts, IDisposable
{
    private readonly IContentLoggingSource _source;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<UiStrings> _strings;
    private readonly Func<DateTimeOffset> _clock;
    private ContentLoggingChoice _selected;

    /// <param name="agent">The agent whose content logging it is.</param>
    /// <param name="dispatcher">The UI thread; inline by default.</param>
    /// <param name="strings">The texts; the current UI language's by default.</param>
    /// <param name="clock">The time now, to say whether the end is today; the system clock by default.</param>
    public ContentLoggingSettingsViewModel(FadenstackAgent agent, IUiDispatcher? dispatcher = null, Func<UiStrings>? strings = null, Func<DateTimeOffset>? clock = null)
        : this(ContentLoggingSource.For(agent), dispatcher, strings, clock)
    {
    }

    /// <param name="source">Where the state comes from and goes to.</param>
    /// <param name="dispatcher">The UI thread; inline by default.</param>
    /// <param name="strings">The texts; the current UI language's by default.</param>
    /// <param name="clock">The time now, to say whether the end is today; the system clock by default.</param>
    public ContentLoggingSettingsViewModel(IContentLoggingSource source, IUiDispatcher? dispatcher = null, Func<UiStrings>? strings = null, Func<DateTimeOffset>? clock = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _dispatcher = dispatcher ?? InlineDispatcher.Instance;
        var current = UiStrings.Current;
        _strings = strings ?? (() => current);
        _clock = clock ?? (() => DateTimeOffset.Now);
        Choices = new ObservableCollection<ContentLoggingChoice>(MakeChoices(Strings));
        _selected = Choices[0];
        StartCommand = new RelayCommand(Start, () => CanStart);
        StopCommand = new RelayCommand(Stop, () => IsOn);
        _source.Changed += OnChanged;
    }

    public UiStrings Strings => _strings();

    /// <summary>The app offers content logging and the policy allows it: the control shows only then.</summary>
    public bool IsAvailable => _source.CanStart;

    /// <summary>Content logging is on now.</summary>
    public bool IsOn => _source.IsOn;

    /// <summary>The times to choose from: 1 hour, 4 hours, 24 hours.</summary>
    public ObservableCollection<ContentLoggingChoice> Choices { get; }

    /// <summary>The time chosen; one hour at first.</summary>
    public ContentLoggingChoice SelectedChoice
    {
        get => _selected;
        set
        {
            if (value is not null && SetProperty(ref _selected, value))
            {
                StartCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The choice can be changed and Start pressed: available and off.</summary>
    public bool CanStart => IsAvailable && !IsOn;

    /// <summary>"On until 14:30." while it is on; empty when off.</summary>
    public string OnUntilText => _source.IsOn && _source.Until is { } until
        ? Strings.ContentLoggingOnUntil(ContentLoggingText.Time(until, _clock(), Strings)) : "";

    /// <summary>What is logged, and that it ends by itself.</summary>
    public string IntroText => Strings.ContentLoggingIntro;

    public IRelayCommand StartCommand { get; }

    public IRelayCommand StopCommand { get; }

    /// <summary>The app's language changed: every text is said again.</summary>
    public void Retext()
    {
        var chosen = _selected.Duration;
        Choices.Clear();
        foreach (var choice in MakeChoices(Strings))
        {
            Choices.Add(choice);
        }
        _selected = Choices.First(c => c.Duration == chosen);
        OnPropertyChanged(string.Empty);
    }

    public void Dispose() => _source.Changed -= OnChanged;

    private void Start()
    {
        if (!CanStart)
        {
            return;
        }
        try
        {
            _source.Start(_selected.Duration);
        }
        catch (ContentLoggingException)
        {
            Refresh(); // the app's offer or the policy changed under the control: it shows what is true now
        }
    }

    private void Stop() => _source.Stop();

    private void OnChanged(object? sender, EventArgs e) => _dispatcher.Post(Refresh);

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(OnUntilText));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private static IEnumerable<ContentLoggingChoice> MakeChoices(UiStrings s) => new[]
    {
        new ContentLoggingChoice(TimeSpan.FromHours(1), s.ContentLoggingOneHour),
        new ContentLoggingChoice(TimeSpan.FromHours(4), s.ContentLoggingFourHours),
        new ContentLoggingChoice(TimeSpan.FromHours(24), s.ContentLoggingDay),
    };
}

/// <summary>
/// The band in the chat while content logging is on (8.1): "Content logging is on until 14:30. Prompts and answers are written to the
/// app's logs." with a Stop button. It shows only while content logging is on, so it is the person's reminder that it is. The chat view
/// model has one (<c>AgentChatViewModel.ContentLogging</c>); a host with its own chat surface can make its own. TS:
/// <c>ContentLoggingBandViewModel</c>.
/// </summary>
public sealed class ContentLoggingBandViewModel : ObservableObject, IDisposable
{
    private readonly IContentLoggingSource _source;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<UiStrings> _strings;
    private readonly Func<DateTimeOffset> _clock;

    public ContentLoggingBandViewModel(IContentLoggingSource source, IUiDispatcher? dispatcher = null, Func<UiStrings>? strings = null, Func<DateTimeOffset>? clock = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _dispatcher = dispatcher ?? InlineDispatcher.Instance;
        var current = UiStrings.Current;
        _strings = strings ?? (() => current);
        _clock = clock ?? (() => DateTimeOffset.Now);
        StopCommand = new RelayCommand(() => _source.Stop(), () => IsShown);
        _source.Changed += OnChanged;
    }

    /// <summary>Content logging is on: the band shows.</summary>
    public bool IsShown => _source.IsOn;

    /// <summary>"Content logging is on until 14:30. Prompts and answers are written to the app's logs."; empty while off.</summary>
    public string Text => _source.IsOn && _source.Until is { } until
        ? _strings().ContentLoggingBand(ContentLoggingText.Time(until, _clock(), _strings())) : "";

    /// <summary>"Stop".</summary>
    public string StopText => _strings().MarkerStop;

    /// <summary>"Stop content logging", the button's name for a screen reader.</summary>
    public string StopName => _strings().ContentLoggingStopName;

    public IRelayCommand StopCommand { get; }

    /// <summary>The app's language changed: every text is said again.</summary>
    public void Retext() => OnPropertyChanged(string.Empty);

    public void Dispose() => _source.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(Text));
        StopCommand.NotifyCanExecuteChanged();
    });
}
