using System.Runtime.CompilerServices;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Client.Tests;
using Fadenstack.Endpoints;
using Fadenstack.Models;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Skills.Tests;

/// <summary>One call the agent made to the model: what it offered and said.</summary>
internal sealed record ModelCall(IReadOnlyList<string> Tools, string? Instructions, IReadOnlyList<ChatMessage> Messages)
{
    /// <summary>The text of every tool result in the messages, in order.</summary>
    public IEnumerable<string> ToolResults => Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
        .Select(r => r.Result is JsonElement e ? e.ToString() : r.Result?.ToString() ?? "");
}

/// <summary>A model that plays a script: each call answers with the next step (text, or tool calls), and every call is kept.</summary>
internal sealed class ScriptedModel : IChatClient
{
    private readonly Queue<Func<ModelCall, IList<AIContent>>> _steps = new();

    public List<ModelCall> Calls { get; } = new();

    public ScriptedModel Calls_(string name, string arguments, string id = "call-1") =>
        Step(_ => [new FunctionCallContent(id, name, JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments))]);

    public ScriptedModel Says(string text) => Step(_ => [new TextContent(text)]);

    public ScriptedModel Step(Func<ModelCall, IList<AIContent>> step)
    {
        _steps.Enqueue(step);
        return this;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = new ModelCall(options?.Tools?.Select(t => t.Name).ToList() ?? new List<string>(), options?.Instructions, messages.ToList());
        Calls.Add(call);
        var contents = _steps.Count > 0 ? _steps.Dequeue()(call) : [new TextContent("Done.")];
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, contents) { FinishReason = contents.Any(c => c is FunctionCallContent) ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop };
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The agent streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A temporary folder of skills: one skill, <c>house-style</c>, with a resource and a script that would leave a mark if it ran.</summary>
internal sealed class SkillFolders : IDisposable
{
    public const string Body = "Always greet the customer by name, then answer in two short paragraphs.";
    public const string Resource = "Keep the tone warm and never use the word 'unfortunately'.";

    public SkillFolders()
    {
        Root = Path.Combine(Path.GetTempPath(), "faden-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Where the skills of the test live (a folder of skill folders).</summary>
    public string Skills => Path.Combine(Root, "skills");

    /// <summary>A file the script writes if it ever runs.</summary>
    public string Mark => Path.Combine(Root, "script-ran.txt");

    /// <summary>The standard skill: frontmatter with the six fields, a body, a resource and a script.</summary>
    public string Standard(string name = "house-style", string description = "Writes customer replies in the house style.",
        string? extraFrontmatter = null, string? folder = null, string? parent = null)
    {
        var dir = Path.Combine(parent ?? Skills, folder ?? name);
        Directory.CreateDirectory(Path.Combine(dir, "references"));
        Directory.CreateDirectory(Path.Combine(dir, "scripts"));
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            "---\nname: " + name + "\ndescription: " + description + "\nlicense: Apache-2.0\ncompatibility: any\nmetadata:\n  author: tests\n"
            + "allowed-tools: read_page\n" + extraFrontmatter + "---\n# House style\n" + Body + "\n");
        File.WriteAllText(Path.Combine(dir, "references", "tone.md"), Resource);
        File.WriteAllText(Path.Combine(dir, "scripts", "check.py"), "open(r'" + Mark + "', 'w').write('ran')\n");
        return dir;
    }

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

internal sealed class Entries : ILoggerFactory, ILoggerProvider
{
    public List<(LogLevel Level, string Text)> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly Entries _owner;

        public Logger(Entries owner) => _owner = owner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_owner.Lines)
            {
                _owner.Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
