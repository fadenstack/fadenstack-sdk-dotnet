using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.LocalTools;

namespace Fadenstack.UI.Tools;

/// <summary>A tool of a local server, as a mono chip on its card; a tool the server says may delete is warning-tinted.</summary>
public sealed class ToolChipViewModel
{
    internal ToolChipViewModel(UiStrings strings, string name, bool mayDelete, bool isMore = false)
    {
        Name = name;
        MayDelete = mayDelete;
        IsMore = isMore;
        Tip = mayDelete ? strings.ToolsToolMayDelete(name) : name;
    }

    /// <summary>The "+1" after the chips: the tools beyond the three shown (plain text, not a chip).</summary>
    public bool IsMore { get; }

    public bool IsTool => !IsMore;

    public string Name { get; }

    /// <summary>The server says the tool may delete or overwrite: the chip is warning-tinted.</summary>
    public bool MayDelete { get; }

    public string Tip { get; }
}

/// <summary>What a card's status line says, as the dot's and the text's colour.</summary>
public enum ServerStatusKind
{
    /// <summary>Off, or starting: neutral.</summary>
    Neutral,

    /// <summary>Running.</summary>
    Good,

    /// <summary>Not allowed, refused, or the server changed its tools.</summary>
    Warning,

    /// <summary>It cannot start.</summary>
    Problem,
}

/// <summary>
/// One of the person's local servers as a card on the page: an icon tile (terminal for a program, plug for an address), the label,
/// where it came from and what it runs, the On switch, a status line with its dot, up to three tool chips and "+n", and for a server
/// that cannot start for want of a value the fix as an inline action. The whole card opens the details drawer. A server that plain
/// http was refused for says why in its status, in its own words for each reason.
/// </summary>
public sealed partial class LocalServerViewModel : ObservableObject, IToolsTexts
{
    private readonly ToolsViewModel _page;
    private bool _updating;

    internal LocalServerViewModel(ToolsViewModel page, LocalServerInfo info, int rootCount)
    {
        _page = page;
        Label = info.Label;
        OpenCommand = new RelayCommand(() => _page.OpenDetails(this));
        FixCommand = new RelayCommand(() => _page.Edit(Label));
        Update(info, rootCount);
    }

    public UiStrings Strings => _page.Strings;

    public string Label { get; }

    /// <summary>What the app knows of the server now; the drawer reads its facts and tools from it.</summary>
    internal LocalServerInfo Info { get; private set; } = null!;

    internal int RootCount { get; private set; }

    /// <summary>A program (a terminal tile); otherwise an address (a plug).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddress))]
    private bool _isProgram;

    public bool IsAddress => !IsProgram;

    /// <summary>"Added by you · faden-mcp-demo.exe": where it came from, then what it runs or its address.</summary>
    [ObservableProperty]
    private string _originText = "";

    [ObservableProperty]
    private string _where = "";

