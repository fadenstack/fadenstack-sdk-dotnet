using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.SkillAuthoring;
using Fadenstack.UI.Markdown;
using Fadenstack.UI.Services;
using Microsoft.Extensions.AI;

namespace Fadenstack.UI.Skills;

/// <summary>What the editor is for.</summary>
public enum SkillEditorMode
{
    /// <summary>A skill that does not exist yet: a new one, or a copy.</summary>
    New,

    /// <summary>One of the person's own skills.</summary>
    Edit,

    /// <summary>A bundled skill: read only.</summary>
    View,

    /// <summary>A model's draft: the person reads it first; it is not saved until they press Save.</summary>
    Proposal,
}

/// <summary>What the model would do with a prompt (the "ask the model once" step).</summary>
public enum SkillModelAnswer
{
    NotAsked,
    Asking,
    LoadsThis,
    LoadsOther,
    LoadsNone,
    Failed,
}

/// <summary>The editor was closed: by Save (<see cref="Saved"/>) or by Cancel.</summary>
public sealed class SkillEditorClosedEventArgs : EventArgs
{
    public SkillEditorClosedEventArgs(bool saved, string? name)
    {
        Saved = saved;
        Name = name;
    }

    public bool Saved { get; }

    public string? Name { get; }
}

/// <summary>A file of the skill, in the editor's list.</summary>
public sealed class SkillResourceViewModel : ObservableObject
{
    private string _newPath;

    internal SkillResourceViewModel(SkillFile file, SkillEditorViewModel owner)
    {
        File = file;
        _newPath = file.Path;
        RenameCommand = new RelayCommand(() => owner.RenameResource(this, _newPath), () => !owner.IsReadOnly && _newPath != file.Path);
        RemoveCommand = new RelayCommand(() => owner.RemoveResource(this), () => !owner.IsReadOnly);
    }

    internal SkillFile File { get; }

    public string Path => File.Path;

    public long Size => File.Length;

    /// <summary>Text the person can read in the skill (not a picture or another binary).</summary>
    public bool IsText => File.Text is not null;

