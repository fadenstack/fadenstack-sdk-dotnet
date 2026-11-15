using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.LocalTools;

namespace Fadenstack.UI.Tools;

/// <summary>One app the scan looks in, as a chip: "Claude Desktop" before a scan, "Claude Desktop · 2" after it.</summary>
public sealed class ScanChipViewModel
{
    internal ScanChipViewModel(string text) => Text = text;

    public string Text { get; }
}

/// <summary>A line under a finding: what it takes from the other app, how it answered, or why it needs a value.</summary>
public sealed class ScanNoteViewModel
{
    internal ScanNoteViewModel(string text, bool isSecret = false, bool isWarning = false)
    {
        Text = text;
        IsSecret = isSecret;
        IsWarning = isWarning;
    }

    public string Text { get; }

    /// <summary>It names secrets (a lock icon): their names only, never a value.</summary>
    public bool IsSecret { get; }

    public bool IsWarning { get; }
}

/// <summary>
/// One server a scan found, with a tick box. New: ticked, to add. Already added: ticked and disabled. On another computer: disabled,
/// with the reason (a scan does not add it). Needs a value: ticked, added switched off. Not understood: disabled, with its reason.
/// Its secrets show by name only.
/// </summary>
public sealed partial class ScanRowViewModel : ObservableObject
{
    private readonly Action _changed;

    internal ScanRowViewModel(UiStrings s, ScanFindingInfo finding, Action changed)
    {
        _changed = changed;
        Id = finding.Id;
        Label = finding.Label;
        Where = finding.Where;
        State = finding.State;
        CanTick = finding.State is ScanFindingState.New or ScanFindingState.NeedsValue;
        _isTicked = finding.State is ScanFindingState.New or ScanFindingState.NeedsValue or ScanFindingState.AlreadyAdded;
        IsDimmed = finding.State == ScanFindingState.AlreadyAdded;
        IsMutedName = finding.State is ScanFindingState.NetworkLater or ScanFindingState.NotUnderstood;
        KindText = finding.IsProgram ? s.ToolsScanKindProgram : s.ToolsScanKindAddress;
        (SideText, SideIsWarning) = finding.State switch
        {
            ScanFindingState.AlreadyAdded => (s.ToolsScanStateAlready, false),
            ScanFindingState.NetworkLater => (s.ToolsScanStateLater, true),
            ScanFindingState.NotUnderstood => (s.ToolsScanStateNotUnderstood, true),
            _ => (KindText, false),
        };
        TickName = finding.State switch
        {
            ScanFindingState.AlreadyAdded => s.ToolsScanTickAlready(finding.Label),
            ScanFindingState.NetworkLater or ScanFindingState.NotUnderstood => s.ToolsScanTickLater(finding.Label),
            _ => s.ToolsScanTick(finding.Label),
        };

        var notes = new List<ScanNoteViewModel>();
        if (finding.State == ScanFindingState.NotUnderstood && !string.IsNullOrWhiteSpace(finding.Detail))
        {
            notes.Add(new ScanNoteViewModel(finding.Detail!));
        }
        if (finding.FromApp && finding.SecretNames.Count > 0 && finding.State != ScanFindingState.NotUnderstood)
        {
            notes.Add(new ScanNoteViewModel(s.ToolsScanTakes(string.Join(", ", finding.SecretNames), finding.FoundIn), isSecret: true));
        }
        if (!finding.FromApp && !string.IsNullOrWhiteSpace(finding.ServerName))
        {
            notes.Add(new ScanNoteViewModel(finding.ToolCount is { } n
                ? s.ToolsScanAnsweredTools(finding.ServerName!, LocalServerViewModel.ToolCount(s, n))
                : s.ToolsScanAnswered(finding.ServerName!)));
        }
        if (finding.State == ScanFindingState.NeedsValue)
        {
            notes.Add(new ScanNoteViewModel(s.ToolsScanStateNeedsValue, isWarning: true));
        }
        Notes = notes;
    }

    public string Id { get; }

    public string Label { get; }

    /// <summary>The program with its arguments, or the address, in mono.</summary>
    public string Where { get; }

    public ScanFindingState State { get; }

    /// <summary>The person can tick it (a new server, or one that needs a value).</summary>
    public bool CanTick { get; }

    [ObservableProperty]
    private bool _isTicked;

    /// <summary>Already added: the row is dimmed (ticked, disabled).</summary>
    public bool IsDimmed { get; }

    /// <summary>The name is muted (it cannot be added).</summary>
    public bool IsMutedName { get; }

    public string KindText { get; }

    /// <summary>At the right: "Program", "Address", "Already added", "On another computer: add it yourself", "Not understood".</summary>
    public string SideText { get; }

