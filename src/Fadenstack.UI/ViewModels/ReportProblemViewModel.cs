using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.Endpoints;
using Fadenstack.Logging;
using Fadenstack.Storage;

namespace Fadenstack.UI.ViewModels;

/// <summary>
/// The "Report a problem" sheet over the chat (SDK 0.3.1): to whom a report goes, and exactly what the mail will say. The SDK
/// sends nothing: "Write mail" hands a <c>mailto:</c> link to the panel's link hook (<see cref="AgentChatViewModel.LinkRequested"/>),
/// and "Copy details" puts the same text on the clipboard for webmail. It opens from About and from a failed turn
/// (<see cref="AgentChatViewModel.OpenReport"/>), which adds that error's code. Nothing from the chats goes in: see <see cref="ReportFacts"/>.
/// </summary>
public sealed class ReportProblemViewModel : ObservableObject
{
    private const int CopyStatusMilliseconds = 3000;

    private readonly AgentChatViewModel _chat;
    private bool _isOpen;
    private string? _errorCode;
    private string? _requestId;
    private ReportPlace? _place;
    private DateTimeOffset _time;
    private ReportDraft _draft;
    private string _copyStatus = "";
    private int _copyCount;
    private int _placeRun;
    private Task _placeTask = Task.CompletedTask;

