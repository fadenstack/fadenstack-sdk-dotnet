using System.Text;
using Fadenstack.Models;
using Fadenstack.SkillAuthoring;

namespace Fadenstack.Skills;

/// <summary>
/// The skill editor's side of the module (sdk/51 F04): the person's skills as folders in their own folder (<see cref="SkillsModule.OwnFolder"/>), with the
/// bundled skills (read only), the organisation's (governed mode: read only, never their instructions), the on/off switches, the checks, the
/// atomic save, import and export, and the test step over the module's own pre-selection. It writes only when the editor calls
/// <see cref="SaveAsync"/>, <see cref="CommitImportAsync"/>, <see cref="DeleteAsync"/> or <see cref="SetEnabledAsync"/>, which the person
/// started; nothing here saves on its own, and no skill text changes a tool's class, a mode or a grant.
/// </summary>
public sealed class SkillWorkshop : ISkillWorkshop
{
    private readonly SkillFolderStore? _fixed;
    private readonly SkillsModule? _module;
    private readonly SkillsOptions _options;
    private readonly ISkillSwitches? _switches;
    private SkillFolderStore? _current;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SkillOption>>>? _organisation;
    private readonly Action? _changed;
    private readonly HashSet<string> _reserved;

    /// <summary>
    /// The editor over the module's person: their own folder and switches (<see cref="SkillsModule.OwnFolder"/>,
    /// <see cref="SkillsModule.Switches"/>), as the profile names them, and the module's bundled skills. Until the agent has started,
    /// whose they are is not known: the list holds the bundled skills only, and a change throws.
    /// </summary>
    /// <param name="module">The agent's skills module; it is told to read the folders again after a change.</param>
    /// <param name="organisation">The organisation's skills in governed mode (<c>FadenstackAgent.ListSkillsAsync</c>); null: none are listed.</param>
    public SkillWorkshop(SkillsModule module, Func<CancellationToken, Task<IReadOnlyList<SkillOption>>>? organisation = null)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
        _options = module.Options;
        _organisation = organisation;
        _changed = module.Refresh;
        _reserved = new HashSet<string>(_options.Bundled.Select(b => b.Name), StringComparer.Ordinal);
    }

    /// <summary>The editor over a folder the app chooses (an app's own place, a test).</summary>
    /// <param name="options">The bundled skills (<see cref="SkillsOptions.Bundled"/>) and the switches (<see cref="SkillsOptions.Switches"/>; in memory when null).</param>
    /// <param name="folder">The skills folder.</param>
    /// <param name="organisation">The organisation's skills in governed mode (<c>FadenstackAgent.ListSkillsAsync</c>); null: none are listed.</param>
    /// <param name="changed">Called after a skill was saved, deleted or switched, so that the module reads the folders again (<see cref="SkillsModule.Refresh"/>).</param>
    public SkillWorkshop(SkillsOptions options, string folder, Func<CancellationToken, Task<IReadOnlyList<SkillOption>>>? organisation = null,
        Action? changed = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fixed = new SkillFolderStore(folder ?? throw new ArgumentNullException(nameof(folder)));
        _switches = options.Switches ?? new MemorySkillSwitches();
        _organisation = organisation;
        _changed = changed;
        _reserved = new HashSet<string>(options.Bundled.Select(b => b.Name), StringComparer.Ordinal);
    }

    /// <summary>The person's skills folder; null while the module has not read whose skills these are.</summary>
    public string? Folder => StoreOrNull()?.Root;

    /// <summary>The store now: the folder the app gave, or the module's person's; null while that is not known.</summary>
    private SkillFolderStore? StoreOrNull()
    {
        if (_module is null)
        {
            return _fixed;
        }
        if (_module.OwnFolder is not { } folder)
        {
            return null;
        }
        var current = _current;
        if (current is null || !string.Equals(current.Root, Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal))
        {
            _current = current = new SkillFolderStore(folder);
        }
        return current;
    }

    /// <summary>The store for a change: it must be known whose skills these are.</summary>
    private SkillFolderStore Store => StoreOrNull() ?? throw new InvalidOperationException(
        "Whose skills these are is not known yet: start the agent first, so that the skills module has read its profile.");

    /// <summary>The switches now: the app's or the person's; every skill on while the person is not known.</summary>
    private ISkillSwitches Switches => _module is null ? _switches! : _module.Switches ?? new MemorySkillSwitches();

    public Task<IReadOnlyList<SkillEntry>> ListAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<SkillEntry>>(async () =>
    {
        var entries = new List<SkillEntry>();
        var switches = Switches;
        foreach (var bundled in _options.Bundled.OrderBy(b => b.Name, StringComparer.Ordinal))
        {
            entries.Add(new SkillEntry(bundled.Name, bundled.Description, SkillOrigin.Bundled, switches.IsEnabled(bundled.Name)));
        }
        var store = StoreOrNull();
        foreach (var name in store?.Names() ?? Enumerable.Empty<string>())
        {
            var (text, _, problem) = store!.ReadHead(name);
            var broken = problem is not null || _reserved.Contains(name);
            var reason = problem;
            string? parseError = null;
            var description = "";
            if (!broken && SkillMarkdown.TryParse(text, out var head, out parseError))
            {
                description = head.Description;
                var check = SkillValidator.Check(head with { Instructions = "", Extension = new SkillExtensionFields() }, name, _reserved);
                broken = check.HasErrors;
                reason = check.Issues.FirstOrDefault(i => i.IsError);
            }
            else
            {
                reason ??= _reserved.Contains(name) ? new SkillIssue(SkillIssueCode.NameReserved, SkillSeverity.Error, name)
                    : new SkillIssue(SkillIssueCode.SkillMdUnreadable, SkillSeverity.Error, "SKILL.md", Detail: parseError ?? "");
                broken = true;
            }
            entries.Add(new SkillEntry(name, description, SkillOrigin.Own, switches.IsEnabled(name)) { IsBroken = broken, BrokenIssue = broken ? reason : null });
        }
        if (_organisation is not null)
        {
            try
            {
                var list = await _organisation(cancellationToken).ConfigureAwait(false);
                foreach (var skill in list.OrderBy(s => s.Name, StringComparer.Ordinal))
                {
                    entries.Add(new SkillEntry(skill.Slug.Length > 0 ? skill.Slug : skill.Name, skill.Description, SkillOrigin.Organisation, true));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // the organisation's list could not be read: the person's own skills are still there
            }
        }
        return entries;
    }, cancellationToken);

    public Task<SkillReadResult> ReadAsync(string name, CancellationToken cancellationToken = default) => Task.Run(() => Read(name), cancellationToken);

    private SkillReadResult Read(string name)
    {
        if (_options.Bundled.FirstOrDefault(b => b.Name == name) is { } bundled)
        {
            var document = new SkillDocument
            {
                Name = bundled.Name,
                Description = bundled.Description,
                Instructions = bundled.Instructions,
                Resources = bundled.Resources.Select(r => SkillFile.FromText(r.Key, r.Value)).ToList(),
                Extension = bundled.FadenYaml is { } yaml ? ParseExtension(yaml).Fields ?? new SkillExtensionFields() : new SkillExtensionFields(),
            };
            return new SkillReadResult(document, SkillOrigin.Bundled, Array.Empty<SkillIssue>());
        }
        var store = Store;
        var (text, yamlText, problem) = store.ReadHead(name);
        if (problem is not null || text is null)
        {
            return new SkillReadResult(null, SkillOrigin.Own, new[] { problem ?? new SkillIssue(SkillIssueCode.ReadFailed, SkillSeverity.Error, name) });
        }
        var issues = new List<SkillIssue>();
        if (!SkillMarkdown.TryParse(text, out var parsed, out var error))
        {
            issues.Add(new SkillIssue(SkillIssueCode.SkillMdUnreadable, SkillSeverity.Error, "SKILL.md", Detail: error));
            return new SkillReadResult(null, SkillOrigin.Own, issues);
        }
        var fields = new SkillExtensionFields();
        if (yamlText is not null)
        {
            var extension = ParseExtension(yamlText);
            if (extension.Fields is { } read)
            {
                fields = read;
            }
            // An unreadable faden.yaml is ignored by the module (the skill still works); a Save would replace it, so it is said.
            issues.AddRange(extension.Issues.Select(i => i with { Severity = SkillSeverity.Warning }));
        }
        var (files, fileIssues) = store.ReadResources(name);
        issues.AddRange(fileIssues);
        parsed = parsed with { Extension = fields, Resources = files };
        issues.AddRange(Check(parsed, name).Issues);
        return new SkillReadResult(parsed, SkillOrigin.Own, issues);
    }

    public SkillCheck Check(SkillDocument document, string? folderName = null) => SkillValidator.Check(document, folderName, _reserved);

    public Task<SkillSaveResult> SaveAsync(SkillDocument document, string? previousName = null, bool replaceExisting = false,
        CancellationToken cancellationToken = default) => Task.Run(() => Save(document, previousName, replaceExisting), cancellationToken);

    private SkillSaveResult Save(SkillDocument document, string? previousName, bool replace)
    {
        if (previousName is not null && _reserved.Contains(previousName))
        {
            return new SkillSaveResult(SkillSaveStatus.NotAllowed, Array.Empty<SkillIssue>(), document.Name);
        }
        var check = Check(document);
        if (check.HasErrors)
        {
            return new SkillSaveResult(SkillSaveStatus.Refused, check.Issues, document.Name);
        }
        if (previousName is not null && !SkillFolderStore.ValidName(previousName))
        {
            previousName = null;
        }
        var status = Store.Write(document, previousName, replace);
        if (status == SkillSaveStatus.Saved)
        {
            var switches = Switches;
            if (previousName is not null && previousName != document.Name && !switches.IsEnabled(previousName))
            {
                switches.Set(previousName, true); // the switch follows the skill
                switches.Set(document.Name, false);
            }
            _changed?.Invoke();
        }
        return new SkillSaveResult(status, check.Issues, document.Name);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var store = Store;
        if (_reserved.Contains(name) || !store.Delete(name))
        {
            return false;
        }
        Switches.Set(name, true);
        _changed?.Invoke();
        return true;
    }, cancellationToken);

    public Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var store = Store; // a switch is the person's: whose must be known
        if ((_reserved.Contains(name) || store.Exists(name)) && Switches.Set(name, enabled))
        {
            _changed?.Invoke();
        }
    }, cancellationToken);

    public Task<ImportAnalysis> AnalyzeImportAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => SkillImport.FromPath(path, ParseExtension, Store.Exists, _reserved), cancellationToken);

    public Task<ImportAnalysis> AnalyzeZipAsync(byte[] zip, string source, CancellationToken cancellationToken = default) =>
        Task.Run(() => SkillImport.FromZip(zip, source, ParseExtension, Store.Exists, _reserved), cancellationToken);

    public async Task<SkillSaveResult> CommitImportAsync(ImportAnalysis analysis, bool replaceExisting, CancellationToken cancellationToken = default)
    {
        // An analysis with any error is refused as a whole, even when its document would pass: an unreadable faden.yaml or a
        // refused file is not in the document, and saving the rest would drop it without a word.
        if (analysis is null || !analysis.Ok || analysis.Document is not { } document)
        {
            return new SkillSaveResult(SkillSaveStatus.Refused, analysis?.Issues ?? Array.Empty<SkillIssue>());
        }
        // The analysis is checked again: what is written is what passes now, not what a caller says passed.
        return await SaveAsync(document, null, replaceExisting, cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]?> ExportAsync(string name, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var read = Read(name);
        return read.Document is { } document && !SkillValidator.Check(document, null).HasErrors ? SkillImport.ToZip(document) : (byte[]?)null;
    }, cancellationToken);

    public SkillExtensionParse ParseExtension(string text)
    {
        SkillExtension extension;
        try
        {
            extension = SkillExtension.Parse(text);
        }
        catch (FadenYamlException e)
        {
            return new SkillExtensionParse(null, new[] { new SkillIssue(SkillIssueCode.FadenYamlUnreadable, SkillSeverity.Error, "faden.yaml", Detail: e.Message) });
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return new SkillExtensionParse(null, new[] { new SkillIssue(SkillIssueCode.FadenYamlUnreadable, SkillSeverity.Error, "faden.yaml", Detail: "a keyword could not be prepared") });
        }
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "title", "tags", "priority", "activation", "trigger_rules", "preferred_tools", "allowed_tools", "forbidden_tools", "knowledge_sources",
        };
        var unknown = new List<string>();
        IReadOnlyList<string> allowed = Array.Empty<string>(), forbidden = Array.Empty<string>(), sources = Array.Empty<string>();
        if (FadenYaml.Parse(text) is Dictionary<string, object?> map)
        {
            unknown.AddRange(map.Keys.Where(k => !known.Contains(k)).Select(Short));
            allowed = Strings(map, "allowed_tools");
            forbidden = Strings(map, "forbidden_tools");
            sources = Strings(map, "knowledge_sources");
            if (map.TryGetValue("trigger_rules", out var rules) && rules is Dictionary<string, object?> trigger)
            {
                unknown.AddRange(trigger.Keys.Where(k => k != "keywords" && k != "file_types").Select(k => "trigger_rules." + Short(k)));
            }
        }
        var fields = new SkillExtensionFields
        {
            Title = extension.Title ?? "",
            Tags = extension.Tags,
            Priority = extension.Priority,
            Activation = extension.Activation,
            Keywords = extension.Keywords,
            FileTypes = extension.FileTypes,
            PreferredTools = extension.PreferredTools,
            AllowedTools = allowed,
            ForbiddenTools = forbidden,
            KnowledgeSources = sources,
            UnknownKeys = unknown,
        };
        var issues = unknown.Select(k => new SkillIssue(SkillIssueCode.FadenYamlUnknownKey, SkillSeverity.Warning, "faden.yaml", Detail: k)).ToList();
        return new SkillExtensionParse(fields, issues);
    }

    private static string Short(string key) => key.Length > 40 ? key.Substring(0, 40) : key;

    private static IReadOnlyList<string> Strings(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return Array.Empty<string>();
        }
        if (value is string text)
        {
            return System.Text.RegularExpressions.Regex.Split(text, @"[,\s]+").Where(p => p.Length > 0).ToList();
        }
        return value is List<object?> list ? list.OfType<string>().Select(v => v.Trim()).Where(v => v.Length > 0).ToList() : Array.Empty<string>();
    }

    public SkillTestResult Test(string prompt, SkillDocument? edited, string? editedFrom = null)
    {
        var candidates = new List<SkillCandidate>();
        var switches = Switches;
        var store = StoreOrNull();
        foreach (var bundled in _options.Bundled.Where(b => switches.IsEnabled(b.Name)))
        {
            candidates.Add(new SkillCandidate(bundled.Name, bundled.Description, Extension(bundled.FadenYaml)));
        }
        foreach (var name in (store?.Names() ?? Enumerable.Empty<string>()).Where(n => switches.IsEnabled(n) && !_reserved.Contains(n)))
        {
            var (text, yaml, problem) = store!.ReadHead(name);
            if (problem is null && SkillMarkdown.TryParse(text, out var head, out _) && head.Name == name && head.UnknownKeys.Count == 0
                && !SkillValidator.Check(head with { Instructions = "x", Extension = new SkillExtensionFields() }, name, _reserved).HasErrors)
            {
                candidates.Add(new SkillCandidate(name, head.Description, Extension(yaml)));
            }
        }
        var editedListed = false;
        if (edited is not null)
        {
            // The edited skill stands in for the saved one it came from (and for itself under the same name).
            candidates.RemoveAll(c => c.Name == edited.Name || (editedFrom is not null && c.Name == editedFrom));
            if (edited.Name.Length > 0 && !_reserved.Contains(edited.Name))
            {
                candidates.Add(new SkillCandidate(edited.Name, edited.Description, Extension(SkillMarkdown.WriteExtension(edited.Extension))));
                editedListed = true;
            }
        }
        var query = prompt is { Length: > 20000 } ? prompt.Substring(0, 20000) : prompt;
        var selection = SkillResolver.Select(candidates, new SkillRequest { Query = query }, _options.MaxMatched, _options.MaxNamed, _options.MaxPreloaded);
        var matches = selection.Matches.Select(m => new SkillTestMatch(m.Skill.Name, m.MatchedBy, m.Score)).ToList();
        var listed = selection.Listed.Select(s => s.Name).ToList();
        return new SkillTestResult(listed, matches, selection.Preloaded.Select(m => m.Skill.Name).ToList(), SkillsModule.Render(selection, null),
            editedListed && edited is not null && listed.Contains(edited.Name));
    }

    private static SkillExtension? Extension(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return null;
        }
        try
        {
            return SkillExtension.Parse(yaml!);
        }
        catch (Exception e) when (e is FadenYamlException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return null; // the module ignores a file it cannot read, and the skill still works
        }
    }
}