    /// <summary>The name being typed for a rename.</summary>
    public string NewPath
    {
        get => _newPath;
        set
        {
            if (SetProperty(ref _newPath, value ?? ""))
            {
                RenameCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IRelayCommand RenameCommand { get; }

    public IRelayCommand RemoveCommand { get; }
}

/// <summary>An activation the person can pick, with its words.</summary>
public sealed record SkillActivationOption(string Value, string Label);

/// <summary>
/// The skill editor (sdk/51 F04): the name and description with the checks as you type, the instructions in Markdown with a preview, the
/// <c>faden.yaml</c> fields, the resource files, Save (enabled only when the checks pass), Cancel, and Test. A skill's text is untrusted
/// input, so <b>nothing is saved except by Save</b>: not a draft, not an import, not what the test step does. Test runs the module's
/// pre-selection over the edited skill and, if the app gave a model, asks it once whether it would call <c>load_skill</c>: one model call,
/// and no tool runs. Platform-neutral: the skins draw it.
/// </summary>
public sealed class SkillEditorViewModel : ObservableObject
{
    private readonly ISkillWorkshop _workshop;
    private readonly ISkillFileHost? _files;
    private readonly IChatClient? _model;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<UiStrings> _strings;
    private readonly IReadOnlyList<SkillIssue> _readIssues;
    private readonly IReadOnlyDictionary<string, string> _metadata;
    private readonly string _allowedTools;
    private readonly SkillExtensionFields _kept;
    private readonly List<SkillFile> _resources = new();
    private IReadOnlyList<string> _unknownKeys;
    private string? _originalName;
    private SkillEditorMode _mode;
    private string _name;
    private string _description;
    private string _license;
    private string _compatibility;
    private string _instructions;
    private string _extensionTitle;
    private string _tagsText;
    private string _keywordsText;
    private string _fileTypesText;
    private string _preferredToolsText;
    private int _priority;
    private string _activation;
    private bool _dirty;
    private bool _saving;
    private bool _needsReplace;
    private bool _showPreview;
    private string _message = "";
    private int _saveVersion;
    private int _askVersion;
    private SkillCheck _check = new(Array.Empty<SkillIssue>());
    private IReadOnlyList<SkillIssueViewModel> _issues = Array.Empty<SkillIssueViewModel>();
    private string _testPrompt = "";
    private SkillTestResult? _test;
    private SkillModelAnswer _answer;
    private string _answerSkill = "";
    private string? _previewFor;
    private IReadOnlyList<MarkdownBlock> _preview = Array.Empty<MarkdownBlock>();

    internal SkillEditorViewModel(ISkillWorkshop workshop, SkillEditorMode mode, SkillDocument document, string? originalName,
        IReadOnlyList<SkillIssue>? readIssues, ISkillFileHost? files, IChatClient? model, IUiDispatcher? dispatcher, Func<UiStrings>? strings)
    {
        _workshop = workshop ?? throw new ArgumentNullException(nameof(workshop));
        _files = files;
        _model = model;
        _dispatcher = dispatcher ?? InlineDispatcher.Instance;
        var current = UiStrings.Current;
        _strings = strings ?? (() => current);
        _mode = mode;
        _originalName = originalName;
        _readIssues = (readIssues ?? Array.Empty<SkillIssue>()).Where(i => i.IsError && i.Code != SkillIssueCode.UnknownKey && IsReadProblem(i.Code)).ToList();

        _name = document.Name;
        _description = document.Description;
        _license = document.License;
        _compatibility = document.Compatibility;
        _instructions = document.Instructions;
        _metadata = document.Metadata;
        _allowedTools = document.AllowedTools;
        _unknownKeys = document.UnknownKeys;
        _kept = document.Extension;
        _extensionTitle = document.Extension.Title;
        _tagsText = Join(document.Extension.Tags);
        _keywordsText = Join(document.Extension.Keywords);
        _fileTypesText = Join(document.Extension.FileTypes);
        _preferredToolsText = Join(document.Extension.PreferredTools);
        _priority = document.Extension.Priority;
        _activation = document.Extension.Activation;
        _resources.AddRange(document.Resources);
        Resources = new ObservableCollection<SkillResourceViewModel>(_resources.Select(r => new SkillResourceViewModel(r, this)));
        _dirty = mode == SkillEditorMode.Proposal;

        SaveCommand = new AsyncRelayCommand(() => SaveAsync(false), () => CanSave);
        ConfirmReplaceCommand = new AsyncRelayCommand(() => SaveAsync(true), () => _needsReplace && !_saving);
        DismissReplaceCommand = new RelayCommand(() => NeedsReplaceConfirmation = false, () => _needsReplace);
        CancelCommand = new RelayCommand(Cancel);
        TogglePreviewCommand = new RelayCommand(() => ShowPreview = !_showPreview);
        AddResourceCommand = new AsyncRelayCommand(AddResourceAsync, () => !IsReadOnly && _files is not null);
        DropUnknownKeysCommand = new RelayCommand(DropUnknownKeys, () => !IsReadOnly && _unknownKeys.Count > 0);
        RunTestCommand = new RelayCommand(RunTest);
        AskModelCommand = new AsyncRelayCommand(AskModelAsync, () => _model is not null && _testPrompt.Trim().Length > 0 && _answer != SkillModelAnswer.Asking);
        Recheck();
    }

    private static bool IsReadProblem(SkillIssueCode code) =>
        code is SkillIssueCode.FileTooLarge or SkillIssueCode.TooManyFiles or SkillIssueCode.TotalTooLarge or SkillIssueCode.ReadFailed or SkillIssueCode.LinkEntry;

    private static string Join(IReadOnlyList<string> items) => string.Join(", ", items);

    private static IReadOnlyList<string> Split(string text) =>
        text.Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The editor was closed, by Save or by Cancel.</summary>
    public event EventHandler<SkillEditorClosedEventArgs>? Closed;

    public SkillEditorMode Mode => _mode;

    public bool IsReadOnly => _mode == SkillEditorMode.View;

    /// <summary>A model's draft that has not been saved: the skin marks it clearly.</summary>
    public bool IsProposal => _mode == SkillEditorMode.Proposal;

    /// <summary>The heading: "New skill", "Editing x", or that a bundled skill is read only.</summary>
    public string Heading => _mode switch
    {
        SkillEditorMode.View => _strings().SkillEditorReadOnly(_name),
        SkillEditorMode.Edit => _strings().SkillEditorEditing(_originalName ?? _name),
        _ => _strings().SkillNew,
    };

    /// <summary>For a proposal: that it is one, and that nothing is saved until Save.</summary>
    public string ProposalNote => IsProposal ? _strings().SkillEditorProposal : "";

    public string Name
    {
        get => _name;
        set => Change(ref _name, value ?? "");
    }

    public string Description
    {
        get => _description;
        set => Change(ref _description, value ?? "");
    }

    public string License
    {
        get => _license;
        set => Change(ref _license, value ?? "");
    }

    public string Compatibility
    {
        get => _compatibility;
        set => Change(ref _compatibility, value ?? "");
    }

    /// <summary>The instructions, Markdown.</summary>
    public string Instructions
    {
        get => _instructions;
        set
        {
            if (Change(ref _instructions, value ?? ""))
            {
                OnPropertyChanged(nameof(PreviewBlocks));
            }
        }
    }

    public string ExtensionTitle
    {
        get => _extensionTitle;
        set => Change(ref _extensionTitle, value ?? "");
    }

    /// <summary>One per line, or separated by commas.</summary>
    public string TagsText
    {
        get => _tagsText;
        set => Change(ref _tagsText, value ?? "");
    }

    public string KeywordsText
    {
        get => _keywordsText;
        set => Change(ref _keywordsText, value ?? "");
    }

    public string FileTypesText
    {
        get => _fileTypesText;
        set => Change(ref _fileTypesText, value ?? "");
    }

    public string PreferredToolsText
    {
        get => _preferredToolsText;
        set => Change(ref _preferredToolsText, value ?? "");
    }

    /// <summary>0 to 100 (the module ranks by it).</summary>
    public int Priority
    {
        get => _priority;
        set => Change(ref _priority, value);
    }

    /// <summary><c>auto</c>, <c>always</c> or <c>manual</c>.</summary>
    public string Activation
    {
        get => _activation;
        set => Change(ref _activation, value ?? "auto");
    }

    public IReadOnlyList<SkillActivationOption> ActivationOptions =>
        new[] { "auto", "always", "manual" }.Select(v => new SkillActivationOption(v, _strings().SkillActivationLabel(v))).ToList();

    /// <summary>The files beside SKILL.md.</summary>
    public ObservableCollection<SkillResourceViewModel> Resources { get; }

    public bool HasResources => Resources.Count > 0;

    /// <summary>What the checks found (errors and warnings), as you type.</summary>
    public IReadOnlyList<SkillIssueViewModel> Issues => _issues;

    public bool HasIssues => _issues.Count > 0;

    public bool HasErrors => _check.HasErrors || _readIssues.Count > 0;

    /// <summary>Save is on only when every check passes.</summary>
    public bool CanSave => !IsReadOnly && !HasErrors && !_saving;

    public bool IsDirty => _dirty;

    public bool IsSaving => _saving;

    /// <summary>The name is taken by another skill: Save asks first, and replaces it only after the person confirms.</summary>
    public bool NeedsReplaceConfirmation
    {
        get => _needsReplace;
        private set
        {
            if (SetProperty(ref _needsReplace, value))
            {
                OnPropertyChanged(nameof(ReplaceTitle));
                ConfirmReplaceCommand.NotifyCanExecuteChanged();
                DismissReplaceCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ReplaceTitle => _needsReplace ? _strings().SkillExistsTitle(_name) : "";

    public string ReplaceBody => _strings().SkillExistsBody;

    /// <summary>The frontmatter has fields beyond the six; they stop the save until removed.</summary>
    public bool HasUnknownKeys => _unknownKeys.Count > 0;

    /// <summary>What the last action said, e.g. that the skill could not be written.</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool ShowPreview
    {
        get => _showPreview;
        set
        {
            if (SetProperty(ref _showPreview, value))
            {
                OnPropertyChanged(nameof(PreviewBlocks));
            }
        }
    }

    /// <summary>The instructions as the chat's Markdown model (its blocks), when the preview is on.</summary>
    public IReadOnlyList<MarkdownBlock> PreviewBlocks
    {
        get
        {
            if (!_showPreview)
            {
                return Array.Empty<MarkdownBlock>();
            }
            if (_previewFor != _instructions)
            {
                _preview = MarkdownParser.Parse(_instructions);
                _previewFor = _instructions;
            }
            return _preview;
        }
    }

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand ConfirmReplaceCommand { get; }

    public IRelayCommand DismissReplaceCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand TogglePreviewCommand { get; }

    public IAsyncRelayCommand AddResourceCommand { get; }

    /// <summary>Removes the fields beyond the six from the document (they are gone from the next Save).</summary>
    public IRelayCommand DropUnknownKeysCommand { get; }

    public IRelayCommand RunTestCommand { get; }

    public IAsyncRelayCommand AskModelCommand { get; }

    // -- Test ------------------------------------------------------------------------------------------

    /// <summary>The message to try the pre-selection with.</summary>
    public string TestPrompt
    {
        get => _testPrompt;
        set
        {
            if (SetProperty(ref _testPrompt, value ?? ""))
            {
                AskModelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasTestResult => _test is not null;

    /// <summary>The skills the model is told about for the prompt.</summary>
    public IReadOnlyList<string> TestListed => _test?.Listed ?? Array.Empty<string>();

    /// <summary>The skills that matched, each with how ("named in the message", "matches its keywords", "always on").</summary>
    public IReadOnlyList<string> TestMatches =>
        _test?.Matches.Select(m => m.Name + " — " + _strings().SkillMatchLabel(m.MatchedBy)).ToList() ?? new List<string>();

    /// <summary>The skills whose instructions are loaded without the model asking.</summary>
    public IReadOnlyList<string> TestPreloaded => _test?.Preloaded ?? Array.Empty<string>();

    /// <summary>The edited skill was listed, matched or pre-loaded.</summary>
    public bool TestIncludesThisSkill => _test?.EditedListed == true;

    /// <summary>What the test says in words when there is nothing to list, or when the edited skill is not offered.</summary>
    public string TestNote => _test is null ? "" : _test.Listed.Count == 0 ? _strings().SkillTestNone : !_test.EditedListed ? _strings().SkillTestEditedNotListed : "";

    /// <summary>The model can be asked (the app gave one).</summary>
    public bool CanAskModel => _model is not null;

    public SkillModelAnswer ModelAnswer => _answer;

    /// <summary>The model's answer in words; empty before it was asked.</summary>
    public string ModelAnswerText => _answer switch
    {
        SkillModelAnswer.Asking => _strings().SkillModelAsking,
        SkillModelAnswer.LoadsThis => _strings().SkillModelWouldLoad(_answerSkill),
        SkillModelAnswer.LoadsOther => _strings().SkillModelWouldLoadOther(_answerSkill),
        SkillModelAnswer.LoadsNone => _strings().SkillModelWouldNot,
        SkillModelAnswer.Failed => _strings().SkillModelFailed,
        _ => "",
    };

    public string ModelNote => _strings().SkillModelNote;

    /// <summary>The pre-selection for <see cref="TestPrompt"/> over the edited skill and the person's others. No model, no tool.</summary>
    public void RunTest()
    {
        _test = _workshop.Test(_testPrompt, BuildDocument(), _originalName);
        OnPropertyChanged(nameof(HasTestResult));
        OnPropertyChanged(nameof(TestListed));
        OnPropertyChanged(nameof(TestMatches));
        OnPropertyChanged(nameof(TestPreloaded));
        OnPropertyChanged(nameof(TestIncludesThisSkill));
        OnPropertyChanged(nameof(TestNote));
    }

    private async Task AskModelAsync()
    {
        if (_model is null || _testPrompt.Trim().Length == 0)
        {
            return;
        }
        var version = Interlocked.Increment(ref _askVersion);
        RunTest();
        SetAnswer(SkillModelAnswer.Asking, "");
        var instructions = _test?.ModelInstructions;
        if (instructions is null)
        {
            SetAnswer(SkillModelAnswer.LoadsNone, ""); // no skill is listed: there is nothing to call, so there is nothing to ask
            return;
        }
        var answer = SkillModelAnswer.Failed;
        var skill = "";
        try
        {
            var options = new ChatOptions
            {
                // The one tool the model is offered, as a declaration: it cannot run, and no function-invoking layer has anything to call.
                Tools = new List<AITool> { AIFunctionFactory.CreateDeclaration(SkillTestResult.LoadSkillTool, "Loads a skill's instructions.", LoadSchema) },
                ToolMode = ChatToolMode.Auto,
                AllowMultipleToolCalls = false,
            };
            var messages = new List<ChatMessage> { new(ChatRole.System, instructions), new(ChatRole.User, _testPrompt) };
            var response = await _model.GetResponseAsync(messages, options).ConfigureAwait(false);
            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
                .Where(c => string.Equals(c.Name, SkillTestResult.LoadSkillTool, StringComparison.Ordinal)).ToList();
            skill = calls.Select(SkillOf).FirstOrDefault(n => n.Length > 0) ?? "";
            answer = calls.Count == 0 ? SkillModelAnswer.LoadsNone
                : string.Equals(skill, _name, StringComparison.Ordinal) ? SkillModelAnswer.LoadsThis : SkillModelAnswer.LoadsOther;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            answer = SkillModelAnswer.Failed;
        }
        if (version == _askVersion)
        {
            Ui(() =>
            {
                if (version == _askVersion)
                {
                    SetAnswer(answer, skill);
                }
            });
        }
    }

    private static readonly JsonElement LoadSchema = JsonDocument.Parse(
        "{\"type\":\"object\",\"properties\":{\"skillName\":{\"type\":\"string\",\"description\":\"The name of the skill.\"}},\"required\":[\"skillName\"]}").RootElement.Clone();

    private static string SkillOf(FunctionCallContent call)
    {
        if (call.Arguments is not null && call.Arguments.TryGetValue("skillName", out var value))
        {
            return value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() ?? "" : value?.ToString() ?? "";
        }
        return "";
    }

    private void SetAnswer(SkillModelAnswer answer, string skill)
    {
        _answer = answer;
        _answerSkill = skill;
        OnPropertyChanged(nameof(ModelAnswer));
        OnPropertyChanged(nameof(ModelAnswerText));
        AskModelCommand.NotifyCanExecuteChanged();
    }

    // -- editing ---------------------------------------------------------------------------------------

    private bool Change<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (IsReadOnly || !SetProperty(ref field, value, name))
        {
            return false;
        }
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
        if (name is nameof(Name))
        {
            OnPropertyChanged(nameof(Heading));
        }
        Recheck();
        return true;
    }

    /// <summary>The skill as the fields now say it.</summary>
    public SkillDocument BuildDocument() => new()
    {
        Name = _name.Trim(),
        Description = _description.Trim(),
        License = _license.Trim(),
        Compatibility = _compatibility.Trim(),
        Metadata = _metadata,
        AllowedTools = _allowedTools,
        Instructions = _instructions,
        UnknownKeys = _unknownKeys,
        Resources = _resources.ToList(),
        Extension = _kept with
        {
            Title = _extensionTitle.Trim(),
            Tags = Split(_tagsText),
            Keywords = Split(_keywordsText),
            FileTypes = Split(_fileTypesText),
            PreferredTools = Split(_preferredToolsText),
            Priority = _priority,
            Activation = _activation,
        },
    };

    private void Recheck()
    {
        _check = _workshop.Check(BuildDocument());
        var all = _readIssues.Concat(_check.Issues).ToList();
        var strings = _strings();
        _issues = all.OrderByDescending(i => i.IsError).Select(i => new SkillIssueViewModel(i, strings)).ToList();
        OnPropertyChanged(nameof(Issues));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(HasUnknownKeys));
        SaveCommand.NotifyCanExecuteChanged();
        DropUnknownKeysCommand.NotifyCanExecuteChanged();
        if (_needsReplace)
        {
            NeedsReplaceConfirmation = false; // the name changed: ask again at the next Save
        }
    }

    /// <summary>The app's language changed: the texts are said in the new one.</summary>
    public void Retext()
    {
        Recheck();
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(ProposalNote));
        OnPropertyChanged(nameof(ActivationOptions));
        OnPropertyChanged(nameof(TestMatches));
        OnPropertyChanged(nameof(TestNote));
        OnPropertyChanged(nameof(ModelAnswerText));
        OnPropertyChanged(nameof(ModelNote));
        OnPropertyChanged(nameof(ReplaceTitle));
        OnPropertyChanged(nameof(ReplaceBody));
    }

    private void DropUnknownKeys()
    {
        _unknownKeys = Array.Empty<string>();
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
        Recheck();
    }

    // -- the files ---------------------------------------------------------------------------------------

    private async Task AddResourceAsync()
    {
        if (_files is null || IsReadOnly)
        {
            return;
        }
        var file = await _files.PickResourceAsync(CancellationToken.None).ConfigureAwait(false);
        if (file is not null)
        {
            Ui(() => AddResource(file));
        }
    }

    /// <summary>Adds a file (one with the same path is replaced). Whether its path and kind are allowed is for the checks.</summary>
    public void AddResource(SkillFile file)
    {
        if (IsReadOnly || file is null)
        {
            return;
        }
        var normalized = file with { Path = (file.Path ?? "").Replace('\\', '/') };
        var existing = _resources.FindIndex(r => string.Equals(r.Path, normalized.Path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            _resources[existing] = normalized;
        }
        else
        {
            _resources.Add(normalized);
        }
        ResourcesChanged();
    }

    internal void RenameResource(SkillResourceViewModel row, string newPath)
    {
        var index = _resources.IndexOf(row.File);
        if (IsReadOnly || index < 0)
        {
            return;
        }
        _resources[index] = row.File with { Path = (newPath ?? "").Trim().Replace('\\', '/') };
        ResourcesChanged();
    }

    internal void RemoveResource(SkillResourceViewModel row)
    {
        if (!IsReadOnly && _resources.Remove(row.File))
        {
            ResourcesChanged();
        }
    }

    private void ResourcesChanged()
    {
        Resources.Clear();
        foreach (var file in _resources)
        {
            Resources.Add(new SkillResourceViewModel(file, this));
        }
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasResources));
        Recheck();
    }

    // -- Save and Cancel -----------------------------------------------------------------------------------

    private async Task SaveAsync(bool replace)
    {
        if (IsReadOnly || HasErrors || _saving)
        {
            return;
        }
        var document = BuildDocument();
        var version = Interlocked.Increment(ref _saveVersion);
        _saving = true;
        NeedsReplaceConfirmation = false;
        Message = "";
        OnPropertyChanged(nameof(IsSaving));
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
        SkillSaveResult result;
        try
        {
            // Only here, because the person pressed Save (or confirmed the replacement).
            result = await _workshop.SaveAsync(document, _originalName, replace).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            result = new SkillSaveResult(SkillSaveStatus.Failed, Array.Empty<SkillIssue>(), document.Name);
        }
        Ui(() =>
        {
            if (version != _saveVersion)
            {
                return;
            }
            _saving = false;
            OnPropertyChanged(nameof(IsSaving));
            switch (result.Status)
            {
                case SkillSaveStatus.Saved:
                    _originalName = document.Name;
                    _mode = SkillEditorMode.Edit;
                    _dirty = false;
                    OnPropertyChanged(nameof(Mode));
                    OnPropertyChanged(nameof(IsProposal));
                    OnPropertyChanged(nameof(ProposalNote));
                    OnPropertyChanged(nameof(Heading));
                    OnPropertyChanged(nameof(IsDirty));
                    Message = _strings().SkillSaved(document.Name);
                    Closed?.Invoke(this, new SkillEditorClosedEventArgs(true, document.Name));
                    break;
                case SkillSaveStatus.Exists:
                    NeedsReplaceConfirmation = true;
                    break;
                case SkillSaveStatus.Refused:
                    Recheck();
                    break;
                default:
                    Message = _strings().SkillSaveFailed;
                    break;
            }
            OnPropertyChanged(nameof(CanSave));
            SaveCommand.NotifyCanExecuteChanged();
        });
    }

    private void Cancel()
    {
        Interlocked.Increment(ref _saveVersion); // a save still running no longer closes anything
        Interlocked.Increment(ref _askVersion);
        Closed?.Invoke(this, new SkillEditorClosedEventArgs(false, null));
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

    /// <summary>The instructions' size in tokens as the advisory limit counts them (4 characters each), for a counter under the field.</summary>
    public int InstructionsTokens => _instructions.Length / SkillLimits.CharactersPerToken;
}
