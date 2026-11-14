using Fadenstack.Models;
using Fadenstack.SkillAuthoring;
using Fadenstack.Skills;
using Fadenstack.UI.Skills;
using Microsoft.Extensions.AI;

namespace Fadenstack.UI.Tests;

/// <summary>A real workshop on a temporary folder, seen through a spy that counts what was written and can hold an import back.</summary>
internal sealed class SkillRig : IDisposable
{
    public SkillRig(Func<CancellationToken, Task<IReadOnlyList<SkillOption>>>? organisation = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "faden-ui-skills-" + Guid.NewGuid().ToString("N"));
        Folder = Path.Combine(Root, "skills");
        Options = new SkillsOptions { Switches = new MemorySkillSwitches() };
        Options.Bundled.Add(new BundledSkill("slide-design", "Designs a slide.", "Use one idea per slide.")
        {
            FadenYaml = "trigger_rules:\n  keywords:\n    - slide\n",
        });
        Real = new SkillWorkshop(Options, Folder, organisation);
        Workshop = new SpyWorkshop(Real);
        Files = new FakeFiles();
    }

    public string Root { get; }

    public string Folder { get; }

    public SkillsOptions Options { get; }

    public SkillWorkshop Real { get; }

    public SpyWorkshop Workshop { get; }

    public FakeFiles Files { get; }

    public bool OnDisk(string name) => Directory.Exists(Path.Combine(Folder, name));

    public SkillLibraryViewModel Library(IChatClient? model = null, Func<UiStrings>? strings = null) =>
        new(Workshop, Files, model, strings: strings ?? (() => UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo("en"))));

    public static UiStrings English => UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo("en"));

    public static UiStrings In(string language) => UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo(language));

    public static SkillDocument Doc(string name = "contract-review", string description = "Reviews a contract clause by clause.") => new()
    {
        Name = name,
        Description = description,
        Instructions = "## Goal\nReview the contract.\n\n## Procedure\n1. Read each clause.\n",
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class SpyWorkshop : ISkillWorkshop
{
    private readonly SkillWorkshop _inner;

    public SpyWorkshop(SkillWorkshop inner) => _inner = inner;

    public int Saves;
    public int Commits;
    public int Deletes;
    public readonly List<string?> SaveNames = new();

    /// <summary>When set, an import analysis waits for it.</summary>
    public Func<string, Task>? HoldAnalysis { get; set; }

    public Task<IReadOnlyList<SkillEntry>> ListAsync(CancellationToken cancellationToken = default) => _inner.ListAsync(cancellationToken);

    public Task<SkillReadResult> ReadAsync(string name, CancellationToken cancellationToken = default) => _inner.ReadAsync(name, cancellationToken);

    public SkillCheck Check(SkillDocument document, string? folderName = null) => _inner.Check(document, folderName);

    public Task<SkillSaveResult> SaveAsync(SkillDocument document, string? previousName = null, bool replaceExisting = false, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Saves);
        lock (SaveNames)
        {
            SaveNames.Add(document.Name);
        }
        return _inner.SaveAsync(document, previousName, replaceExisting, cancellationToken);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Deletes);
        return _inner.DeleteAsync(name, cancellationToken);
    }

    public Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default) => _inner.SetEnabledAsync(name, enabled, cancellationToken);

    public async Task<ImportAnalysis> AnalyzeImportAsync(string path, CancellationToken cancellationToken = default)
    {
        if (HoldAnalysis is { } hold)
        {
            await hold(path).ConfigureAwait(false);
        }
        return await _inner.AnalyzeImportAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public Task<ImportAnalysis> AnalyzeZipAsync(byte[] zip, string source, CancellationToken cancellationToken = default) => _inner.AnalyzeZipAsync(zip, source, cancellationToken);

    public Task<SkillSaveResult> CommitImportAsync(ImportAnalysis analysis, bool replaceExisting, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Commits);
        return _inner.CommitImportAsync(analysis, replaceExisting, cancellationToken);
    }

    public Task<byte[]?> ExportAsync(string name, CancellationToken cancellationToken = default) => _inner.ExportAsync(name, cancellationToken);

    public SkillExtensionParse ParseExtension(string text) => _inner.ParseExtension(text);

    public SkillTestResult Test(string prompt, SkillDocument? edited, string? editedFrom = null) => _inner.Test(prompt, edited, editedFrom);
}

internal sealed class FakeFiles : ISkillFileHost
{
    public string? ImportPath { get; set; }

    public SkillFile? Resource { get; set; }

    public byte[]? Exported { get; private set; }

    public string? ExportedAs { get; private set; }

    public bool ExportWorks { get; set; } = true;

    public Task<string?> PickImportAsync(CancellationToken cancellationToken) => Task.FromResult(ImportPath);

    public Task<bool> SaveExportAsync(string suggestedFileName, byte[] zip, CancellationToken cancellationToken)
    {
        Exported = zip;
        ExportedAs = suggestedFileName;
        return Task.FromResult(ExportWorks);
    }

    public Task<SkillFile?> PickResourceAsync(CancellationToken cancellationToken) => Task.FromResult(Resource);
}

/// <summary>A model that answers from a script and keeps what it was asked; it runs no tool.</summary>
internal sealed class ScriptedChat : IChatClient
{
    private readonly Queue<Func<ChatMessage[], ChatOptions?, Task<ChatResponse>>> _answers = new();

    public List<(ChatMessage[] Messages, ChatOptions? Options)> Calls { get; } = new();

    public ScriptedChat Says(string text) => Then((_, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))));

    public ScriptedChat Loads(string skill) => Then((_, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
        new List<AIContent> { new FunctionCallContent("call-1", "load_skill", new Dictionary<string, object?> { ["skillName"] = skill }) }))));

    public ScriptedChat Throws() => Then((_, _) => throw new InvalidOperationException("offline"));

    public ScriptedChat Then(Func<ChatMessage[], ChatOptions?, Task<ChatResponse>> answer)
    {
        _answers.Enqueue(answer);
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToArray();
        Calls.Add((list, options));
        return _answers.Count > 0 ? _answers.Dequeue()(list, options) : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal static class Wait
{
    /// <summary>Waits (briefly) for a condition that a fire-and-forget refresh brings about.</summary>
    public static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }
    }
}