    /// <summary>"Running · 4 tools · told 1 folder", "Off", "Can't start: ...", or why plain http was refused: beside the dot.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>The reason in full when the line is shortened (not allowed).</summary>
    [ObservableProperty]
    private string _statusTip = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIsGood))]
    [NotifyPropertyChangedFor(nameof(StatusIsWarning))]
    [NotifyPropertyChangedFor(nameof(StatusIsProblem))]
    [NotifyPropertyChangedFor(nameof(StatusIsNeutral))]
    private ServerStatusKind _statusKind;

    public bool StatusIsGood => StatusKind == ServerStatusKind.Good;

    public bool StatusIsWarning => StatusKind == ServerStatusKind.Warning;

    public bool StatusIsProblem => StatusKind == ServerStatusKind.Problem;

    public bool StatusIsNeutral => StatusKind == ServerStatusKind.Neutral;

    /// <summary>Why a connection over plain http was refused; null when it was not (the card's status says it in words).</summary>
    public LocalPlainHttpRefusal? PlainHttpRefusal { get; private set; }

    [ObservableProperty]
    private bool _isOn;

    /// <summary>Its drawer is open: the card's border is the accent.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The listed tools, in all (the bar's count).</summary>
    public int ToolTotal { get; private set; }

    public IReadOnlyList<ToolChipViewModel> Chips { get; private set; } = Array.Empty<ToolChipViewModel>();

    public bool HasChips => Chips.Count > 0;

    /// <summary>"+1": the tools beyond the three chips.</summary>
    [ObservableProperty]
    private string _moreText = "";

    [ObservableProperty]
    private bool _hasMore;

    /// <summary>A failed card's fix, such as "Add the value".</summary>
    [ObservableProperty]
    private string _fixText = "";

    [ObservableProperty]
    private bool _hasFix;

    public string SwitchName => Strings.ToolsSwitchName(Label);

    public string OpenName => Strings.ToolsOpenDetails(Label);

    public IRelayCommand OpenCommand { get; }

    public IRelayCommand FixCommand { get; }

    // The person moved the switch: a change from the page itself (Update) is not sent back.
    partial void OnIsOnChanged(bool value)
    {
        if (!_updating)
        {
            _ = _page.SwitchAsync(Label, value);
        }
    }

    internal bool Matches(string filter) =>
        ToolsViewModel.Contains(Label, filter) || (Info?.Tools.Any(t => ToolsViewModel.Contains(t.Name, filter)) ?? false);

    internal void Update(LocalServerInfo info, int rootCount)
    {
        var s = Strings;
        _updating = true;
        try
        {
            Info = info;
            RootCount = rootCount;
            IsProgram = info.IsProgram;
            Where = info.Where;
            OriginText = s.ToolsOriginLine(OriginName(s, info.Origin), info.Where);
            IsOn = info.Enabled;
            ToolTotal = info.Tools.Count;

            var tools = ToolCount(s, info.Tools.Count);
            var told = rootCount == 0 ? "" : rootCount == 1 ? s.ToolsToldFoldersOne : s.ToolsToldFoldersMany(rootCount);
            StatusTip = "";
            PlainHttpRefusal = Refusal(info);
            if (PlainHttpRefusal is { } refusal)
            {
                (StatusText, StatusKind) = (RefusalText(s, refusal), ServerStatusKind.Warning);
            }
            else
            {
                switch (info.State)
                {
                    case LocalServerState.Running when info.ToolsChanged:
                        (StatusText, StatusKind) = (s.ToolsStateChangedTools, ServerStatusKind.Warning);
                        break;
                    case LocalServerState.Running:
                        (StatusText, StatusKind) = (Join(s.ToolsStateRunning, tools, told), ServerStatusKind.Good);
                        break;
                    case LocalServerState.Starting:
                        (StatusText, StatusKind) = (s.ToolsStateStarting, ServerStatusKind.Neutral);
                        break;
                    case LocalServerState.Failed when info.MissingValues.Count > 0:
                        (StatusText, StatusKind) = (s.ToolsStateMissingValue(info.MissingValues[0]), ServerStatusKind.Problem);
                        break;
                    case LocalServerState.Failed:
                        (StatusText, StatusKind) = (s.ToolsStateFailed(string.IsNullOrWhiteSpace(info.Detail) ? s.ToolsReasonUnknown : info.Detail!), ServerStatusKind.Problem);
                        break;
                    case LocalServerState.NotAllowed:
                        (StatusText, StatusKind) = (s.ToolsStateNotAllowed, ServerStatusKind.Warning);
                        StatusTip = s.ToolsStateNotAllowedBecause(s.ToolsReasonPolicy);
                        break;
                    default:
                        (StatusText, StatusKind) = (info.Tools.Count > 0 ? Join(s.ToolsStateOff, tools) : s.ToolsStateOff, ServerStatusKind.Neutral);
                        break;
                }
            }

            HasMore = info.Tools.Count > 3;
            MoreText = HasMore ? s.ToolsMoreTools(info.Tools.Count - 3) : "";
            var chips = info.Tools.Take(3).Select(t => new ToolChipViewModel(s, t.Name, t.Destructive == true || t.Class == LocalToolClass.Destructive)).ToList();
            if (HasMore)
            {
                chips.Add(new ToolChipViewModel(s, MoreText, false, isMore: true));
            }
            Chips = chips;
            OnPropertyChanged(nameof(Chips));
            OnPropertyChanged(nameof(HasChips));

            // A server that cannot start for want of a value offers the fix on its card; the person fills it in on the edit sheet.
            HasFix = info.State == LocalServerState.Failed && info.MissingValues.Count > 0 && PlainHttpRefusal is null;
            FixText = HasFix ? s.ToolsFixAddValue : "";
            OnPropertyChanged(nameof(Strings));
            OnPropertyChanged(nameof(SwitchName));
            OnPropertyChanged(nameof(OpenName));
        }
        finally
        {
            _updating = false;
        }
    }

    // The module's own reason, else (the switch is on, but this agent or computer does not allow plain http, and the module has
    // not tried yet) "not allowed": a server already on says so in its status.
    private LocalPlainHttpRefusal? Refusal(LocalServerInfo info)
    {
        if (info.State == LocalServerState.Off)
        {
            return null;
        }
        if (info.PlainHttpRefusal is { } given)
        {
            return given;
        }
        return info.PlainHttpApplies && info.PlainHttpInLan && !_page.PlainHttpPermitted && info.State != LocalServerState.Running
            ? LocalPlainHttpRefusal.NotAllowed
            : null;
    }

    /// <summary>Each refusal in its own words.</summary>
    internal static string RefusalText(UiStrings s, LocalPlainHttpRefusal refusal) => refusal switch
    {
        LocalPlainHttpRefusal.NoOptIn => s.McpPlainHttpNoOptIn,
        LocalPlainHttpRefusal.NotLocal => s.McpPlainHttpNotLocal,
        LocalPlainHttpRefusal.Secret => s.McpPlainHttpSecret,
        _ => s.McpPlainHttpNotAllowed,
    };

    internal static string ToolCount(UiStrings s, int count) => count == 1 ? s.ToolsCountOne : s.ToolsCountMany(count);

    /// <summary>"Added by you", "From Claude Desktop" or "Found by a scan": where the server came from.</summary>
    internal static string OriginName(UiStrings s, string origin) => origin switch
    {
        "" or "typed" => s.ToolsOriginTyped,
        "scan" => s.ToolsOriginScan,
        _ => s.ToolsOriginImported(origin),
    };

    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));
}
