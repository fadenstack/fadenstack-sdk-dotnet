using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.LocalTools;

namespace Fadenstack.UI.Tools;

/// <summary>One choice of a tool's raise picker: none, read or write.</summary>
public sealed class RaiseOptionViewModel
{
    internal RaiseOptionViewModel(LocalToolRaise raise, string text)
    {
        Raise = raise;
        Text = text;
    }

    public LocalToolRaise Raise { get; }

    public string Text { get; }
}

/// <summary>
/// A tool of a local server in the drawer: its name, what the server says it does, the server's own hints as outlined badges, the
/// class it has now, and the person's raise. A raise picker (as it arrives, reads only, changes things) is offered for every tool
/// but one the server says is destructive: that one shows "Destructive" fixed, because a destructive hint is a floor.
/// </summary>
public sealed class LocalToolViewModel : ObservableObject
{
    private readonly ServerDetailsViewModel _details;
    private bool _updating;
    private RaiseOptionViewModel? _selectedRaise;

    internal LocalToolViewModel(ServerDetailsViewModel details, LocalToolInfo tool)
    {
        _details = details;
        Name = tool.Name;
        Update(tool);
    }

    public string Name { get; private set; }

    public string Description { get; private set; } = "";

    public bool HasDescription => Description.Length > 0;

    /// <summary>"Says: reads only": the server's own word about the tool, labelled as such, each as its own badge.</summary>
    public IReadOnlyList<string> Hints { get; private set; } = Array.Empty<string>();

    public bool HasHints => Hints.Count > 0;

    /// <summary>The class it has now: "Asks every time", "Reads", "Changes things" or "Destructive".</summary>
    public string ClassText { get; private set; } = "";

    public LocalToolClass Class { get; private set; }

    /// <summary>The server says the tool may delete or overwrite: no picker, "Destructive" fixed.</summary>
    public bool IsDestructive { get; private set; }

    /// <summary>"The server says this tool may delete or overwrite. It always asks, and nothing can lower that."</summary>
    public string DestructiveHelp { get; private set; } = "";

    /// <summary>The person can raise it (and can change marks at all: standalone); never below a destructive hint.</summary>
    public bool CanRaise { get; private set; }

    /// <summary>As it arrives, reads only, changes things; empty for a destructive tool.</summary>
    public IReadOnlyList<RaiseOptionViewModel> RaiseOptions { get; private set; } = Array.Empty<RaiseOptionViewModel>();

    /// <summary>The picker's choice; choosing one tells the catalog.</summary>
    public RaiseOptionViewModel? SelectedRaise
    {
        get => _selectedRaise;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedRaise))
            {
                return;
            }
            if (SetProperty(ref _selectedRaise, value) && !_updating && CanRaise)
            {
                _details.SetRaise(Name, value.Raise);
            }
        }
    }

    /// <summary>"Class of look": the picker's name for a screen reader, so the pickers of two rows are told apart.</summary>
    public string RaiseName { get; private set; } = "";

    internal void Update(LocalToolInfo tool)
    {
        var s = _details.Strings;
        _updating = true;
        try
        {
            Description = tool.Description;
            var hints = new List<string>();
            if (tool.ReadOnly == true)
            {
                hints.Add(s.ToolsSaysReadOnly);
            }
            if (tool.Destructive == true)
            {
                hints.Add(s.ToolsSaysDestructive);
            }
            if (tool.Idempotent == true)
            {
                hints.Add(s.ToolsSaysIdempotent);
            }
            if (tool.OpenWorld == true)
            {
                hints.Add(s.ToolsSaysOpenWorld);
            }
            Hints = hints;
            Class = tool.Class;
            IsDestructive = tool.Destructive == true || tool.Class == LocalToolClass.Destructive;
            ClassText = tool.Class switch
            {
                LocalToolClass.Read => s.ToolsClassRead,
                LocalToolClass.Write => s.ToolsClassWrite,
                LocalToolClass.Destructive => s.ToolsClassDestructive,
                _ => IsDestructive ? s.ToolsClassDestructive : s.ToolsClassUnknown,
            };
            DestructiveHelp = IsDestructive ? s.ToolsDestructiveHelp : "";
            CanRaise = !IsDestructive && _details.CanChangeMarks;
            RaiseOptions = IsDestructive
                ? Array.Empty<RaiseOptionViewModel>()
                : new[]
                {
                    new RaiseOptionViewModel(LocalToolRaise.None, s.ToolsRaiseNone),
                    new RaiseOptionViewModel(LocalToolRaise.Read, s.ToolsRaiseRead),
                    new RaiseOptionViewModel(LocalToolRaise.Write, s.ToolsRaiseWrite),
                };
            RaiseName = s.ToolsRaiseName(Name);
            _selectedRaise = RaiseOptions.FirstOrDefault(o => o.Raise == tool.Raise);
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(HasDescription));
            OnPropertyChanged(nameof(Hints));
            OnPropertyChanged(nameof(HasHints));
            OnPropertyChanged(nameof(Class));
            OnPropertyChanged(nameof(ClassText));
            OnPropertyChanged(nameof(IsDestructive));
            OnPropertyChanged(nameof(DestructiveHelp));
            OnPropertyChanged(nameof(CanRaise));
            OnPropertyChanged(nameof(RaiseOptions));
            OnPropertyChanged(nameof(SelectedRaise));
            OnPropertyChanged(nameof(RaiseName));
        }
        finally
        {
            _updating = false;
        }
    }

    internal bool Matches(string filter) =>
        ToolsViewModel.Contains(Name, filter) || ToolsViewModel.Contains(Description, filter);
}

