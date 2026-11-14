using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.SkillAuthoring;
using Fadenstack.UI.Services;
using Microsoft.Extensions.AI;

namespace Fadenstack.UI.Skills;

/// <summary>
/// A skill view model that tells its texts (as the connection wizard does): a view reads every text from <see cref="Strings"/> and says them
/// again when it changes (the app's language changed, <c>Retext</c>).
/// </summary>
public interface ISkillTexts : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>The texts the view model was given; the current UI language's by default.</summary>
    UiStrings Strings { get; }
}

/// <summary>
/// What the editor needs from the app that a view model cannot do itself: choose a file or a folder, and write a file. The skin
/// implements it with the system's dialogs; a host without any answers null or false.
/// </summary>
public interface ISkillFileHost
{
    /// <summary>The folder or zip the person chose to import; null when they cancelled.</summary>
    Task<string?> PickImportAsync(CancellationToken cancellationToken);

    /// <summary>Writes the exported zip where the person chose; false when they cancelled or it could not be written.</summary>
    Task<bool> SaveExportAsync(string suggestedFileName, byte[] zip, CancellationToken cancellationToken);

    /// <summary>A file the person chose to add to a skill (its name and content); null when they cancelled.</summary>
    Task<SkillFile?> PickResourceAsync(CancellationToken cancellationToken);
}

/// <summary>What kind of thing a message says: a skin shows an error as an alert and the others as a status.</summary>
public enum SkillMessageKind
{
    /// <summary>What an action did ("x was saved"): polite.</summary>
    Information,

    /// <summary>Something the person should look at, that did not stop the action.</summary>
    Warning,

    /// <summary>The action did not happen: assertive.</summary>
    Error,
}

/// <summary>One check's finding, in words, for the list under the editor.</summary>
public sealed class SkillIssueViewModel
{
    internal SkillIssueViewModel(SkillIssue issue, UiStrings strings)
    {
        Issue = issue;
        Text = strings.SkillIssueText(issue.Code, issue.Subject ?? "", issue.Limit, issue.Actual, issue.Detail ?? "");
    }

    public SkillIssue Issue { get; }

    public string Text { get; }

    /// <summary>An error stops the save; a warning only says.</summary>
    public bool IsError => Issue.IsError;

    /// <summary>The field or file it is about (<c>name</c>, <c>description</c>, <c>instructions</c>, a path), for a skin that marks the field.</summary>
    public string? Subject => Issue.Subject;
}

/// <summary>A skill in the library list: where it comes from, what the person may do with it, and its switch.</summary>
public sealed class SkillRowViewModel : ObservableObject
{
    private readonly Func<UiStrings> _strings;
    private bool _isEnabled;

    internal SkillRowViewModel(SkillEntry entry, Func<UiStrings> strings)
    {
        Entry = entry;
        _strings = strings;
        _isEnabled = entry.Enabled;
    }

    public SkillEntry Entry { get; }

    public string Name => Entry.Name;

    public string Description => Entry.Description;

    public SkillOrigin Origin => Entry.Origin;

    /// <summary>"Built in", "Yours" or "Your organisation".</summary>
    public string OriginLabel => _strings().SkillOriginLabel(Entry.Origin);

    /// <summary>"Not reviewed" for the person's own skills; empty for the others.</summary>
    public string StatusLabel => Entry.NotReviewed ? _strings().SkillNotReviewed : "";

    public bool IsNotReviewed => Entry.NotReviewed;

    /// <summary>The skill cannot be used as it is (its SKILL.md breaks a rule): the module does not list it; open it to fix it, or delete it.</summary>
    public bool IsBroken => Entry.IsBroken;

    /// <summary>"This skill cannot be read: ..." for a broken skill (with the reason when the library knows it); empty for the others.</summary>
    public string BrokenText => !Entry.IsBroken ? "" : Entry.BrokenIssue is { } issue
        ? _strings().SkillBroken(_strings().SkillIssueText(issue.Code, issue.Subject ?? "", issue.Limit, issue.Actual, issue.Detail ?? ""))
        : _strings().SkillBrokenNoReason;

    public bool IsEnabled
    {
        get => _isEnabled;
        internal set => SetProperty(ref _isEnabled, value);
    }

