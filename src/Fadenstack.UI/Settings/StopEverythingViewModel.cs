using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.Settings;

/// <summary>
/// "Stop everything", the emergency stop an app hosts in its tray menu or title bar (release plan 8.11): one press stops every running
/// chat of the agents it is given and ends their grants (<see cref="FadenstackAgent.StopAllAsync(IEnumerable{FadenstackAgent}, CancellationToken)"/>),
/// then says what it did in one line. There is no confirmation: it is the emergency stop. What the person allowed always stays.
/// The agents come as a function because they come and go. A view binds to it and holds no logic: the texts come from
/// <see cref="Strings"/>. TypeScript: <c>StopEverythingViewModel</c>.
/// </summary>
public sealed class StopEverythingViewModel : ObservableObject
{
    private readonly Func<IEnumerable<FadenstackAgent>> _agents;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<UiStrings> _strings;
    private readonly Func<IReadOnlyList<FadenstackAgent>, Task<StopAllResult>> _stop;
    private bool _isBusy;
    private StopAllResult? _result;

    /// <param name="agents">The agents that run now; asked at each press.</param>
    /// <param name="dispatcher">The UI thread; inline by default.</param>
    /// <param name="strings">The texts; the current UI language's by default.</param>
    public StopEverythingViewModel(Func<IEnumerable<FadenstackAgent>> agents, IUiDispatcher? dispatcher = null, Func<UiStrings>? strings = null)
        : this(agents, list => FadenstackAgent.StopAllAsync(list), dispatcher, strings)
    {
    }

    internal StopEverythingViewModel(Func<IEnumerable<FadenstackAgent>> agents, Func<IReadOnlyList<FadenstackAgent>, Task<StopAllResult>> stop,
        IUiDispatcher? dispatcher, Func<UiStrings>? strings)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _stop = stop;
        _dispatcher = dispatcher ?? InlineDispatcher.Instance;
        var current = UiStrings.Current;
        _strings = strings ?? (() => current);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !_isBusy);
    }

    public UiStrings Strings => _strings();

    /// <summary>The accessible name and the tooltip's title: "Stop everything".</summary>
    public string Name => Strings.StopEverything;

    /// <summary>The tooltip: what the button does and what stays.</summary>
    public string ToolTip => Strings.StopEverythingTip;

    /// <summary>The stop is working.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(ResultText));
                OnPropertyChanged(nameof(HasResult));
                StopCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>What the stop did: "Stopped 2 chats and ended 3 grants.", "Nothing was running.", or that the server could not be told. Empty before the first press.</summary>
    public string ResultText => _isBusy ? Strings.StopEverythingBusy : Describe(_result, Strings);

    public bool HasResult => ResultText.Length > 0;

    /// <summary>The counts of the last stop; null before the first press.</summary>
    public StopAllResult? Result => _result;

    /// <summary>Stops everything at once, with no confirmation.</summary>
    public IAsyncRelayCommand StopCommand { get; }

    /// <summary>Says the last result again in a new language.</summary>
    public void Retext()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(ResultText));
    }

    private async Task StopAsync()
    {
        IsBusy = true;
        StopAllResult result;
        try
        {
            result = await _stop(_agents().Where(a => a is not null).ToList()).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // What was local happened; the person is told what is known: nothing was counted.
            result = new StopAllResult();
        }
        _dispatcher.Post(() =>
        {
            _result = result;
            OnPropertyChanged(nameof(Result));
            IsBusy = false;
            OnPropertyChanged(nameof(ResultText));
            OnPropertyChanged(nameof(HasResult));
        });
    }

    /// <summary>The one line for a result, in <paramref name="strings"/>'s language.</summary>
    public static string Describe(StopAllResult? result, UiStrings strings)
    {
        if (result is null)
        {
            return "";
        }
        if (result.GrantsNotTold > 0)
        {
            return strings.StopEverythingNotTold(strings.StopEverythingGrants(result.GrantsNotTold));
        }
        if (result.NothingRan)
        {
            return strings.StopEverythingNothing;
        }
        var chats = strings.StopEverythingChats(result.TurnsStopped);
        var grants = strings.StopEverythingGrants(result.GrantsEnded);
        return result.TurnsStopped > 0 && result.GrantsEnded > 0 ? strings.StopEverythingBoth(chats, grants)
            : result.TurnsStopped > 0 ? strings.StopEverythingChatsOnly(chats)
            : strings.StopEverythingGrantsOnly(grants);
    }
}
