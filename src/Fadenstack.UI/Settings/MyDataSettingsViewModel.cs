using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.Agents.Audit;
using Fadenstack.UI.Services;
using Fadenstack.UI.Skills;

namespace Fadenstack.UI.Settings;

/// <summary>Where "Your data on this device" reads the apps and exports and removes the data: an agent (<see cref="MyDataSettingsViewModel(FadenstackAgent, IMyDataSaveTarget, IUiDispatcher?, Func{UiStrings}?)"/>), or a test's.</summary>
public interface IMyDataSource
{
    /// <summary>The apps that use the person's data on this device, this app first (<see cref="FadenstackAgent.MyDataAppsAsync"/>).</summary>
    Task<IReadOnlyList<MyDataApp>> AppsAsync(CancellationToken cancellationToken);

    /// <summary>Removes the person's data in <paramref name="scope"/>; says what went and what stayed.</summary>
    Task<DataDeletion> DeleteAsync(DataScope scope, CancellationToken cancellationToken);

    /// <summary>Writes the ZIP of the person's data in <paramref name="scope"/> to <paramref name="destination"/> (left open).</summary>
    Task ExportAsync(Stream destination, DataScope scope, CancellationToken cancellationToken);
}

/// <summary>
/// What a source adds when it keeps the person's record of tool calls (sdk/51 8.10): the check of the record and its export as a web
/// page. The agent's source does; a source that does not leaves the line and its button out. TS: <c>MyDataSource.audit</c>.
/// </summary>
public interface IMyDataAuditSource
{
    /// <summary>Checks the person's record of tool calls on this device (<see cref="FadenstackAgent.VerifyAuditAsync"/>).</summary>
    Task<AuditVerification> AuditAsync(CancellationToken cancellationToken);

    /// <summary>Writes the record as one self-contained HTML page to <paramref name="destination"/> (left open).</summary>
    Task ExportAuditAsync(Stream destination, CancellationToken cancellationToken);
}

/// <summary>
/// Where an export goes: the host picks it (a save dialog), because a view model never opens a dialog. Each skin implements it with
/// the system's dialog; a host without one answers null.
/// </summary>
public interface IMyDataSaveTarget
{
    /// <summary>Opens the place the person chose for the export, to write to; null when they cancelled. The caller disposes the stream.</summary>
    Task<Stream?> OpenAsync(string suggestedFileName, CancellationToken cancellationToken);
}

/// <summary>What "Your data on this device" shows.</summary>
public enum MyDataState
{
    /// <summary>The buttons: export, remove this app's data, remove everything.</summary>
    Idle,

    /// <summary>"Remove my data…" was pressed: what goes and what stays, with Remove and Cancel.</summary>
    ConfirmThisApp,

    /// <summary>"Remove all my Fadenstack data on this device…" was pressed: every app it affects, with Remove and Cancel.</summary>
    ConfirmAll,

    /// <summary>Reading the apps, exporting or removing.</summary>
    Busy,

    /// <summary>A removal is done: what was removed and what stayed.</summary>
    Done,

    /// <summary>Something did not work; a sentence says so (never why: no exception text, no content).</summary>
    Error,
}

/// <summary>
/// "Your data on this device", for an app's settings page (owner, 16 Nov): the person exports their data, removes this app's data
/// ("Remove my data…": its chats, approvals and preferences, and the shared data only when no other Fadenstack app on the device
/// uses it) or removes all of their Fadenstack data on the device, after a confirmation that says what goes and what stays and names
/// the apps (<see cref="FadenstackAgent.MyDataAppsAsync"/>, <see cref="FadenstackAgent.DeleteMyDataAsync(DataScope, CancellationToken)"/>,
/// <see cref="FadenstackAgent.ExportMyDataAsync(Stream, DataScope, CancellationToken)"/>; ASVS 5.0 V14, data protection). The export
/// follows the same scope. Where the export goes is the host's choice (<see cref="IMyDataSaveTarget"/>); this class opens no dialog.
/// A view binds to it and holds no logic: the texts come from <see cref="Strings"/> (say them again on <see cref="Retext"/>), the
/// state and the buttons are here.
/// </summary>
public sealed class MyDataSettingsViewModel : ObservableObject, ISkillTexts
{
    private enum Failure
    {
        None,
        Read,
        Remove,
        Export,
    }