    public string SwitchLabel => _isEnabled ? _strings().SkillOn : _strings().SkillOff;

    public bool CanSwitch => Entry.CanSwitch;

    public bool IsReadOnly => Entry.IsReadOnly;

    /// <summary>Open it for editing (own) or for reading (bundled); an organisation's skill has no text to show.</summary>
    public bool CanOpen => Entry.Origin != SkillOrigin.Organisation;

    public bool CanDuplicate => Entry.Origin != SkillOrigin.Organisation && !Entry.IsBroken;

    public bool CanDelete => Entry.Origin == SkillOrigin.Own;

    public bool CanExport => Entry.Origin != SkillOrigin.Organisation && !Entry.IsBroken;

    /// <summary>The names a screen reader says for the row's buttons ("Edit contract-review"), so the buttons of two rows are told apart.</summary>
    public string EditLabel => _strings().SkillEditNamed(Name);

    public string ViewLabel => _strings().SkillViewNamed(Name);

    public string DuplicateLabel => _strings().SkillDuplicateNamed(Name);

    public string DeleteLabel => _strings().SkillDeleteNamed(Name);

    public string ExportLabel => _strings().SkillExportNamed(Name);

    internal void Retext()
    {
        OnPropertyChanged(nameof(BrokenText));
        OnPropertyChanged(nameof(EditLabel));
        OnPropertyChanged(nameof(ViewLabel));
        OnPropertyChanged(nameof(DuplicateLabel));
        OnPropertyChanged(nameof(DeleteLabel));
        OnPropertyChanged(nameof(ExportLabel));
        OnPropertyChanged(nameof(OriginLabel));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(SwitchLabel));
    }
}

/// <summary>
/// The skill library (sdk/51 F04): the bundled, the person's own and (governed mode) the organisation's skills with their labels and
/// on/off switches, and new, edit, duplicate, delete (after a confirmation), import (after the checks and a confirmation) and export.
/// Platform-neutral: the skins draw it. Nothing is written except by a command the person started, and an import writes nothing until
/// every check has passed and the person pressed Import. A newer action makes an older one's answer stale, and a stale answer changes nothing.
/// </summary>
public sealed class SkillLibraryViewModel : ObservableObject, ISkillTexts
{
    private readonly ISkillWorkshop _workshop;
    private readonly ISkillFileHost? _files;
    private readonly IChatClient? _model;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<UiStrings> _strings;
    private int _listVersion;
    private int _importVersion;
    private IReadOnlyList<SkillRowViewModel> _rows = Array.Empty<SkillRowViewModel>();
    private bool _isLoading;
    private bool _loaded;
    private bool _failed;
    private string _message = "";
    private SkillMessageKind _messageKind;
    private readonly SkillDraftViewModel? _draft;
    private bool _draftOpen;
    private SkillRowViewModel? _pendingDelete;
    private ImportAnalysis? _pendingImport;
    private IReadOnlyList<SkillIssueViewModel> _importIssues = Array.Empty<SkillIssueViewModel>();
    private bool _importBusy;
    private SkillEditorViewModel? _editor;