/// <summary>One row of the drawer's facts: a label and what it says, in mono for a program or an address.</summary>
public sealed class FactRowViewModel
{
    internal FactRowViewModel(string label, string value, bool isMono = false, bool isMuted = false)
    {
        Label = label;
        Value = value;
        IsMono = isMono;
        IsMuted = isMuted;
    }

    public string Label { get; }

    public string Value { get; }

    public bool IsMono { get; }

    public bool IsMuted { get; }
}

/// <summary>
/// The details drawer of a local server: its header with the status and the switch, the facts (added, what it runs, the folders it
/// was told, its secrets by name, the longest call), the "Trusted" switch, the note that its tools are its own words, its tools with
/// a filter, the server's hints, their class and the raise picker, "Advanced" with the plain-http switch where it applies, and in
/// the footer Test, Restart, Edit and Remove, which asks first. Values of secrets are never here.
/// <para>
/// The plain-http switch follows the owner's rules: it is hidden where plain http is not permitted; turning it on first shows the
/// warning and changes nothing until the person confirms (<see cref="ConfirmPlainHttpCommand"/>), and the confirmation carries the
/// warning's version; turning it off needs no warning.
/// </para>
/// </summary>
public sealed partial class ServerDetailsViewModel : ObservableObject, IToolsTexts
{
    private readonly ToolsViewModel _page;
    private readonly Dictionary<string, LocalToolViewModel> _toolRows = new Dictionary<string, LocalToolViewModel>(StringComparer.Ordinal);
    private LocalServerViewModel _server;
    private bool _plainHttpOn;
    private bool _isTrusted;
    private bool _updating;