    private readonly IMyDataSource _source;
    private readonly IMyDataSaveTarget _saveTarget;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<UiStrings> _strings;
    private MyDataState _state = MyDataState.Idle;
    private IReadOnlyList<MyDataApp> _apps = Array.Empty<MyDataApp>();
    private DataDeletion? _deletion;
    private Failure _failure;
    private bool _exported;
    private bool _picking;
    private AuditVerification? _audit;
    private bool _auditUnreadable;

    /// <param name="agent">The agent whose app it is.</param>
    /// <param name="saveTarget">Where the host lets the person save an export.</param>
    /// <param name="dispatcher">The UI thread; inline by default.</param>
    /// <param name="strings">The texts; the current UI language's by default.</param>
    public MyDataSettingsViewModel(FadenstackAgent agent, IMyDataSaveTarget saveTarget, IUiDispatcher? dispatcher = null, Func<UiStrings>? strings = null)
        : this(new AgentSource(agent ?? throw new ArgumentNullException(nameof(agent))), saveTarget, dispatcher, strings)
    {
    }

    /// <param name="source">Where the apps come from and the data goes.</param>
    /// <param name="saveTarget">Where the host lets the person save an export.</param>
    /// <param name="dispatcher">The UI thread; inline by default.</param>
    /// <param name="strings">The texts; the current UI language's by default.</param>
    public MyDataSettingsViewModel(IMyDataSource source, IMyDataSaveTarget saveTarget, IUiDispatcher? dispatcher = null, Func<UiStrings>? strings = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _saveTarget = saveTarget ?? throw new ArgumentNullException(nameof(saveTarget));
        _dispatcher = dispatcher ?? InlineDispatcher.Instance;
        var current = UiStrings.Current;
        _strings = strings ?? (() => current);
        ExportCommand = new AsyncRelayCommand(() => ExportAsync(DataScope.ThisApp), CanStart);
        ExportAllCommand = new AsyncRelayCommand(() => ExportAsync(DataScope.AllApps), CanStart);
        RemoveCommand = new AsyncRelayCommand(() => BeginRemoveAsync(DataScope.ThisApp), CanStart);
        RemoveAllCommand = new AsyncRelayCommand(() => BeginRemoveAsync(DataScope.AllApps), CanStart);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => IsConfirming);
        CancelCommand = new RelayCommand(Cancel, () => IsConfirming || IsDone || IsError);
        ExportAuditCommand = new AsyncRelayCommand(ExportAuditAsync, () => CanStart() && HasAudit);
        AuditLoading = RefreshAuditAsync();
    }

    /// <summary>The first check of the record of tool calls (a test waits for it).</summary>
    public Task AuditLoading { get; }

    /// <summary>Whether the source keeps the person's record of tool calls: the line and the button show.</summary>
    public bool HasAudit => _source is IMyDataAuditSource;

    /// <summary>
    /// "Record of tool calls: N entries since &lt;date&gt;, intact", or "damaged at entry N", "no entries yet", "could not be checked"; empty until
    /// the first check is done, and where the source keeps no record.
    /// </summary>
    public string AuditLine
    {
        get
        {
            if (_auditUnreadable)
            {
                return Strings.MyDataAuditUnreadable;
            }
            if (_audit is not { } audit)
            {
                return "";
            }
            if (!audit.Intact)
            {
                return Strings.MyDataAuditDamaged(audit.BrokenAtSeq ?? audit.FirstSeq ?? 0);
            }
            if (audit.Entries == 0)
            {
                return Strings.MyDataAuditEmpty;
            }
            var since = (audit.FirstTime ?? DateTimeOffset.UtcNow).ToLocalTime().ToString("d", Strings.FormatCulture);
            return audit.Entries == 1 ? Strings.MyDataAuditIntactOne(since) : Strings.MyDataAuditIntact(audit.Entries, since);
        }
    }

    public bool HasAuditLine => AuditLine.Length > 0;

    /// <summary>The record is damaged: the line shows as a warning, not as a caption.</summary>
    public bool AuditDamaged => _auditUnreadable || _audit is { Intact: false };

    /// <summary>Saves the record of tool calls as one web page (a table; no script, nothing loaded from outside).</summary>
    public IAsyncRelayCommand ExportAuditCommand { get; }

    public UiStrings Strings => _strings();

    /// <summary>What the control shows now.</summary>
    public MyDataState State => _state;

    public bool IsIdle => _state == MyDataState.Idle;

    public bool IsConfirmThisApp => _state == MyDataState.ConfirmThisApp;

    public bool IsConfirmAll => _state == MyDataState.ConfirmAll;

    /// <summary>Either confirmation shows.</summary>
    public bool IsConfirming => IsConfirmThisApp || IsConfirmAll;

    public bool IsBusy => _state == MyDataState.Busy;

    public bool IsDone => _state == MyDataState.Done;

    public bool IsError => _state == MyDataState.Error;

    /// <summary>The apps that use the person's data on this device other than this one, by the names they gave (an app on an older SDK shows as its agent).</summary>
    public IReadOnlyList<string> OtherApps => _apps.Where(a => !a.IsThisApp).Select(a => a.Name).ToList();

    /// <summary>"Remove my data…" would keep the shared data because other apps use it (the first confirmation text); false: everything goes.</summary>
    public bool SharedDataStays => IsConfirmThisApp && OtherApps.Count > 0;

    /// <summary>What the confirmation says: what goes and what stays (with the other apps' names), or that everything of the person's goes.</summary>
    public string ConfirmText => _state switch
    {
        MyDataState.ConfirmThisApp => SharedDataStays
            ? Strings.MyDataConfirmThisAppShared(Join(OtherApps))
            : Strings.MyDataConfirmThisAppEverything,
        MyDataState.ConfirmAll => Strings.MyDataConfirmAll,
        _ => "",
    };

    /// <summary>"This cannot be undone." under either confirmation.</summary>
    public string UndoText => IsConfirming ? Strings.MyDataConfirmUndo : "";

    /// <summary>Every app a removal of everything affects, this app marked; empty in the other states.</summary>
    public IReadOnlyList<string> AppLines => IsConfirmAll
        ? _apps.Select(a => a.IsThisApp ? Strings.MyDataThisApp(a.Name) : a.Name).ToList()
        : Array.Empty<string>();

    public bool HasAppLines => AppLines.Count > 0;

    /// <summary>The text of the button that goes ahead with what the confirmation says.</summary>
    public string ConfirmButtonText => _state switch
    {
        MyDataState.ConfirmThisApp => Strings.MyDataConfirmRemove,
        MyDataState.ConfirmAll => Strings.MyDataConfirmRemoveAll,
        _ => "",
    };

    /// <summary>"Please wait…" while busy; "Removed everything." or "Removed. Shared data kept for: A, B." when a removal is done; "Your data was saved…" after an export.</summary>
    public string StatusText => _state switch
    {
        MyDataState.Busy => Strings.MyDataBusy,
        MyDataState.Done => DoneText(),
        MyDataState.Idle when _exported => Strings.MyDataExported,
        _ => "",
    };

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>The status shows as a plain line (busy, or an export that was saved); a finished removal has a banner of its own.</summary>
    public bool HasStatusLine => HasStatus && !IsDone;

    /// <summary>A result or an error shows, with the button that closes it.</summary>
    public bool HasResult => IsDone || IsError;

    /// <summary>What went wrong, in one sentence and without a reason (no exception text, nothing from the content); empty unless <see cref="IsError"/>.</summary>
    public string ErrorText => _state != MyDataState.Error ? "" : _failure switch
    {
        Failure.Read => Strings.MyDataErrorRead,
        Failure.Export => Strings.MyDataErrorExport,
        _ => Strings.MyDataErrorRemove,
    };

    /// <summary>Saves a ZIP of this app's data (what "Remove my data…" would take).</summary>
    public IAsyncRelayCommand ExportCommand { get; }

    /// <summary>Saves a ZIP of all the person's data on this device.</summary>
    public IAsyncRelayCommand ExportAllCommand { get; }

    /// <summary>"Remove my data…": reads the apps, then asks for confirmation (<see cref="MyDataState.ConfirmThisApp"/>).</summary>
    public IAsyncRelayCommand RemoveCommand { get; }

    /// <summary>"Remove all my Fadenstack data on this device…": reads the apps, then asks for confirmation (<see cref="MyDataState.ConfirmAll"/>).</summary>
    public IAsyncRelayCommand RemoveAllCommand { get; }

    /// <summary>Goes ahead with what the confirmation says.</summary>
    public IAsyncRelayCommand ConfirmCommand { get; }

    /// <summary>Leaves a confirmation, or dismisses the result or the error: back to the buttons.</summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>The app's language changed: every text is said again.</summary>
    public void Retext()
    {
        OnPropertyChanged(nameof(Strings));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(AppLines));
        OnPropertyChanged(nameof(ConfirmButtonText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(AuditLine));
    }

    /// <summary>
    /// Reads what the control shows again, so it says what is true now (the record of tool calls grows as the agents work). The views call
    /// it whenever the control is shown: when it loads and when it becomes visible. Cheap; does nothing while an action works. TS: <c>refresh()</c>.
    /// </summary>
    public Task RefreshAsync() => IsBusy ? Task.CompletedTask : RefreshAuditAsync();

    /// <summary>Checks the record of tool calls again (it changes as the agents work); a failed check is a sentence, never its reason.</summary>
    public async Task RefreshAuditAsync()
    {
        if (_source is not IMyDataAuditSource audit)
        {
            return;
        }
        AuditVerification? result = null;
        try
        {
            result = await audit.AuditAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // said as a sentence, without the reason
        }
        _dispatcher.Post(() =>
        {
            (_audit, _auditUnreadable) = (result, result is null);
            OnPropertyChanged(nameof(AuditLine));
            OnPropertyChanged(nameof(HasAuditLine));
            OnPropertyChanged(nameof(AuditDamaged));
            NotifyCommands();
        });
    }

    private bool CanStart() => IsIdle && !_picking;

    private string Join(IEnumerable<string> names) => string.Join(Strings.MyDataListSeparator, names);

    private string DoneText()
    {
        if (_deletion is not { } deletion)
        {
            return "";
        }
        var parts = new List<string>
        {
            deletion.Scope == DataScope.ThisApp && deletion.SharedDataKept
                ? Strings.MyDataRemovedKept(Join(deletion.OtherApps))
                : Strings.MyDataRemovedEverything,
        };
        if (deletion.AgentsKept.Count > 0)
        {
            parts.Add(Strings.MyDataAgentsKept(Join(deletion.AgentsKept)));
        }
        if (deletion.HostStoresLeft)
        {
            parts.Add(Strings.MyDataHostStores);
        }
        return string.Join(" ", parts);
    }

    private async Task BeginRemoveAsync(DataScope scope)
    {
        _exported = false;
        SetState(MyDataState.Busy);
        IReadOnlyList<MyDataApp>? apps = null;
        try
        {
            apps = await _source.AppsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // said as a sentence, without the reason
        }
        _dispatcher.Post(() =>
        {
            if (apps is null)
            {
                Fail(Failure.Read);
                return;
            }
            _apps = apps;
            SetState(scope == DataScope.AllApps ? MyDataState.ConfirmAll : MyDataState.ConfirmThisApp);
        });
    }

    private async Task ConfirmAsync()
    {
        var scope = IsConfirmAll ? DataScope.AllApps : DataScope.ThisApp;
        SetState(MyDataState.Busy);
        DataDeletion? deletion = null;
        try
        {
            deletion = await _source.DeleteAsync(scope, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // said as a sentence, without the reason
        }
        _dispatcher.Post(() =>
        {
            if (deletion is null)
            {
                Fail(Failure.Remove);
                return;
            }
            _deletion = deletion;
            SetState(MyDataState.Done);
        });
    }

    private async Task ExportAsync(DataScope scope)
    {
        Stream? stream;
        _picking = true;
        NotifyCommands();
        try
        {
            // the host's dialog opens on the UI thread: no ConfigureAwait(false) before it has answered
            stream = await _saveTarget.OpenAsync(scope == DataScope.AllApps ? AllFileName : ThisAppFileName, CancellationToken.None);
        }
        catch (Exception)
        {
            _picking = false;
            Fail(Failure.Export);
            return;
        }
        _picking = false;
        if (stream is null)
        {
            NotifyCommands(); // cancelled: nothing changes
            return;
        }
        _exported = false;
        SetState(MyDataState.Busy);
        var written = false;
        try
        {
            using (stream)
            {
                await _source.ExportAsync(stream, scope, CancellationToken.None).ConfigureAwait(false);
            }
            written = true;
        }
        catch (Exception)
        {
            // said as a sentence, without the reason
        }
        _dispatcher.Post(() =>
        {
            if (!written)
            {
                Fail(Failure.Export);
                return;
            }
            _exported = true;
            SetState(MyDataState.Idle);
        });
    }

    private void Cancel()
    {
        _exported = false;
        _deletion = null;
        SetState(MyDataState.Idle);
        _ = RefreshAuditAsync(); // a removal may have taken the record
    }

    private async Task ExportAuditAsync()
    {
        if (_source is not IMyDataAuditSource audit)
        {
            return;
        }
        Stream? stream;
        _picking = true;
        NotifyCommands();
        try
        {
            // the host's dialog opens on the UI thread: no ConfigureAwait(false) before it has answered
            stream = await _saveTarget.OpenAsync(AuditFileName, CancellationToken.None);
        }
        catch (Exception)
        {
            _picking = false;
            Fail(Failure.Export);
            return;
        }
        _picking = false;
        if (stream is null)
        {
            NotifyCommands(); // cancelled: nothing changes
            return;
        }
        _exported = false;
        SetState(MyDataState.Busy);
        var written = false;
        try
        {
            using (stream)
            {
                await audit.ExportAuditAsync(stream, CancellationToken.None).ConfigureAwait(false);
            }
            written = true;
        }
        catch (Exception)
        {
            // said as a sentence, without the reason
        }
        _dispatcher.Post(() =>
        {
            if (!written)
            {
                Fail(Failure.Export);
                return;
            }
            _exported = true;
            SetState(MyDataState.Idle);
        });
    }

    private void Fail(Failure failure)
    {
        _failure = failure;
        SetState(MyDataState.Error);
    }

    private void SetState(MyDataState state)
    {
        _state = state;
        OnPropertyChanged(string.Empty);
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        ExportCommand.NotifyCanExecuteChanged();
        ExportAllCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        RemoveAllCommand.NotifyCanExecuteChanged();
        ExportAuditCommand.NotifyCanExecuteChanged();
        ConfirmCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The names the export is suggested under (the host's dialog may change them).</summary>
    internal const string ThisAppFileName = "fadenstack-my-data.zip";

    internal const string AllFileName = "fadenstack-all-my-data.zip";

    /// <summary>The name the record of tool calls is suggested under.</summary>
    internal const string AuditFileName = "fadenstack-tool-calls.html";

    private sealed class AgentSource : IMyDataSource, IMyDataAuditSource
    {
        private readonly FadenstackAgent _agent;

        internal AgentSource(FadenstackAgent agent) => _agent = agent;

        public Task<IReadOnlyList<MyDataApp>> AppsAsync(CancellationToken cancellationToken) => _agent.MyDataAppsAsync(cancellationToken);

        public Task<DataDeletion> DeleteAsync(DataScope scope, CancellationToken cancellationToken) => _agent.DeleteMyDataAsync(scope, cancellationToken);

        public Task ExportAsync(Stream destination, DataScope scope, CancellationToken cancellationToken) =>
            _agent.ExportMyDataAsync(destination, scope, cancellationToken);

        public Task<AuditVerification> AuditAsync(CancellationToken cancellationToken) => _agent.VerifyAuditAsync(cancellationToken);

        public Task ExportAuditAsync(Stream destination, CancellationToken cancellationToken) =>
            _agent.ExportAuditAsync(AuditExportFormat.Html, destination, cancellationToken);
    }
}