    public bool SideIsWarning { get; }

    public string TickName { get; }

    public IReadOnlyList<ScanNoteViewModel> Notes { get; }

    public bool HasNotes => Notes.Count > 0;

    /// <summary>Counts towards "Add n servers": ticked and addable.</summary>
    public bool IsChosen => CanTick && IsTicked;

    partial void OnIsTickedChanged(bool value) => _changed();
}

/// <summary>The findings of one place, under a title such as "Found in Claude Desktop".</summary>
public sealed class ScanGroupViewModel
{
    internal ScanGroupViewModel(string title, IReadOnlyList<ScanRowViewModel> rows)
    {
        Title = title;
        Rows = rows;
    }

    public string Title { get; }

    public IReadOnlyList<ScanRowViewModel> Rows { get; }
}

/// <summary>
/// The scan sheet: a modal sheet that opens on the person's click and looks only where they tick: the other apps' MCP settings and
/// this computer's loopback ports. It shows where it looked (the apps with how many servers each had, and "not installed"), the
/// progress, then the findings grouped by where they were found, each with a tick box in one of five states, the secrets by name
/// only, a footer that says nothing leaves this computer, and "Add n servers". Cancel stops a running scan and closes the sheet. A
/// scan never starts by itself, and what it adds is never trusted and never has the plain-http switch on.
/// </summary>
public sealed partial class ScanSheetViewModel : ObservableObject, IToolsTexts, IRetext, IDisposable
{
    private readonly ToolsViewModel _page;
    private readonly List<ScanRowViewModel> _rows = new List<ScanRowViewModel>();
    private CancellationTokenSource? _cts;
    private ScanResultInfo? _result;
    private bool _disposed;

    internal ScanSheetViewModel(ToolsViewModel page)
    {
        _page = page;
        var s = page.Strings;
        var ranges = page.Catalog.ScanPortRanges.Select(r => s.ToolsScanPortRange(r.From, r.To)).ToList();
        var joined = ranges.Count <= 1 ? string.Concat(ranges)
            : string.Join(", ", ranges.Take(ranges.Count - 1)) + " " + s.ToolsScanAnd + " " + ranges.Last();
        PortsText = s.ToolsScanPorts(joined);
        _apps = page.Catalog.ScanAppNames.Select(n => new ScanChipViewModel(n)).ToList();
        StartCommand = new AsyncRelayCommand(StartAsync);
        CancelCommand = new RelayCommand(Close);
        AddCommand = new AsyncRelayCommand(AddAsync, () => ChosenCount > 0 && !IsScanning);
    }

    public UiStrings Strings => _page.Strings;

    /// <summary>Look in the other apps' MCP settings.</summary>
    [ObservableProperty]
    private bool _otherApps = true;

    /// <summary>Look on this computer's loopback ports.</summary>
    [ObservableProperty]
    private bool _thisComputer = true;

    /// <summary>"localhost, ports 3000–3999 and 8000–8999. It only says hello in MCP; nothing is sent."</summary>
    public string PortsText { get; }

    /// <summary>The apps that were read, with the counts, after a scan; their names before.</summary>
    [ObservableProperty]
    private IReadOnlyList<ScanChipViewModel> _apps;

    /// <summary>"Windsurf, Gemini CLI, LM Studio: not installed".</summary>
    [ObservableProperty]
    private string _notInstalledText = "";