    internal ServerDetailsViewModel(ToolsViewModel page, LocalServerViewModel server)
    {
        _page = page;
        _server = server;
        Label = server.Label;
        CloseCommand = new RelayCommand(page.CloseDetails);
        EditCommand = new RelayCommand(() => page.Edit(Label));
        TestCommand = new AsyncRelayCommand(TestAsync);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => _server.IsOn);
        RemoveCommand = new RelayCommand(() => IsConfirmingRemove = true);
        ConfirmRemoveCommand = new AsyncRelayCommand(async () =>
        {
            await _page.Catalog.RemoveAsync(Label);
            _page.CloseDetails();
            _page.Refresh();
        });
        KeepCommand = new RelayCommand(() => IsConfirmingRemove = false);
        UseNewProgramCommand = new AsyncRelayCommand(UseNewProgramAsync);
        ConfirmPlainHttpCommand = new AsyncRelayCommand(ConfirmPlainHttpAsync, () => IsPlainHttpWarningOpen);
        CancelPlainHttpCommand = new RelayCommand(() => IsPlainHttpWarningOpen = false, () => IsPlainHttpWarningOpen);
        Refresh(server);
    }

    public UiStrings Strings => _page.Strings;

    public string Label { get; }

    /// <summary>The card, whose switch and status the drawer shares.</summary>
    public LocalServerViewModel Server => _server;

    public string DrawerName => Strings.ToolsDrawerName(Label);

    /// <summary>"Running since 17:03", else the card's status line.</summary>
    [ObservableProperty]
    private string _statusText = "";

    public IReadOnlyList<FactRowViewModel> Facts { get; private set; } = Array.Empty<FactRowViewModel>();

    public ObservableCollection<LocalToolViewModel> Tools { get; } = new ObservableCollection<LocalToolViewModel>();

    /// <summary>"Tools · 4".</summary>
    [ObservableProperty]
    private string _toolsTitle = "";

    [ObservableProperty]
    private string _toolFilter = "";

    /// <summary>The server lists no tools (or none matches the filter).</summary>
    [ObservableProperty]
    private string _noToolsText = "";

    [ObservableProperty]
    private bool _hasNoToolsText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTest))]
    private string _testText = "";

    [ObservableProperty]
    private bool _isBusy;

    public bool HasTest => TestText.Length > 0;

    [ObservableProperty]
    private bool _isConfirmingRemove;

    public string RemoveConfirmText => Strings.ToolsRemoveConfirm(Label);

    public string RemoveName => Strings.ToolsRemoveName(Label);

    // ---- trust ---------------------------------------------------------------------------------------------------------

    /// <summary>The person can change the trust mark and the raises (standalone); false: an administrator's marks count and are shown as they are.</summary>
    [ObservableProperty]
    private bool _canChangeMarks;

    /// <summary>The server's read-only hints count; a server's own word is never enough. Setting it tells the catalog.</summary>
    public bool IsTrusted
    {
        get => _isTrusted;
        set
        {
            if (value == _isTrusted)
            {
                return;
            }
            _isTrusted = value;
            OnPropertyChanged();
            if (!_updating && CanChangeMarks)
            {
                _page.Catalog.SetTrusted(Label, value);
                _page.Refresh();
            }
        }
    }

    /// <summary>"Only a trusted server's “reads only” hints count..." or "Marked by your administrator".</summary>
    public string TrustedNote => CanChangeMarks ? Strings.ToolsTrustedHelp : Strings.ToolsTrustedByAdmin;

    public string TrustedName => Strings.ToolsTrustedName(Label);

    // ---- a program that changed since it was added (sdk/51 5.5) ------------------------------------------------------------

    /// <summary>The server does not run because its program is not the one the person added: the drawer shows the reason and the two ways out.</summary>
    [ObservableProperty]
    private bool _hasProgramChange;

    /// <summary>"Use the new program": pins the program the command names now and starts the server with it. The other way out is Remove.</summary>
    public IAsyncRelayCommand UseNewProgramCommand { get; }

    public string ProgramChangedTitle => Strings.ToolsProgramChangedTitle;

    public string ProgramChangedText => Strings.ToolsProgramChangedHelp;

    public string UseNewProgramName => Strings.ToolsUseNewProgramName(Label);

    /// <summary>Why the new program could not be used; empty when nothing is wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgramError))]
    private string _programError = "";

    public bool HasProgramError => ProgramError.Length > 0;

    private async Task UseNewProgramAsync()
    {
        var problem = await _page.Catalog.ConfirmNewProgramAsync(Label);
        ProgramError = problem == ServerProblem.None ? "" : ServerSheetViewModel.ProblemText(Strings, problem);
        _page.Refresh();
    }

    // ---- Advanced: plain http on the local network -----------------------------------------------------------------------

    /// <summary>The switch applies to this server (plain http beyond this computer) and is permitted here; otherwise the whole Advanced part is hidden.</summary>
    [ObservableProperty]
    private bool _showsAdvanced;

    /// <summary>
    /// "Allow plain http on the local network" as it stands. Turning it on does not change it: it opens the warning, and the switch
    /// stays off until <see cref="ConfirmPlainHttpCommand"/>. Turning it off changes it at once.
    /// </summary>
    public bool PlainHttpOn
    {
        get => _plainHttpOn;
        set
        {
            if (value == _plainHttpOn || _updating)
            {
                return;
            }
            if (value)
            {
                IsPlainHttpWarningOpen = true;
                OnPropertyChanged(); // the switch the person pressed goes back to off until they confirm
            }
            else
            {
                _ = TurnPlainHttpOffAsync();
            }
        }
    }

    /// <summary>The warning is shown, and the person has not answered it.</summary>
    [ObservableProperty]
    private bool _isPlainHttpWarningOpen;

    partial void OnIsPlainHttpWarningOpenChanged(bool value)
    {
        ConfirmPlainHttpCommand.NotifyCanExecuteChanged();
        CancelPlainHttpCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlainHttpWarningText));
    }

    /// <summary>The warning (<see cref="UiStrings.McpPlainHttpWarning"/>) while it is open; empty otherwise.</summary>
    public string PlainHttpWarningText => IsPlainHttpWarningOpen ? Strings.McpPlainHttpWarning : "";

    /// <summary>Why the switch could not be changed; empty when nothing is wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlainHttpError))]
    private string _plainHttpError = "";

    public bool HasPlainHttpError => PlainHttpError.Length > 0;

    /// <summary>Turns the switch on; the person has read the warning and confirmed. Passes the warning's version.</summary>
    public IAsyncRelayCommand ConfirmPlainHttpCommand { get; }

    /// <summary>The person did not confirm: the switch stays off.</summary>
    public IRelayCommand CancelPlainHttpCommand { get; }

    public IRelayCommand CloseCommand { get; }

    public IRelayCommand EditCommand { get; }

    public IAsyncRelayCommand TestCommand { get; }

    public IAsyncRelayCommand RestartCommand { get; }

    public IRelayCommand RemoveCommand { get; }

    public IAsyncRelayCommand ConfirmRemoveCommand { get; }

    public IRelayCommand KeepCommand { get; }

    partial void OnToolFilterChanged(string value) => ApplyToolFilter();

    private async Task ConfirmPlainHttpAsync()
    {
        IsPlainHttpWarningOpen = false;
        var problem = await _page.Catalog.SetPlainHttpInLanAsync(Label, true, UiStrings.McpPlainHttpWarningVersion);
        PlainHttpError = problem == ServerProblem.None ? "" : ServerSheetViewModel.ProblemText(Strings, problem);
        _page.Refresh();
    }

    private async Task TurnPlainHttpOffAsync()
    {
        var problem = await _page.Catalog.SetPlainHttpInLanAsync(Label, false, null);
        PlainHttpError = problem == ServerProblem.None ? "" : ServerSheetViewModel.ProblemText(Strings, problem);
        _page.Refresh();
    }

    internal void SetRaise(string tool, LocalToolRaise raise)
    {
        _page.Catalog.SetToolClass(Label, tool, raise);
        _page.Refresh();
    }

    /// <summary>The app's language changed: the drawer says its facts again.</summary>
    internal void Retext()
    {
        OnPropertyChanged(nameof(Strings));
        Refresh(_server);
    }

    /// <summary>Reads the card's server again (the app reported a change).</summary>
    internal void Refresh(LocalServerViewModel server)
    {
        var s = Strings;
        _server = server;
        RestartCommand.NotifyCanExecuteChanged();
        var info = server.Info;
        _updating = true;
        try
        {
            StatusText = info.State == LocalServerState.Running && !info.ToolsChanged && info.RunningSince is { } since
                ? s.ToolsRunningSince(since.ToLocalTime().ToString("t", s.FormatCulture))
                : server.StatusText;

            CanChangeMarks = _page.Catalog.MarksAreYours;
            _isTrusted = info.Trusted;
            OnPropertyChanged(nameof(IsTrusted));
            OnPropertyChanged(nameof(TrustedNote));
            OnPropertyChanged(nameof(TrustedName));
            HasProgramChange = info.State == LocalServerState.ProgramChanged;
            if (!HasProgramChange)
            {
                ProgramError = "";
            }
            OnPropertyChanged(nameof(ProgramChangedTitle));
            OnPropertyChanged(nameof(ProgramChangedText));
            OnPropertyChanged(nameof(UseNewProgramName));
            ShowsAdvanced = info.PlainHttpApplies && _page.PlainHttpPermitted;
            _plainHttpOn = info.PlainHttpInLan;
            OnPropertyChanged(nameof(PlainHttpOn));
            if (!ShowsAdvanced)
            {
                IsPlainHttpWarningOpen = false;
            }

            var facts = new List<FactRowViewModel>
            {
                new FactRowViewModel(s.ToolsFactAdded, AddedText(s, info.Origin)),
                info.IsProgram ? new FactRowViewModel(s.ToolsFactRuns, info.Where, isMono: true) : new FactRowViewModel(s.ToolsFactAddress, info.Where, isMono: true),
            };
            if (info.IsProgram)
            {
                facts.Add(info.OnlyRunnerChecked ? new FactRowViewModel(s.ToolsFactProgramCheck, s.ToolsProgramOnlyRunner)
                    : info.ProgramPinned ? new FactRowViewModel(s.ToolsFactProgramCheck, s.ToolsProgramChecked)
                    : new FactRowViewModel(s.ToolsFactProgramCheck, s.ToolsProgramNotPinned, isMuted: true));
                var roots = _page.Catalog.RootNames;
                facts.Add(roots.Count == 0 ? new FactRowViewModel(s.ToolsFactFolders, s.ToolsNoFoldersTold, isMuted: true) : new FactRowViewModel(s.ToolsFactFolders, string.Join(", ", roots)));
            }
            facts.Add(info.SecretNames.Count == 0 ? new FactRowViewModel(s.ToolsFactSecrets, s.ToolsNoneText, isMuted: true) : new FactRowViewModel(s.ToolsFactSecrets, string.Join(", ", info.SecretNames), isMono: true));
            facts.Add(new FactRowViewModel(s.ToolsFactLongestCall, Duration(s, info.CallTimeoutSeconds)));
            Facts = facts;
            OnPropertyChanged(nameof(Facts));
            OnPropertyChanged(nameof(DrawerName));
            OnPropertyChanged(nameof(RemoveConfirmText));
            OnPropertyChanged(nameof(RemoveName));
            OnPropertyChanged(nameof(PlainHttpWarningText));

            // A tool that stays keeps its row (and its picker); the rows follow the server's own order.
            var listed = new List<LocalToolViewModel>();
            foreach (var tool in info.Tools)
            {
                if (_toolRows.TryGetValue(tool.Name, out var row))
                {
                    row.Update(tool);
                }
                else
                {
                    row = new LocalToolViewModel(this, tool);
                    _toolRows[tool.Name] = row;
                }
                listed.Add(row);
            }
            foreach (var gone in _toolRows.Keys.Where(k => info.Tools.All(t => t.Name != k)).ToList())
            {
                _toolRows.Remove(gone);
            }
            _allTools = listed;
            ToolsTitle = s.ToolsListHeading(_allTools.Count);
            ApplyToolFilter();
        }
        finally
        {
            _updating = false;
        }
    }

    private IReadOnlyList<LocalToolViewModel> _allTools = Array.Empty<LocalToolViewModel>();

    private void ApplyToolFilter()
    {
        var s = Strings;
        var shown = _allTools.Where(t => t.Matches(ToolFilter)).ToList();
        for (var i = Tools.Count - 1; i >= 0; i--)
        {
            if (!shown.Contains(Tools[i]))
            {
                Tools.RemoveAt(i);
            }
        }
        foreach (var tool in shown.Where(t => !Tools.Contains(t)))
        {
            Tools.Add(tool);
        }
        NoToolsText = _allTools.Count == 0 ? s.ToolsNoTools : shown.Count == 0 ? s.ToolsNoToolsMatch : "";
        HasNoToolsText = NoToolsText.Length > 0;
    }

    // "By you", "From Claude Desktop" or "Found by a scan": who added it.
    private static string AddedText(UiStrings s, string origin) => origin switch
    {
        "" or "typed" => s.ToolsAddedByYou,
        "scan" => s.ToolsAddedByScan,
        _ => s.ToolsAddedFrom(origin),
    };

    /// <summary>"90 seconds", "2 minutes".</summary>
    internal static string Duration(UiStrings s, int seconds)
    {
        if (seconds >= 60 && seconds % 60 == 0)
        {
            var minutes = seconds / 60;
            return minutes == 1 ? s.ToolsMinutesOne : s.ToolsMinutesMany(minutes);
        }
        return seconds == 1 ? s.ToolsSecondsOne : s.ToolsSecondsMany(seconds);
    }

    private async Task TestAsync()
    {
        if (_page.Catalog.GetDraft(Label) is not { } draft)
        {
            return;
        }
        IsBusy = true;
        TestText = Strings.ToolsTestRunning;
        try
        {
            TestText = ServerSheetViewModel.TestLine(Strings, await _page.Catalog.TestAsync(draft, Label));
        }
        finally
        {
            draft.ClearSecrets();
            IsBusy = false;
        }
    }

    private async Task RestartAsync()
    {
        IsBusy = true;
        try
        {
            await _page.Catalog.RestartAsync(Label);
            TestText = Strings.ToolsRestartedLine;
        }
        finally
        {
            IsBusy = false;
        }
        _page.Refresh();
    }
}
