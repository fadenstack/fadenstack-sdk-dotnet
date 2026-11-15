using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.LocalTools;

namespace Fadenstack.UI.Tools;

/// <summary>One server found in another program's file, with a tick box.</summary>
public sealed partial class ImportRowViewModel : ObservableObject
{
    private readonly Action _changed;

    internal ImportRowViewModel(UiStrings strings, ImportCandidateInfo info, Action changed)
    {
        Label = info.Label;
        Where = info.Where;
        OriginalName = info.OriginalName;
        Notes = info.Notes.Select(n => NoteText(strings, n, info.Detail)).ToList();
        TickName = strings.ToolsImportTick(info.Label);
        _changed = changed;
    }

    public string Label { get; }

    public string Where { get; }

    public string OriginalName { get; }

    public string TickName { get; }

    /// <summary>What the person should know before they tick it.</summary>
    public IReadOnlyList<string> Notes { get; }

    public bool HasNotes => Notes.Count > 0;

    [ObservableProperty]
    private bool _isTicked;

    partial void OnIsTickedChanged(bool value) => _changed();

    private static string NoteText(UiStrings s, ImportNote note, string? detail) => note switch
    {
        ImportNote.NeedsValue => s.ToolsImportNoteNeedsValue,
        ImportNote.HasPlaceholders => s.ToolsImportNotePlaceholders,
        ImportNote.IgnoredFields => s.ToolsImportNoteIgnored(detail ?? ""),
        ImportNote.Renamed => s.ToolsImportNoteRenamed,
        _ => s.ToolsImportNoteWasOff,
    };
}

/// <summary>A server of the file that is not offered, and why.</summary>
public sealed class SkippedRowViewModel
{
    internal SkippedRowViewModel(UiStrings strings, ImportSkipInfo info)
    {
        Name = info.Name;
        Reason = info.Reason switch
        {
            ImportSkipReason.NetworkServer => strings.ToolsSkipNetwork,
            ImportSkipReason.NothingToStart => strings.ToolsSkipNothing,
            ImportSkipReason.NotValid => strings.ToolsSkipNotValid(info.Detail ?? ""),
            _ => strings.ToolsSkipNotUnderstood,
        };
    }

    public string Name { get; }

    public string Reason { get; }
}

/// <summary>
/// The import sheet: the person picks Claude Desktop, VS Code, Cursor or a .mcp.json file; the servers found come with a tick box
/// each, the skipped ones with their reasons. Nothing is copied until they tick servers and press Import, and an import never turns
/// on the plain-http switch or marks a server trusted.
/// </summary>
public sealed partial class ImportSheetViewModel : ObservableObject, IToolsTexts, IRetext
{
    private readonly ToolsViewModel _page;
    private ImportPreview? _preview;

    internal ImportSheetViewModel(ToolsViewModel page)
    {
        _page = page;
        ReadClaudeCommand = new AsyncRelayCommand(() => ReadAsync(ImportSourceKind.ClaudeDesktop));
        ReadVsCodeCommand = new AsyncRelayCommand(() => ReadAsync(ImportSourceKind.VsCode));
        ReadCursorCommand = new AsyncRelayCommand(() => ReadAsync(ImportSourceKind.Cursor));
        ReadFileCommand = new AsyncRelayCommand(ReadFileAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => Candidates.Any(c => c.IsTicked));
        CancelCommand = new RelayCommand(page.CloseSheet);
    }

    public UiStrings Strings => _page.Strings;

    public ObservableCollection<ImportRowViewModel> Candidates { get; } = new ObservableCollection<ImportRowViewModel>();

    public ObservableCollection<SkippedRowViewModel> Skipped { get; } = new ObservableCollection<SkippedRowViewModel>();

    public bool HasSkipped => Skipped.Count > 0;

    /// <summary>"Read ...", or why the file could not be used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "";

    public bool HasStatus => StatusText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _resultText = "";

    public bool HasResult => ResultText.Length > 0;

    public IAsyncRelayCommand ReadClaudeCommand { get; }

    public IAsyncRelayCommand ReadVsCodeCommand { get; }

    public IAsyncRelayCommand ReadCursorCommand { get; }

    public IAsyncRelayCommand ReadFileCommand { get; }

    public IAsyncRelayCommand ImportCommand { get; }

    public IRelayCommand CancelCommand { get; }

    private async Task ReadFileAsync()
    {
        if (_page.Files is not { } files)
        {
            return;
        }
        var path = await files.PickFileAsync(Strings.ToolsChooseFileTitle, null, CancellationToken.None);
        if (path is not null)
        {
            await ReadAsync(ImportSourceKind.ProjectFile, path);
        }
    }

    private async Task ReadAsync(ImportSourceKind source, string? path = null)
    {
        var s = Strings;
        Candidates.Clear();
        Skipped.Clear();
        ResultText = "";
        _preview = await _page.Catalog.ReadImportAsync(source, path);
        StatusText = _preview.Problem switch
        {
            ImportProblem.None => s.ToolsImportRead(System.IO.Path.GetFileName(_preview.Path ?? "")),
            ImportProblem.NotFound => s.ToolsImportProblemNotFound,
            ImportProblem.Unreadable => s.ToolsImportProblemUnreadable,
            ImportProblem.NoServerList => s.ToolsImportProblemNoList,
            _ => s.ToolsImportProblemTooLarge,
        };
        foreach (var server in _preview.Servers)
        {
            Candidates.Add(new ImportRowViewModel(s, server, () => ImportCommand.NotifyCanExecuteChanged()));
        }
        foreach (var skip in _preview.Skipped)
        {
            Skipped.Add(new SkippedRowViewModel(s, skip));
        }
        if (_preview.Problem == ImportProblem.None && Candidates.Count == 0)
        {
            ResultText = s.ToolsImportNothingToAdd;
        }
        OnPropertyChanged(nameof(HasSkipped));
        ImportCommand.NotifyCanExecuteChanged();
    }

    private async Task ImportAsync()
    {
        if (_preview is null)
        {
            return;
        }
        var labels = Candidates.Where(c => c.IsTicked).Select(c => c.Label).ToList();
        var added = await _page.Catalog.ImportAsync(_preview, labels);
        ResultText = Strings.ToolsImported(added);
        Candidates.Clear();
        _page.Refresh();
    }

    public void Retext() => OnPropertyChanged(nameof(Strings));
}