    internal ReportProblemViewModel(AgentChatViewModel chat)
    {
        _chat = chat;
        _time = chat.Now();
        _draft = Build();
        CloseCommand = new RelayCommand(Close);
        WriteMailCommand = new RelayCommand(WriteMail, () => HasAddress);
        CopyCommand = new RelayCommand(Copy);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder, () => CanOpenLogFolder);
    }

    /// <summary>The sheet is open (presentation state, so a skin needs no code).</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(OpenSheet));
            }
        }
    }

    /// <summary>
    /// This view model while the sheet is open, else null: a skin shows the sheet as the content of a control bound to it, so the sheet
    /// is in the tree only while open and the keyboard focus goes into it and back out (<c>DrawerFocus</c>).
    /// </summary>
    public ReportProblemViewModel? OpenSheet => _isOpen ? this : null;

    private UiStrings Strings => _chat.Strings;

    /// <summary>"Report a problem".</summary>
    public string Title => Strings.ReportProblem;

    /// <summary>Whom the report goes to; null where there is nobody (governed, and the policy named no address).</summary>
    public string? Address => _draft.Address;

    public bool HasAddress => _draft.Address is not null;

    /// <summary>"This goes to support@fadenstack.com."; empty without an address.</summary>
    public string GoesTo => _draft.Address is { } address ? Strings.ReportGoesTo(address) : "";

    /// <summary>Governed, and IT named no address: "Ask your organisation's IT for help. You can copy the details below for them."; empty otherwise.</summary>
    public string AskIt => HasAddress ? "" : Strings.ReportAskIt;

    /// <summary>The organisation's Help (<c>&lt;server&gt;/help/</c>) when there is no address and a server; else null.</summary>
    public PanelLink? HelpLink => HasAddress ? null : _chat.About.HelpLink;

    public bool HasHelp => HelpLink is not null;

    /// <summary>There is nobody to write to: the sheet says to ask IT instead of offering mail.</summary>
    public bool HasNoAddress => !HasAddress;

    /// <summary>"This report contains:": the heading of <see cref="Preview"/> and its accessible name.</summary>
    public string Contains => Strings.ReportContains;

    /// <summary>The mail's text as the person's mail app will get it (without the lines the link's limit dropped).</summary>
    public string Preview => _draft.Preview;

    /// <summary>"Nothing from your chats is included."</summary>
    public string NoChats => Strings.ReportNoChats;

    /// <summary>The mail's subject.</summary>
    public string Subject => _draft.Subject;

    /// <summary>The <c>mailto:</c> link "Write mail" opens; null without an address.</summary>
    public string? Mailto => _draft.Mailto;

    /// <summary>What "Copy details" copies, before the profile path is masked.</summary>
    public string CopyText => _draft.CopyText;

    /// <summary>The app logs with the SDK's file log (<see cref="FadenstackFileLoggerProvider.RetentionDays"/>), so there is a folder to open.</summary>
    public bool CanOpenLogFolder => FadenstackFileLoggerProvider.RetentionDays is not null;

    /// <summary>"Copied" for a few seconds after "Copy details".</summary>
    public string CopyStatus
    {
        get => _copyStatus;
        private set
        {
            if (SetProperty(ref _copyStatus, value))
            {
                OnPropertyChanged(nameof(HasCopyStatus));
            }
        }
    }

    public bool HasCopyStatus => _copyStatus.Length > 0;

    /// <summary>Opens the person's mail app with the report, through the panel's link hook.</summary>
    public IRelayCommand WriteMailCommand { get; }

    /// <summary>Copies <see cref="CopyText"/> with the profile path masked (<see cref="DiagnosticText.MaskUserPaths(string)"/>), through <see cref="AgentChatOptions.Clipboard"/>.</summary>
    public IRelayCommand CopyCommand { get; }

    /// <summary>Opens <see cref="FadenstackPaths.LogRoot"/> as a <c>file:</c> link through the panel's link hook; only with a file log.</summary>
    public IRelayCommand OpenLogFolderCommand { get; }

    /// <summary>Closes the sheet (Escape and Close).</summary>
    public IRelayCommand CloseCommand { get; }

    /// <summary>Finishes where the endpoint's address points (standalone); for tests to wait on.</summary>
    internal Task PlaceResolved => _placeTask;

    internal void Open(string? errorCode, string? requestId)
    {
        _errorCode = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode;
        _requestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId;
        _time = _chat.Now();
        _place = null;
        CopyStatus = "";
        _copyCount++;
        Rebuild();
        IsOpen = true;
        var run = ++_placeRun;
        _placeTask = _chat.Agent.Connection == AgentConnection.Standalone && _chat.Agent.EndpointUrl is { } url
            ? ResolvePlaceAsync(url, run)
            : Task.CompletedTask;
    }

    private void Close() => IsOpen = false;

    /// <summary>The language or the profile changed: the open sheet says it again.</summary>
    internal void Refresh()
    {
        if (_isOpen)
        {
            Rebuild();
        }
        else
        {
            OnPropertyChanged(nameof(Title));
        }
    }

    private async Task ResolvePlaceAsync(Uri url, int run)
    {
        ReportPlace? place;
        try
        {
            place = await EndpointCheck.PlaceOfAsync(url).ConfigureAwait(false) switch
            {
                EndpointPlace.ThisComputer => ReportPlace.ThisComputer,
                EndpointPlace.LocalNetwork => ReportPlace.LocalNetwork,
                _ => ReportPlace.Internet,
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return; // where it points stays unknown: the line is left out
        }
        _chat.Post(() =>
        {
            if (run == _placeRun)
            {
                _place = place;
                Rebuild();
            }
        });
    }

    private void Rebuild()
    {
        _draft = Build();
        OnPropertyChanged(string.Empty);
        WriteMailCommand.NotifyCanExecuteChanged();
        OpenLogFolderCommand.NotifyCanExecuteChanged();
    }

    private ReportDraft Build()
    {
        var agent = _chat.Agent;
        var governed = agent.Connection == AgentConnection.Governed;
        var profile = _chat.CurrentProfile;
        var app = string.Join(" ", new[] { agent.App?.Name, agent.App?.Version }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        var facts = new ReportFacts
        {
            App = app.Length == 0 ? null : app,
            SdkVersion = SdkInfo.Version,
            Sdk = "Fadenstack SDK " + SdkInfo.Version + " (.NET)",
            System = ReportSystem.Current(),
            Mode = governed ? Strings.ReportModeGoverned : Strings.ReportModeOwnAi,
            Agent = ReportFactsText.Agent(profile is null ? null : new ReportAgent(profile.Name, profile.Version, profile.Draft, profile.Model), Strings),
            Server = governed ? _chat.About.Server : null,
            Endpoint = governed ? null : ReportFactsText.Endpoint(agent.EndpointDialectName, _place, Strings),
            Time = ReportFactsText.Time(_time),
            Error = _errorCode is null ? null : new ReportedError(_errorCode, _requestId),
        };
        return ReportMailBuilder.Build(ReportAddress.Resolve(agent.Connection, agent.MachinePolicy, _chat.HostSupportEmail), Strings.ReportLabels(), facts);
    }

    private void WriteMail()
    {
        if (_draft.Mailto is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            _chat.RequestLink(uri);
        }
    }

    private void Copy()
    {
        _chat.Clipboard.SetText(DiagnosticText.MaskUserPaths(_draft.CopyText));
        CopyStatus = Strings.Copied;
        _ = ClearCopyStatusAsync(++_copyCount);
    }

    private async Task ClearCopyStatusAsync(int count)
    {
        await Task.Delay(CopyStatusMilliseconds).ConfigureAwait(true);
        if (count == _copyCount)
        {
            CopyStatus = "";
        }
    }

    private void OpenLogFolder()
    {
        if (!CanOpenLogFolder)
        {
            return;
        }
        var folder = Path.GetFullPath(FadenstackPaths.LogRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Uri.TryCreate(folder + Path.DirectorySeparatorChar, UriKind.Absolute, out var uri))
        {
            _chat.RequestLink(uri);
        }
    }
}