    [ObservableProperty]
    private bool _hasNotInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsStatus))]
    [NotifyPropertyChangedFor(nameof(CanScanAgain))]
    private bool _isScanning;

    public bool CanScanAgain => !IsScanning;

    /// <summary>"Looking… 3 of 10 done" while it runs.</summary>
    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private double _progressDone;

    [ObservableProperty]
    private double _progressTotal = 1;

    /// <summary>"Done in 4 s · 5 servers found" after; or why it stopped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsStatus))]
    private string _statusText = "";

    /// <summary>The scan finished: the status is the good line with Scan again beside it.</summary>
    [ObservableProperty]
    private bool _isDone;

    /// <summary>The scan did not finish (stopped, failed, nothing ticked): a plain line.</summary>
    [ObservableProperty]
    private bool _isProblem;

    public bool ShowsStatus => StatusText.Length > 0 || IsScanning;

    /// <summary>"No MCP servers were found. You can add one yourself."</summary>
    [ObservableProperty]
    private string _emptyText = "";

    [ObservableProperty]
    private bool _hasEmptyText;

    public ObservableCollection<ScanGroupViewModel> Groups { get; } = new ObservableCollection<ScanGroupViewModel>();

    [ObservableProperty]
    private bool _hasFindings;

    /// <summary>How many servers "Add" would add.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddText))]
    private int _chosenCount;

    /// <summary>"Add 3 servers", "Add 1 server".</summary>
    public string AddText => ChosenCount == 1 ? Strings.ToolsScanAddOne : Strings.ToolsScanAddMany(Math.Max(ChosenCount, 0));

    public IAsyncRelayCommand StartCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand AddCommand { get; }

    partial void OnIsScanningChanged(bool value) => AddCommand.NotifyCanExecuteChanged();

    partial void OnChosenCountChanged(int value) => AddCommand.NotifyCanExecuteChanged();

    /// <summary>Scans (the first time when the sheet opens on the person's click; again on "Scan again").</summary>
    public async Task StartAsync()
    {
        if (_disposed)
        {
            return;
        }
        var s = Strings;
        if (!OtherApps && !ThisComputer)
        {
            Clear();
            (StatusText, IsProblem, IsDone) = (s.ToolsScanNothingToLookAt, true, false);
            return;
        }
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        Clear();
        IsScanning = true;
        StatusText = "";
        ProgressDone = 0;
        ProgressTotal = 1;
        ProgressText = s.ToolsScanStarting;
        try
        {
            var result = await _page.Catalog.ScanAsync(new ScanOptionsInfo { OtherApps = OtherApps, ThisComputer = ThisComputer },
                (done, total) => _page.Post(() =>
                {
                    if (_disposed || cts.IsCancellationRequested)
                    {
                        return;
                    }
                    ProgressTotal = Math.Max(total, 1);
                    ProgressDone = Math.Min(done, total);
                    ProgressText = Strings.ToolsScanRunning((int)ProgressDone, (int)ProgressTotal);
                }), cts.Token);
            if (_disposed || cts.IsCancellationRequested)
            {
                return;
            }
            Show(result);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                (StatusText, IsProblem, IsDone) = (Strings.ToolsScanStopped, true, false);
            }
        }
        catch (Exception)
        {
            // What failed is not shown: a scan's error text may name a path or an address, and the line says what the person can do.
            if (!_disposed)
            {
                (StatusText, IsProblem, IsDone) = (Strings.ToolsScanFailed, true, false);
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsScanning = false;
            }
        }
    }

    private void Clear()
    {
        _result = null;
        _rows.Clear();
        Groups.Clear();
        HasFindings = false;
        ChosenCount = 0;
        EmptyText = "";
        HasEmptyText = false;
        StatusText = "";
        IsDone = false;
        IsProblem = false;
    }

    private void Show(ScanResultInfo result)
    {
        var s = Strings;
        _result = result;
        Apps = result.Apps.Where(a => a.Installed).Select(a => new ScanChipViewModel(s.ToolsScanChip(a.Name, a.Found))).ToList();
        var missing = result.Apps.Where(a => !a.Installed).Select(a => a.Name).ToList();
        NotInstalledText = missing.Count == 0 ? "" : s.ToolsScanNotInstalled(string.Join(", ", missing));
        HasNotInstalled = NotInstalledText.Length > 0;

        foreach (var group in result.Findings.GroupBy(f => new { f.FromApp, f.FoundIn }))
        {
            var rows = group.Select(f => new ScanRowViewModel(s, f, Recount)).ToList();
            _rows.AddRange(rows);
            Groups.Add(new ScanGroupViewModel(group.Key.FromApp ? s.ToolsScanFoundIn(group.Key.FoundIn) : s.ToolsScanRunningHere, rows));
        }
        HasFindings = _rows.Count > 0;
        var found = result.Findings.Count == 0 ? s.ToolsScanFoundNone
            : result.Findings.Count == 1 ? s.ToolsScanFoundOne
            : s.ToolsScanFoundMany(result.Findings.Count);
        StatusText = s.ToolsScanDone((int)Math.Round(result.Seconds, MidpointRounding.AwayFromZero), found);
        IsDone = true;
        IsProblem = false;
        EmptyText = HasFindings ? "" : s.ToolsScanNothingFoundHelp;
        HasEmptyText = EmptyText.Length > 0;
        Recount();
    }

    private void Recount() => ChosenCount = _rows.Count(r => r.IsChosen);

    // Cancel: a running scan stops at once, then the sheet closes.
    private void Close()
    {
        _cts?.Cancel();
        _page.CloseSheet();
    }

    private async Task AddAsync()
    {
        if (_result is not { } result)
        {
            return;
        }
        var ids = _rows.Where(r => r.IsChosen).Select(r => r.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }
        await _page.Catalog.AddFoundAsync(result, ids);
        _page.Refresh();
        _page.CloseSheet();
    }

    public void Retext() => OnPropertyChanged(nameof(Strings));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
