using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.LocalTools;

namespace Fadenstack.UI.Tools;

/// <summary>
/// A name and a value in the sheet: an environment variable or a header (OWASP ASVS 5.0 V13). A saved value shows as dots and the
/// view model never holds it: Replace clears the mark and lets the person type a new one, which lives here only until the sheet is
/// saved, tested or closed (<see cref="Forget"/>). The value is never put in a message, a log or an exception.
/// </summary>
public sealed partial class SecretRowViewModel : ObservableObject, IToolsTexts
{
    private readonly UiStrings _strings;
    private readonly Action<SecretRowViewModel> _remove;

    internal SecretRowViewModel(UiStrings strings, string name, bool isKept, Action<SecretRowViewModel> remove)
    {
        _strings = strings;
        _name = name;
        _isKept = isKept;
        _remove = remove;
        ReplaceCommand = new RelayCommand(() =>
        {
            IsKept = false;
            Value = "";
        });
        RemoveCommand = new RelayCommand(() =>
        {
            Forget();
            _remove(this);
        });
    }

    public UiStrings Strings => _strings;

    [ObservableProperty]
    private string _name;

    /// <summary>The new value while it is typed; empty when the saved one is kept.</summary>
    [ObservableProperty]
    private string _value = "";

    /// <summary>The value is the one saved earlier, which the page never sees.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsInput))]
    private bool _isKept;

    public bool ShowsInput => !IsKept;

    public IRelayCommand ReplaceCommand { get; }

    public IRelayCommand RemoveCommand { get; }

    internal SecretPairDraft ToDraft() => new SecretPairDraft { Name = Name, Value = Value, IsKept = IsKept };

    /// <summary>Forgets the typed value.</summary>
    internal void Forget() => Value = "";
}

/// <summary>
/// The modal sheet that adds a server or edits one: a segmented choice of a program on this computer (command, arguments, working
/// folder, environment) or an address (URL, headers), secrets as masked rows with Replace, then Test and Add (Save when editing).
/// Checked by the catalog's rules before it is saved. Saved values are never shown again.
/// <para>
/// How a value travels: the skin binds the row's password box to <see cref="SecretRowViewModel.Value"/>; Save and Test copy the
/// rows into a <see cref="ServerDraft"/> and give it to the catalog, which puts a saved value back for a row that is kept; the
/// draft's values are cleared when the call returns, and the rows' values when the sheet is saved, cancelled or disposed.
/// </para>
/// </summary>
public sealed partial class ServerSheetViewModel : ObservableObject, IToolsTexts, IRetext, IDisposable
{
    private readonly ToolsViewModel _page;
    private readonly string? _original;
    private bool _disposed;

    internal ServerSheetViewModel(ToolsViewModel page, string? original)
    {
        _page = page;
        _original = original;
        var s = page.Strings;
        var draft = original is null ? new ServerDraft() : page.Catalog.GetDraft(original) ?? new ServerDraft();
        Title = original is null ? s.ToolsSheetAddTitle : s.ToolsSheetEditTitle(original);
        _label = draft.Label;
        _enabled = draft.Enabled;
        _isProgram = draft.IsProgram;
        _command = draft.Command;
        _argumentsText = string.Join("\n", draft.Arguments);
        _workingFolder = draft.WorkingFolder;
        _url = draft.Url;
        _timeoutText = draft.CallTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        foreach (var pair in draft.Environment)
        {
            EnvironmentRows.Add(new SecretRowViewModel(s, pair.Name, pair.IsKept, Remove(EnvironmentRows)));
        }
        foreach (var pair in draft.Headers)
        {
            HeaderRows.Add(new SecretRowViewModel(s, pair.Name, pair.IsKept, Remove(HeaderRows)));
        }
        draft.ClearSecrets();
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        TestCommand = new AsyncRelayCommand(TestAsync);
        CancelCommand = new RelayCommand(_page.CloseSheet);
        ImportCommand = new RelayCommand(() => _page.ImportServersCommand.Execute(null));
        ChooseFolderCommand = new AsyncRelayCommand(ChooseFolderAsync);
        AddEnvironmentCommand = new RelayCommand(() => EnvironmentRows.Add(new SecretRowViewModel(_page.Strings, "", false, Remove(EnvironmentRows))));
        AddHeaderCommand = new RelayCommand(() => HeaderRows.Add(new SecretRowViewModel(_page.Strings, "", false, Remove(HeaderRows))));
    }

    public UiStrings Strings => _page.Strings;

    public string Title { get; private set; }

    public bool IsEditing => _original is not null;

    /// <summary>The sheet adds a server (otherwise it edits one): its primary button says Add, then Save.</summary>
    public bool IsAdding => _original is null;

    public string PrimaryText => IsAdding ? Strings.ToolsAddButton : Strings.ToolsSave;

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private bool _enabled;

    /// <summary>A program on this computer (otherwise an address).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddress))]
    private bool _isProgram;

    public bool IsAddress
    {
        get => !IsProgram;
        set => IsProgram = !value;
    }

    [ObservableProperty]
    private string _command;

    /// <summary>One argument per line; never one shell string.</summary>
    [ObservableProperty]
    private string _argumentsText;

    [ObservableProperty]
    private string _workingFolder;

    [ObservableProperty]
    private string _url;

    [ObservableProperty]
    private string _timeoutText;

    public ObservableCollection<SecretRowViewModel> EnvironmentRows { get; } = new ObservableCollection<SecretRowViewModel>();

    public ObservableCollection<SecretRowViewModel> HeaderRows { get; } = new ObservableCollection<SecretRowViewModel>();