    /// <param name="workshop">The skills (<c>Fadenstack.Skills.SkillWorkshop</c>).</param>
    /// <param name="files">Dialogs for import, export and adding a file; without it those commands do nothing.</param>
    /// <param name="model">The model for the editor's "ask the model once" (one call, no tool runs); without it that button is off.</param>
    /// <param name="dispatcher">The UI thread; inline by default (tests, headless hosts).</param>
    /// <param name="strings">The texts; the current UI language's by default.</param>
    /// <param name="draftSource">The app's conversation, for "Draft from this chat" (see <see cref="CreateDraft"/>); without it the library has no such entry.</param>
    public SkillLibraryViewModel(ISkillWorkshop workshop, ISkillFileHost? files = null, IChatClient? model = null, IUiDispatcher? dispatcher = null,
        Func<UiStrings>? strings = null, Func<IReadOnlyList<SkillDraftMessage>>? draftSource = null)
    {
        _workshop = workshop ?? throw new ArgumentNullException(nameof(workshop));
        _files = files;
        _model = model;
        _dispatcher = dispatcher ?? InlineDispatcher.Instance;
        var current = UiStrings.Current;
        _strings = strings ?? (() => current);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        NewCommand = new RelayCommand(New, () => _editor is null);
        EditCommand = new AsyncRelayCommand<SkillRowViewModel>(EditAsync, row => row is { CanOpen: true } && _editor is null);
        DuplicateCommand = new AsyncRelayCommand<SkillRowViewModel>(DuplicateAsync, row => row is { CanDuplicate: true } && _editor is null);
        AskDeleteCommand = new RelayCommand<SkillRowViewModel>(row => PendingDelete = row, row => row is { CanDelete: true });
        ConfirmDeleteCommand = new AsyncRelayCommand(ConfirmDeleteAsync, () => _pendingDelete is not null);
        CancelDeleteCommand = new RelayCommand(() => PendingDelete = null, () => _pendingDelete is not null);
        ToggleCommand = new AsyncRelayCommand<SkillRowViewModel>(ToggleAsync, row => row is { CanSwitch: true });
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => _files is not null && !_importBusy && _editor is null);
        ConfirmImportCommand = new AsyncRelayCommand(ConfirmImportAsync, () => _pendingImport is { Ok: true } && !_importBusy);
        CancelImportCommand = new RelayCommand(CancelImport, () => _pendingImport is not null);
        ExportCommand = new AsyncRelayCommand<SkillRowViewModel>(ExportAsync, row => _files is not null && row is { CanExport: true });
        if (draftSource is not null)
        {
            _draft = CreateDraft(draftSource);
            _draft.ProposalReady += (_, _) => DraftOpen = false;
        }
        DraftFromChatCommand = new RelayCommand(() => DraftOpen = !_draftOpen, () => _draft is not null && _editor is null);
    }

    /// <summary>The texts; a skin binds the fixed ones and the computed ones below, and follows this when the language changes.</summary>
    public UiStrings Strings => _strings();

    /// <summary>"Draft from this chat": the draft, when the app gave a conversation (<c>draftSource</c>); null otherwise, and then the library has no such entry.</summary>
    public SkillDraftViewModel? Draft => _draft;

    public bool HasDraft => _draft is not null;

    /// <summary>The draft's panel is shown (the entry was pressed and no proposal has opened since).</summary>
    public bool DraftOpen
    {
        get => _draftOpen;
        private set => SetProperty(ref _draftOpen, value);
    }

    /// <summary>The "Draft from this chat" entry: shows or hides the draft's panel.</summary>
    public IRelayCommand DraftFromChatCommand { get; }

    /// <summary>The skills, bundled first, then the person's own, then the organisation's.</summary>
    public IReadOnlyList<SkillRowViewModel> Rows => _rows;

    public bool IsLoading => _isLoading;

    /// <summary>The list could not be read.</summary>
    public bool Failed => _failed;

    public bool IsEmpty => _loaded && _rows.Count == 0;

    /// <summary>What the library says instead of a list: reading, could not read, none yet; else empty.</summary>
    public string Note => _isLoading && !_loaded ? _strings().SkillLibraryLoading : _failed ? _strings().SkillLibraryFailed : IsEmpty ? _strings().SkillLibraryEmpty : "";

    /// <summary>What the last action did ("x was saved"), or why not; empty when nothing was said.</summary>
    public string Message => _message;

    /// <summary>Whether <see cref="Message"/> is information, a warning or an error; a view shows an error as an alert (assertive) and the rest as a status (polite).</summary>
    public SkillMessageKind MessageKind => _messageKind;

    /// <summary>There is a message and it is an error: a skin shows it as an alert.</summary>
    public bool MessageIsError => _message.Length > 0 && _messageKind == SkillMessageKind.Error;

    /// <summary>There is a message and it is a warning.</summary>
    public bool MessageIsWarning => _message.Length > 0 && _messageKind == SkillMessageKind.Warning;

    /// <summary>There is a message and it is information: a skin shows it as a status.</summary>
    public bool MessageIsInformation => _message.Length > 0 && _messageKind == SkillMessageKind.Information;

    private void Say(string text, SkillMessageKind kind = SkillMessageKind.Information)
    {
        _messageKind = kind;
        _message = text ?? "";
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(MessageKind));
        OnPropertyChanged(nameof(MessageIsError));
        OnPropertyChanged(nameof(MessageIsWarning));
        OnPropertyChanged(nameof(MessageIsInformation));
    }

    /// <summary>The skill the person asked to delete, until they confirm or cancel.</summary>
    public SkillRowViewModel? PendingDelete
    {
        get => _pendingDelete;
        private set
        {
            if (SetProperty(ref _pendingDelete, value))
            {
                OnPropertyChanged(nameof(DeleteTitle));
                ConfirmDeleteCommand.NotifyCanExecuteChanged();
                CancelDeleteCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string DeleteTitle => _pendingDelete is null ? "" : _strings().SkillDeleteTitle(_pendingDelete.Name);

    public string DeleteBody => _strings().SkillDeleteBody;

    /// <summary>What an import found, until the person confirms or cancels. Nothing is written yet.</summary>
    public ImportAnalysis? PendingImport => _pendingImport;

    /// <summary>The findings of the import's checks.</summary>
    public IReadOnlyList<SkillIssueViewModel> ImportIssues => _importIssues;

    /// <summary>The import passed every check: the person may press Import.</summary>
    public bool CanConfirmImport => _pendingImport is { Ok: true };

    /// <summary>The import would replace a skill of the same name: the button says Replace, and the text says so.</summary>
    public bool ImportReplaces => _pendingImport is { WouldReplace: true };

    public string ImportTitle => _pendingImport?.Document is { } document ? _strings().SkillImportTitle(document.Name) : _strings().SkillImportRefused;

    public string ImportBody => _pendingImport is null ? "" : !_pendingImport.Ok ? _strings().SkillImportRefused
        : _pendingImport.WouldReplace ? _strings().SkillImportReplaceBody(_pendingImport.Document!.Name) : _strings().SkillImportBody;

    /// <summary>The editor that is open, if any (new, edit, duplicate, view or a draft's proposal).</summary>
    public SkillEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (SetProperty(ref _editor, value))
            {
                NewCommand.NotifyCanExecuteChanged();
                EditCommand.NotifyCanExecuteChanged();
                DuplicateCommand.NotifyCanExecuteChanged();
                ImportCommand.NotifyCanExecuteChanged();
                DraftFromChatCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IRelayCommand NewCommand { get; }

    public IAsyncRelayCommand<SkillRowViewModel> EditCommand { get; }

    public IAsyncRelayCommand<SkillRowViewModel> DuplicateCommand { get; }

    public IRelayCommand<SkillRowViewModel> AskDeleteCommand { get; }

    public IAsyncRelayCommand ConfirmDeleteCommand { get; }

    public IRelayCommand CancelDeleteCommand { get; }

    /// <summary>Switches a skill on or off.</summary>
    public IAsyncRelayCommand<SkillRowViewModel> ToggleCommand { get; }

    public IAsyncRelayCommand ImportCommand { get; }

    public IAsyncRelayCommand ConfirmImportCommand { get; }

    public IRelayCommand CancelImportCommand { get; }

    public IAsyncRelayCommand<SkillRowViewModel> ExportCommand { get; }

    /// <summary>Reads the list again.</summary>
    public async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _listVersion);
        _isLoading = true;
        NotifyList();
        IReadOnlyList<SkillEntry>? entries = null;
        try
        {
            entries = await _workshop.ListAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            entries = null;
        }
        Ui(() =>
        {
            if (version != _listVersion)
            {
                return; // a newer read is on its way
            }
            _isLoading = false;
            if (entries is null)
            {
                _failed = true;
            }
            else
            {
                _failed = false;
                _loaded = true;
                _rows = entries.Select(e => new SkillRowViewModel(e, _strings)).ToList();
            }
            NotifyList();
        });
    }

    private void NotifyList()
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(Failed));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Note));
    }

    /// <summary>The app's language changed: the labels are said in the new one.</summary>
    public void Retext()
    {
        foreach (var row in _rows)
        {
            row.Retext();
        }
        OnPropertyChanged(nameof(Strings));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(DeleteTitle));
        OnPropertyChanged(nameof(DeleteBody));
        OnPropertyChanged(nameof(ImportTitle));
        OnPropertyChanged(nameof(ImportBody));
        _draft?.Retext();
        _editor?.Retext();
    }

    private void Ui(Action action)
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

    private SkillEditorViewModel NewEditor(SkillEditorMode mode, SkillDocument document, string? originalName, IReadOnlyList<SkillIssue>? readIssues = null) =>
        new(_workshop, mode, document, originalName, readIssues, _files, _model, _dispatcher, _strings);

    private void Open(SkillEditorViewModel editor)
    {
        if (ReferenceEquals(_editor, editor))
        {
            return;
        }
        editor.Closed += OnEditorClosed;
        Editor = editor;
    }

    /// <summary>Shows an editor the app opened (a draft's proposal): the library closes it again when it is saved or cancelled.</summary>
    public void OpenEditor(SkillEditorViewModel editor) => Open(editor ?? throw new ArgumentNullException(nameof(editor)));

    private void OnEditorClosed(object? sender, SkillEditorClosedEventArgs e)
    {
        if (sender is SkillEditorViewModel editor)
        {
            editor.Closed -= OnEditorClosed;
        }
        Ui(() =>
        {
            Editor = null;
            if (e.Saved)
            {
                Say(_strings().SkillSaved(e.Name ?? ""));
            }
        });
        _ = RefreshAsync();
    }

    /// <summary>
    /// "Draft from this chat": a draft view model on the app's conversation. When the model's draft is ready it opens in <see cref="Editor"/> as a
    /// proposal; nothing is saved until the person presses Save there.
    /// </summary>
    public SkillDraftViewModel CreateDraft(Func<IReadOnlyList<SkillDraftMessage>> conversation)
    {
        var draft = new SkillDraftViewModel(_workshop, _model, conversation, _files, _dispatcher, _strings);
        draft.ProposalReady += (_, editor) => Open(editor);
        return draft;
    }

    private void New() => Open(NewEditor(SkillEditorMode.New, new SkillDocument(), null));

    private async Task EditAsync(SkillRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }
        var read = await _workshop.ReadAsync(row.Name).ConfigureAwait(false);
        Ui(() =>
        {
            if (read.Document is null)
            {
                Say(_strings().SkillIssueText(read.Issues.FirstOrDefault()?.Code ?? SkillIssueCode.ReadFailed, row.Name, 0, 0, read.Issues.FirstOrDefault()?.Detail ?? ""), SkillMessageKind.Error);
                return;
            }
            Open(NewEditor(row.IsReadOnly ? SkillEditorMode.View : SkillEditorMode.Edit, read.Document, row.IsReadOnly ? null : row.Name, read.Issues));
        });
    }

    private async Task DuplicateAsync(SkillRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }
        var read = await _workshop.ReadAsync(row.Name).ConfigureAwait(false);
        var takenNames = (await _workshop.ListAsync().ConfigureAwait(false)).Select(e => e.Name).ToList();
        var taken = new HashSet<string>(takenNames, StringComparer.Ordinal);
        Ui(() =>
        {
            if (read.Document is null)
            {
                Say(_strings().SkillIssueText(SkillIssueCode.ReadFailed, row.Name, 0, 0, ""), SkillMessageKind.Error);
                return;
            }
            var name = CopyName(row.Name, taken);
            // A copy is a new skill: the person reads it and saves it; nothing is written yet.
            Open(NewEditor(SkillEditorMode.New, read.Document with { Name = name, UnknownKeys = Array.Empty<string>() }, null));
        });
    }

    private static string CopyName(string name, ISet<string> taken)
    {
        var stem = name.Length > SkillLimits.NameMax - 9 ? name.Substring(0, SkillLimits.NameMax - 9).TrimEnd('-') : name;
        var candidate = stem + "-copy";
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = stem + "-copy-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return candidate;
    }

    private async Task ConfirmDeleteAsync()
    {
        var row = _pendingDelete;
        if (row is null)
        {
            return;
        }
        PendingDelete = null;
        var deleted = await _workshop.DeleteAsync(row.Name).ConfigureAwait(false);
        Ui(() => Say(deleted ? _strings().SkillDeleted(row.Name) : _strings().SkillSaveFailed, deleted ? SkillMessageKind.Information : SkillMessageKind.Error));
        await RefreshAsync().ConfigureAwait(false);
    }

    private async Task ToggleAsync(SkillRowViewModel? row)
    {
        if (row is null || !row.CanSwitch)
        {
            return;
        }
        var on = !row.IsEnabled;
        row.IsEnabled = on;
        row.Retext();
        await _workshop.SetEnabledAsync(row.Name, on).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    private async Task ImportAsync()
    {
        if (_files is null)
        {
            return;
        }
        var path = await _files.PickImportAsync(CancellationToken.None).ConfigureAwait(false);
        if (path is not null)
        {
            await ImportFromAsync(path).ConfigureAwait(false);
        }
    }

    /// <summary>Checks a folder or a zip (also for a dropped one); writes nothing. The result waits in <see cref="PendingImport"/> for the person's confirmation.</summary>
    public async Task ImportFromAsync(string path)
    {
        var version = Interlocked.Increment(ref _importVersion);
        _importBusy = true;
        ImportCommand.NotifyCanExecuteChanged();
        ImportAnalysis analysis;
        try
        {
            analysis = await _workshop.AnalyzeImportAsync(path).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            analysis = new ImportAnalysis(path, null, new[] { new SkillIssue(SkillIssueCode.ReadFailed, SkillSeverity.Error, path) }, false, 0, 0);
        }
        Ui(() =>
        {
            if (version != _importVersion)
            {
                return;
            }
            _importBusy = false;
            SetImport(analysis);
            ImportCommand.NotifyCanExecuteChanged();
        });
    }

    private void SetImport(ImportAnalysis? analysis)
    {
        _pendingImport = analysis;
        _importIssues = analysis is null ? Array.Empty<SkillIssueViewModel>() : analysis.Issues.Select(i => new SkillIssueViewModel(i, _strings())).ToList();
        OnPropertyChanged(nameof(PendingImport));
        OnPropertyChanged(nameof(ImportIssues));
        OnPropertyChanged(nameof(CanConfirmImport));
        OnPropertyChanged(nameof(ImportReplaces));
        OnPropertyChanged(nameof(ImportTitle));
        OnPropertyChanged(nameof(ImportBody));
        ConfirmImportCommand.NotifyCanExecuteChanged();
        CancelImportCommand.NotifyCanExecuteChanged();
    }

    private void CancelImport()
    {
        Interlocked.Increment(ref _importVersion); // an analysis still running is stale now
        _importBusy = false;
        SetImport(null);
        ImportCommand.NotifyCanExecuteChanged();
    }

    private async Task ConfirmImportAsync()
    {
        var analysis = _pendingImport;
        if (analysis is not { Ok: true })
        {
            return;
        }
        _importBusy = true;
        ConfirmImportCommand.NotifyCanExecuteChanged();
        // Pressing Import is the person's confirmation, also to replace a skill of the same name.
        var result = await _workshop.CommitImportAsync(analysis, analysis.WouldReplace).ConfigureAwait(false);
        Ui(() =>
        {
            _importBusy = false;
            if (result.Saved)
            {
                SetImport(null);
                Say(_strings().SkillImported(result.Name ?? ""));
            }
            else if (result.Status == SkillSaveStatus.Refused)
            {
                SetImport(analysis with { Issues = result.Issues });
            }
            else
            {
                Say(_strings().SkillSaveFailed, SkillMessageKind.Error);
            }
            ImportCommand.NotifyCanExecuteChanged();
        });
        if (result.Saved)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private async Task ExportAsync(SkillRowViewModel? row)
    {
        if (row is null || _files is null)
        {
            return;
        }
        var zip = await _workshop.ExportAsync(row.Name).ConfigureAwait(false);
        var written = zip is not null && await _files.SaveExportAsync(row.Name + ".zip", zip, CancellationToken.None).ConfigureAwait(false);
        Ui(() => Say(zip is null ? _strings().SkillExportFailed(row.Name) : written ? _strings().SkillExported(row.Name) : "", zip is null ? SkillMessageKind.Error : SkillMessageKind.Information));
    }
}
