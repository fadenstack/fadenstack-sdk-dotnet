using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.LocalTools;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.Tools;

/// <summary>The three places of the Tools view, in the order of its tabs.</summary>
public enum ToolsTab
{
    /// <summary>"In this app": the tools the app itself gives the assistant (read only).</summary>
    App,

    /// <summary>"On this computer": the person's MCP servers.</summary>
    Computer,

    /// <summary>"On your server": the organisation's tools (read only); only while the connection is governed.</summary>
    Server,
}

/// <summary>
/// The Tools view (sdk/51 F05; the desktop app's Tools page without its built-in tools and assistants): a header with Scan and Add, a
/// bar with the places tools come from (in this app, on this computer, on your server), the policy chip and a filter. <i>In this app</i>
/// lists the tools the app itself gives the assistant, read only. <i>On this computer</i> shows
/// the person's MCP servers as cards (a details drawer opens on a card, with the trust mark, each tool's class and the plain-http
/// switch); the empty state replaces the cards while there are none. <i>On your server</i> shows the organisation's tools by server,
/// read only, and exists only while the agent is connected to an organisation's server (not standalone, not unknown). Adding and editing a server, the import and the scan are modal sheets. Everything comes from the app's
/// <see cref="ILocalToolsCatalog"/>; every change goes back through it. Strict MVVM: no view type, and the catalog's events (from any
/// thread) are brought to the UI thread through the <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class ToolsViewModel : ObservableObject, IToolsTexts, IDisposable
{
    private readonly ILocalToolsCatalog _catalog;
    private readonly IUiDispatcher _dispatcher;
    private readonly IToolsFileHost? _files;
    private readonly Func<UiStrings> _strings;
    private readonly List<LocalServerViewModel> _allServers = new List<LocalServerViewModel>();
    private CloudToolsResult? _cloud;
    private bool _refreshPending;
    private bool _cloudLoaded;
    private readonly List<AppToolViewModel> _allApp = new List<AppToolViewModel>();
    private bool _disposed;

    /// <param name="catalog">The servers and the cloud tools (<c>Fadenstack.Mcp.McpToolsCatalog</c>).</param>
    /// <param name="files">Dialogs for the working folder and the import file; without it those buttons do nothing.</param>
    /// <param name="dispatcher">The UI thread; inline by default (tests, headless hosts).</param>
    /// <param name="strings">The texts; the current UI language's by default.</param>
    public ToolsViewModel(ILocalToolsCatalog catalog, IToolsFileHost? files = null, IUiDispatcher? dispatcher = null, Func<UiStrings>? strings = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _files = files;
        _dispatcher = dispatcher ?? InlineDispatcher.Instance;
        var current = UiStrings.Current;
        _strings = strings ?? (() => current);
        AddServerCommand = new RelayCommand(AddServer);
        ScanCommand = new RelayCommand(Scan, () => _catalog.CanScan);
        ImportServersCommand = new RelayCommand(ImportServers);
        CloseSheetCommand = new RelayCommand(CloseSheet);
        CloseDetailsCommand = new RelayCommand(CloseDetails);
        CloseTopCommand = new RelayCommand(CloseTop);
        RetryCommand = new AsyncRelayCommand(() => LoadCloudAsync());
        _catalog.Changed += OnCatalogChanged;
        Refresh();
    }

    /// <summary>The texts; a skin binds them and follows this when the language changes.</summary>
    public UiStrings Strings => _strings();

    internal ILocalToolsCatalog Catalog => _catalog;

    internal IToolsFileHost? Files => _files;

    // ---- sign-in, policy, tabs, filter -------------------------------------------------------------------------------

    /// <summary>Nobody is signed in or the agent has not started: a short line replaces the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLists))]
    private bool _needsSignIn;

    public bool ShowsLists => !NeedsSignIn;

    /// <summary>The policy: the chip beside the tabs; the banner shows only while local servers are not allowed.</summary>
    [ObservableProperty]
    private string _policyText = "";

    /// <summary>The chip's tooltip: the full sentence.</summary>
    [ObservableProperty]
    private string _policyTip = "";

    /// <summary>The policy is a warning (developer mode, not allowed); otherwise a success.</summary>
    [ObservableProperty]
    private bool _policyIsWarning;

    [ObservableProperty]
    private bool _hasPolicy;

    /// <summary>The policy does not allow local servers: a full banner in the flow.</summary>
    [ObservableProperty]
    private bool _showsBanner;

    [ObservableProperty]
    private string _bannerText = "";

    /// <summary>Plain http on the local network is permitted here (the profile and this computer's policy): the switch is offered.</summary>
    [ObservableProperty]
    private bool _plainHttpPermitted;

    /// <summary>The tab that is open; "On this computer" until the person picks another.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsApp))]
    [NotifyPropertyChangedFor(nameof(ShowsLocal))]
    [NotifyPropertyChangedFor(nameof(ShowsServer))]
    [NotifyPropertyChangedFor(nameof(ShowsLocalChip))]
    [NotifyPropertyChangedFor(nameof(ShowsManagedChip))]
    [NotifyPropertyChangedFor(nameof(ShowsScanButton))]
    [NotifyPropertyChangedFor(nameof(ShowsAddButton))]
    private ToolsTab _tab = ToolsTab.Computer;

    /// <summary>The tab "In this app" is open. Setting it true opens it (false changes nothing: another tab was picked).</summary>
    public bool ShowsApp
    {
        get => Tab == ToolsTab.App;
        set => Select(ToolsTab.App, value);
    }

    /// <summary>The tab "On this computer" is open. Setting it true opens it (false changes nothing: another tab was picked).</summary>
    public bool ShowsLocal
    {
        get => Tab == ToolsTab.Computer;
        set => Select(ToolsTab.Computer, value);
    }

    /// <summary>The tab "On your server" is open. Setting it true opens it where there is such a tab.</summary>
    public bool ShowsServer
    {
        get => Tab == ToolsTab.Server;
        set => Select(ToolsTab.Server, value);
    }

    // A radio button that is unchecked reports false after the one that was picked reported true: only a pick counts.
    private void Select(ToolsTab tab, bool picked)
    {
        if (picked && (tab != ToolsTab.Server || ShowsServerTab))
        {
            Tab = tab;
        }
        else
        {
            // The bound radio button follows the view model again (it may have flipped itself).
            OnPropertyChanged(tab switch { ToolsTab.App => nameof(ShowsApp), ToolsTab.Computer => nameof(ShowsLocal), _ => nameof(ShowsServer) });
        }
    }

    /// <summary>The tab "On your server" exists: the agent is connected to an organisation's server and somebody is signed in.</summary>
    [ObservableProperty]
    private bool _showsServerTab;

    /// <summary>The policy chip belongs to the computer's tab; the server's tab has its own.</summary>
    public bool ShowsLocalChip => ShowsLocal && HasPolicy;

    public bool ShowsManagedChip => ShowsServer;

    [ObservableProperty]
    private string _appTabText = "";

    [ObservableProperty]
    private string _localTabText = "";

    [ObservableProperty]
    private string _serverTabText = "";

    /// <summary>Narrows every section by a tool's or a server's name.</summary>
    [ObservableProperty]
    private string _filter = "";

    partial void OnFilterChanged(string value)
    {
        Rebuild();
        BuildApp();
        BuildCloud();
    }

    partial void OnHasPolicyChanged(bool value) => OnPropertyChanged(nameof(ShowsLocalChip));

    // ---- header actions -----------------------------------------------------------------------------------------------

    /// <summary>The app has a scanner: otherwise the Scan buttons are hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanIsPrimary))]
    [NotifyPropertyChangedFor(nameof(AddIsPrimary))]
    [NotifyPropertyChangedFor(nameof(ShowsScanButton))]
    private bool _canScan;

    /// <summary>The header's Scan this computer: the app has a scanner and the computer's tab is open.</summary>
    public bool ShowsScanButton => CanScan && ShowsLocal;

    /// <summary>The header's Add server: on the computer's tab only (the server's tab is read only).</summary>
    public bool ShowsAddButton => ShowsLocal;

    /// <summary>In the empty state Scan is the main action; without a scanner it is Add.</summary>
    public bool ScanIsPrimary => CanScan;

    public bool AddIsPrimary => !CanScan;

    public IRelayCommand AddServerCommand { get; }

    public IRelayCommand ScanCommand { get; }

    public IRelayCommand ImportServersCommand { get; }

    public IRelayCommand CloseSheetCommand { get; }

    public IRelayCommand CloseDetailsCommand { get; }

    /// <summary>Esc: the sheet over the page first, then the drawer.</summary>
    public IRelayCommand CloseTopCommand { get; }

    /// <summary>Loads the cloud tools again ("Try again").</summary>
    public IAsyncRelayCommand RetryCommand { get; }

    // ---- on this computer ---------------------------------------------------------------------------------------------

    /// <summary>The servers the filter lets through, as cards.</summary>
    public ObservableCollection<LocalServerViewModel> Servers { get; } = new ObservableCollection<LocalServerViewModel>();

    /// <summary>The person has servers (whatever the filter says).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowsServerCards))]
    private bool _hasServers;

    /// <summary>The cards: there are servers (the empty state otherwise).</summary>
    public bool ShowsServerCards => HasServers;

    /// <summary>No servers at all: the dashed first-open card.</summary>
    public bool ShowsEmpty => !HasServers;

    /// <summary>The filter hides every server: a short line says so.</summary>
    [ObservableProperty]
    private bool _showsNoMatch;

    /// <summary>The servers are kept in memory only, or the saved list could not be read.</summary>
    [ObservableProperty]
    private string _listNote = "";

    [ObservableProperty]
    private bool _hasListNote;

    // ---- in this app --------------------------------------------------------------------------------------------------

    /// <summary>The tools the app itself gives the assistant that the filter lets through (by name), read only.</summary>
    public ObservableCollection<AppToolViewModel> AppTools { get; } = new ObservableCollection<AppToolViewModel>();

    /// <summary>The app gives the assistant no tools of its own: one short line replaces the list.</summary>
    [ObservableProperty]
    private bool _showsAppEmpty;

    /// <summary>The app has tools but the filter hides every one: a short line says so.</summary>
    [ObservableProperty]
    private bool _showsAppNoMatch;

    // ---- on your server -----------------------------------------------------------------------------------------------

    public ObservableCollection<CloudGroupViewModel> CloudGroups { get; } = new ObservableCollection<CloudGroupViewModel>();

    /// <summary>"This server does not list its tools yet.", "...could not be loaded right now." or "...offers no tools"; empty when there are rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloudMessage))]
    private string _cloudMessage = "";

    public bool HasCloudMessage => CloudMessage.Length > 0;

    /// <summary>The list could not be loaded: Try again is offered.</summary>
    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private bool _isLoadingCloud;

    // ---- the drawer and the sheets ------------------------------------------------------------------------------------

    /// <summary>The details of the server whose card was opened; null while none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsOpen))]
    private ServerDetailsViewModel? _details;

    public bool IsDetailsOpen => Details is not null;

    /// <summary>The add or edit sheet, the import sheet or the scan sheet; null while none is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSheet))]
    private object? _sheet;

    public bool HasSheet => Sheet is not null;

    private void AddServer()
    {
        Details = null;
        OpenSheet(new ServerSheetViewModel(this, null));
    }

    private void Scan()
    {
        if (!_catalog.CanScan)
        {
            return;
        }
        Details = null;
        var sheet = new ScanSheetViewModel(this);
        OpenSheet(sheet);
        _ = sheet.StartAsync();
    }

    private void ImportServers() => OpenSheet(new ImportSheetViewModel(this));

    // A sheet that is replaced is closed first: nothing it holds (typed secret values) outlives it.
    private void OpenSheet(object sheet)
    {
        if (Sheet is IDisposable old)
        {
            old.Dispose();
        }
        Sheet = sheet;
    }

    /// <summary>Opens the edit sheet of a server (from its card's fix or its drawer).</summary>
    internal void Edit(string label)
    {
        Details = null;
        OpenSheet(new ServerSheetViewModel(this, label));
    }

    /// <summary>Opens the drawer of a server.</summary>
    internal void OpenDetails(LocalServerViewModel server)
    {
        foreach (var other in _allServers)
        {
            other.IsSelected = ReferenceEquals(other, server);
        }
        Details = new ServerDetailsViewModel(this, server);
    }

    /// <summary>Closes the sheet; a sheet that holds typed values forgets them.</summary>
    public void CloseSheet()
    {
        if (Sheet is IDisposable disposable)
        {
            disposable.Dispose();
        }
        Sheet = null;
    }

    /// <summary>Closes the drawer.</summary>
    public void CloseDetails()
    {
        Details = null;
        foreach (var server in _allServers)
        {
            server.IsSelected = false;
        }
    }

    private void CloseTop()
    {
        if (HasSheet)
        {
            CloseSheet();
        }
        else if (IsDetailsOpen)
        {
            CloseDetails();
        }
    }

    /// <summary>Opens the page: reads the catalog and loads the cloud tools.</summary>
    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        Refresh();
        return LoadCloudAsync(cancellationToken);
    }

    /// <summary>The app's language changed: every text is said again in the new one.</summary>
    public void Retext()
    {
        OnPropertyChanged(nameof(Strings));
        Refresh();
        BuildCloud();
        (Sheet as IRetext)?.Retext();
        Details?.Retext();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _catalog.Changed -= OnCatalogChanged;
        if (Sheet is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    // ---- refresh ------------------------------------------------------------------------------------------------------

    /// <summary>Reads the catalog and brings the bar and the lists up to date in place.</summary>
    public void Refresh()
    {
        var s = Strings;
        NeedsSignIn = !_catalog.SignedIn;
        CanScan = _catalog.CanScan;
        ScanCommand.NotifyCanExecuteChanged();
        PlainHttpPermitted = _catalog.PlainHttpPermitted;
        var policy = _catalog.Policy;
        (PolicyText, PolicyTip, PolicyIsWarning, HasPolicy) = policy switch
        {
            ToolsPolicy.Allowed => (s.ToolsPolicyAllowed, s.ToolsPolicyAllowedTip, false, true),
            ToolsPolicy.DeveloperMode => (s.ToolsPolicyDeveloper, s.ToolsPolicyDeveloperTip, true, true),
            ToolsPolicy.NotAllowed => (s.ToolsPolicyNotAllowed, s.ToolsBannerNotAllowed, true, true),
            _ => ("", "", false, false),
        };
        ShowsBanner = policy == ToolsPolicy.NotAllowed && !NeedsSignIn;
        BannerText = s.ToolsBannerNotAllowed;

        // The servers: the same card for the same label, in the catalog's order.
        var infos = _catalog.LocalServers;
        var roots = _catalog.RootNames;
        var existing = _allServers.ToDictionary(x => x.Label, StringComparer.Ordinal);
        _allServers.Clear();
        foreach (var info in infos)
        {
            if (existing.TryGetValue(info.Label, out var item))
            {
                item.Update(info, roots.Count);
            }
            else
            {
                item = new LocalServerViewModel(this, info, roots.Count);
            }
            _allServers.Add(item);
        }
        HasServers = _allServers.Count > 0;
        Rebuild();

        ListNote = _catalog.ServersUnreadable ? s.ToolsListUnreadable : !_catalog.ServersKeptOnDisk ? s.ToolsKeptInMemory : "";
        HasListNote = ListNote.Length > 0;
        LocalTabText = s.ToolsTabLocal(_allServers.Sum(x => x.ToolTotal));

        // The app's own tools (read only), rebuilt each time: a row holds nothing the person changes.
        _allApp.Clear();
        _allApp.AddRange(_catalog.AppTools.Select(t => new AppToolViewModel(s, t)));
        BuildApp();

        // "On your server" exists only for a governed agent that somebody is signed in to; elsewhere nothing of it is loaded or kept.
        var governed = _catalog.IsGoverned && !NeedsSignIn;
        ShowsServerTab = governed;
        if (!governed)
        {
            _cloud = null;
            _cloudLoaded = false;
            CloudGroups.Clear();
            CloudMessage = "";
            CanRetry = false;
            if (Tab == ToolsTab.Server)
            {
                Tab = ToolsTab.Computer;
            }
        }

        if (Details is { } details)
        {
            var server = _allServers.FirstOrDefault(x => x.Label == details.Label);
            if (server is null)
            {
                CloseDetails();
            }
            else
            {
                details.Refresh(server);
            }
        }

        if (_cloud is null)
        {
            ServerTabText = s.ToolsTabServer(0);
        }

        if (governed && !_cloudLoaded && !IsLoadingCloud)
        {
            _ = LoadCloudAsync();
        }
    }

    // The cards the filter lets through, in place (a card that stays keeps its state).
    private void Rebuild()
    {
        var wanted = _allServers.Where(x => x.Matches(Filter)).ToList();
        for (var i = Servers.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Servers[i]))
            {
                Servers.RemoveAt(i);
            }
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = Servers.IndexOf(wanted[i]);
            if (at < 0)
            {
                Servers.Insert(i, wanted[i]);
            }
            else if (at != i)
            {
                Servers.Move(at, i);
            }
        }
        ShowsNoMatch = HasServers && Servers.Count == 0 && Filter.Trim().Length > 0;
    }

    // The app's rows the filter lets through, in the catalog's order.
    private void BuildApp()
    {
        var s = Strings;
        AppTabText = s.ToolsTabApp(_allApp.Count);
        var wanted = _allApp.Where(x => x.Matches(Filter)).ToList();
        AppTools.Clear();
        foreach (var tool in wanted)
        {
            AppTools.Add(tool);
        }
        ShowsAppEmpty = _allApp.Count == 0;
        ShowsAppNoMatch = _allApp.Count > 0 && wanted.Count == 0;
    }

    // ---- on your server -----------------------------------------------------------------------------------------------

    /// <summary>Loads the cloud tools again (opening the page, Try again).</summary>
    public async Task LoadCloudAsync(CancellationToken cancellationToken = default)
    {
        if (!_catalog.SignedIn || !_catalog.IsGoverned)
        {
            _cloud = null;
            CloudGroups.Clear();
            CloudMessage = "";
            CanRetry = false;
            ServerTabText = Strings.ToolsTabServer(0);
            return;
        }
        IsLoadingCloud = true;
        CloudToolsResult result;
        try
        {
            result = await _catalog.GetCloudToolsAsync(cancellationToken);
        }
        finally
        {
            IsLoadingCloud = false;
        }
        _cloudLoaded = result.Status != CloudToolsStatus.NotSignedIn;
        _cloud = result;
        BuildCloud();
    }

    private void BuildCloud()
    {
        var s = Strings;
        CloudGroups.Clear();
        if (_cloud is not { } result)
        {
            ServerTabText = s.ToolsTabServer(0);
            return;
        }
        CanRetry = result.Status == CloudToolsStatus.Unavailable;
        CloudMessage = result.Status switch
        {
            CloudToolsStatus.NotListed => s.ToolsCloudNotListed,
            CloudToolsStatus.Unavailable => s.ToolsCloudUnavailable,
            CloudToolsStatus.Listed when result.Tools.Count == 0 => s.ToolsCloudNone,
            _ => "",
        };
        ServerTabText = s.ToolsTabServer(result.Tools.Count);
        foreach (var server in result.Tools.GroupBy(t => t.Server).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var group = new CloudGroupViewModel(this, server.Key, server, Filter); // the server's own order
            if (group.Tools.Count > 0)
            {
                CloudGroups.Add(group);
            }
        }
    }

    /// <summary>The switch of a server: it holds from the next message.</summary>
    internal async Task SwitchAsync(string label, bool on)
    {
        await _catalog.SetEnabledAsync(label, on);
        Refresh();
    }

    // The catalog raises Changed per server and status event, from any thread; one refresh after them is enough.
    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (_refreshPending || _disposed)
        {
            return;
        }
        _refreshPending = true;
        Post(() =>
        {
            _refreshPending = false;
            if (!_disposed)
            {
                Refresh();
            }
        });
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread: now when the caller is on it, else later.</summary>
    internal void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Post(action);
        }
    }

    /// <summary>True when <paramref name="text"/> contains the filter (any case); an empty filter matches everything.</summary>
    internal static bool Contains(string text, string filter) =>
        filter.Trim().Length == 0 || text.IndexOf(filter.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0;
}

/// <summary>A sheet or drawer that says its texts again when the language changes.</summary>
internal interface IRetext
{
    void Retext();
}