    /// <summary>Why the sheet cannot be saved; empty when nothing is wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    public bool HasError => ErrorText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTest))]
    private string _testText = "";

    public bool HasTest => TestText.Length > 0;

    [ObservableProperty]
    private bool _isBusy;

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand TestCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand ImportCommand { get; }

    public IAsyncRelayCommand ChooseFolderCommand { get; }

    public IRelayCommand AddEnvironmentCommand { get; }

    public IRelayCommand AddHeaderCommand { get; }

    private Action<SecretRowViewModel> Remove(ObservableCollection<SecretRowViewModel> rows) => row => rows.Remove(row);

    private async Task ChooseFolderAsync()
    {
        if (_page.Files is not { } files)
        {
            return;
        }
        var chosen = await files.PickFolderAsync(Strings.ToolsFieldFolder, string.IsNullOrWhiteSpace(WorkingFolder) ? null : WorkingFolder, CancellationToken.None);
        if (chosen is not null)
        {
            WorkingFolder = chosen;
        }
    }

    private ServerDraft ToDraft(int timeout) => new ServerDraft
    {
        Label = Label.Trim(),
        Enabled = Enabled,
        IsProgram = IsProgram,
        Command = Command,
        Arguments = ArgumentsText.Replace("\r\n", "\n").Split('\n').Where(a => a.Length > 0).ToList(),
        WorkingFolder = WorkingFolder,
        Environment = EnvironmentRows.Where(r => r.Name.Trim().Length > 0).Select(r => r.ToDraft()).ToList(),
        Url = Url,
        Headers = HeaderRows.Where(r => r.Name.Trim().Length > 0).Select(r => r.ToDraft()).ToList(),
        CallTimeoutSeconds = timeout,
    };

    private bool TryDraft(out ServerDraft draft)
    {
        if (!int.TryParse(TimeoutText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var timeout))
        {
            draft = new ServerDraft();
            ErrorText = Strings.ToolsProblemTimeout;
            return false;
        }
        draft = ToDraft(timeout);
        var problem = _page.Catalog.Validate(draft, _original);
        ErrorText = problem == ServerProblem.None ? "" : ProblemText(Strings, problem);
        if (problem != ServerProblem.None)
        {
            draft.ClearSecrets();
        }
        return problem == ServerProblem.None;
    }

    private async Task SaveAsync()
    {
        if (!TryDraft(out var draft))
        {
            return;
        }
        IsBusy = true;
        ServerProblem problem;
        try
        {
            problem = await _page.Catalog.SaveAsync(_original, draft);
        }
        finally
        {
            draft.ClearSecrets();
            IsBusy = false;
        }
        if (problem != ServerProblem.None)
        {
            ErrorText = ProblemText(Strings, problem);
            return;
        }
        _page.CloseSheet(); // disposes this sheet: the typed values go
        _page.Refresh();
    }

    private async Task TestAsync()
    {
        if (!TryDraft(out var draft))
        {
            return;
        }
        IsBusy = true;
        TestText = Strings.ToolsTestRunning;
        try
        {
            TestText = TestLine(Strings, await _page.Catalog.TestAsync(draft, _original));
        }
        finally
        {
            draft.ClearSecrets();
            IsBusy = false;
        }
    }

    /// <summary>The line a test result shows; a refusal of plain http says why, in its own words.</summary>
    internal static string TestLine(UiStrings s, ServerTestInfo result)
    {
        if (result.PlainHttp is { } refusal)
        {
            return LocalServerViewModel.RefusalText(s, refusal);
        }
        switch (result.Outcome)
        {
            case TestOutcome.NotAllowed:
                return s.ToolsTestNotAllowed;
            case TestOutcome.Started:
                return result.Tools.Count == 0 ? s.ToolsTestOkNoTools : s.ToolsTestOk(string.Join(", ", result.Tools));
            default:
                return s.ToolsTestFailed(string.IsNullOrWhiteSpace(result.Error) ? s.ToolsReasonUnknown : result.Error!);
        }
    }

    /// <summary>What is wrong with a draft, in the person's language.</summary>
    internal static string ProblemText(UiStrings s, ServerProblem problem) => problem switch
    {
        ServerProblem.LabelMissing => s.ToolsProblemLabelMissing,
        ServerProblem.LabelInvalid => s.ToolsProblemLabelInvalid,
        ServerProblem.LabelTaken => s.ToolsProblemLabelTaken,
        ServerProblem.CommandMissing => s.ToolsProblemCommandMissing,
        ServerProblem.CommandInvalid => s.ToolsProblemCommandInvalid,
        ServerProblem.FolderNotAbsolute => s.ToolsProblemFolder,
        ServerProblem.NameInvalid => s.ToolsProblemName,
        ServerProblem.ValueInvalid => s.ToolsProblemValue,
        ServerProblem.AddressMissing => s.ToolsProblemAddressMissing,
        ServerProblem.AddressInvalid => s.ToolsProblemAddressInvalid,
        ServerProblem.TimeoutOutOfRange => s.ToolsProblemTimeout,
        ServerProblem.PlainHttpWithHeaders => s.ToolsProblemPlainHttpWithHeaders,
        ServerProblem.PlainHttpNotLocal => s.ToolsProblemPlainHttpNotLocal,
        _ => "",
    };

    public void Retext()
    {
        Title = _original is null ? Strings.ToolsSheetAddTitle : Strings.ToolsSheetEditTitle(_original);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(Strings));
    }

    /// <summary>The sheet is saved, cancelled or replaced: no typed secret value stays in the rows.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var row in EnvironmentRows.Concat(HeaderRows))
        {
            row.Forget();
        }
    }
}
