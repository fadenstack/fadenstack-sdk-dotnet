// A console agent: signs in for one agent, keeps its sessions on this machine, runs host tools under the
// session's tool mode and asks before anything the mode does not let run on its own. Open source (Apache-2.0): the SDK's
// showcase, one file you can read from top to bottom.
//
//   dotnet run -- --server https://ai.example.internal --agent docs-assistant [--api https://...] [--login-token <jwt>]
//                 [--insecure] [--mcp "label=command arg ..."] [--skills <folder>] [--session <id>]
//
// Standalone, on the person's own model endpoint, with no Fadenstack server (docs/standalone.md):
//
//   dotnet run -- --endpoint http://localhost:11434/v1 --model qwen3:14b [--dialect ollama] [--key-env NAME]
//                 [--definition agent.json] [--instructions "Answer in German."] [--check]
//                 [--mcp "label=command arg ..."] [--skills <folder>] [--session <id>]
//
// --key-env names an environment variable that holds the key: a key never goes on the command line, and the sample
// never stores or prints one. --check runs the endpoint check first and prints what it found. --skills loads your own
// skills (folders with a SKILL.md, docs/skills.md) through the Skills package, as an app does. --mcp starts one local MCP
// server over stdio, in standalone mode too (docs/local-mcp.md); the agent's definition allows it and every call of its
// tools asks first. Content marking (docs/provenance.md) is on: standalone, the records are signed on this device.
//
// Commands: /mode off|read_only|ask|auto, /sessions, /skills, /copy, /stop, /record, /quit. Anything else is sent to the
// agent. The write tool add_comment asks before it runs in Ask mode (y runs it once, s allows it for the session).
using System.Net;
using Fadenstack;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Endpoints;
using Fadenstack.Mcp;
using Fadenstack.Provenance;
using Fadenstack.Samples;
using Fadenstack.Skills;
using Fadenstack.Tools;

var arguments = Arguments.Parse(args);
var mcpServer = LocalMcpFrom(arguments.Get("mcp", null));
var skillsFolder = arguments.Get("skills", null);
FadenstackAgent agent;
FadenstackClient? client = null;
string? standaloneLine = null;
if (arguments.Get("endpoint", "FADEN_ENDPOINT") is { } endpointUrl)
{
    var keyName = arguments.Get("key-env", null);
    var key = keyName is null ? null : Environment.GetEnvironmentVariable(keyName) is { Length: > 0 } k
        ? new StaticTokenProvider(k) : throw new ArgumentException($"The environment variable {keyName} holds no key.");
    var preset = EndpointPreset.Named(arguments.Get("dialect", null));
    var endpoint = new ModelEndpoint
    {
        BaseUrl = new Uri(endpointUrl),
        Model = arguments.Get("model", "FADEN_MODEL") ?? throw Usage("--model"),
        Key = key,
        Dialect = preset?.Dialect ?? EndpointDialect.Named(arguments.Get("dialect", null)),
    };
    EndpointReport? report = null;
    if (arguments.Has("check"))
    {
        report = await new EndpointCheck(endpoint).RunAsync(new EndpointCheckOptions { RequiredContextTokens = 8000, ProbeVision = true });
    }
    // What the person sees first: where their questions go, which model answers, and what the check found.
    Console.WriteLine($"Endpoint {endpoint.BaseUrl.GetLeftPart(UriPartial.Path)}, model {endpoint.Model}, dialect {endpoint.Dialect.Name}.");
    Console.WriteLine(report is null ? "Not checked (run with --check)." : Describe(report));
    Console.WriteLine($"Notice: you are talking to an AI model ({endpoint.Model}). Its answers can be wrong; check them before you rely on them.");
    var definition = arguments.Get("definition", null) is { } file
        ? AgentDefinition.FromJson(File.ReadAllText(file))
        : AgentDefinition.FromJson(DemoTools.Definition);
    var standalone = FadenstackAgent.CreateBuilder()
        .UseEndpoint(endpoint, report)
        .UseLocalProfile(definition, arguments.Get("instructions", null), new LocalProfileOptions { LocalMcp = mcpServer is not null })
        .AddTools(new DemoTools())
        .UseApprovals(AskOnConsole);
    Extend(standalone);
    agent = standalone.Build();
    standaloneLine = "Marking: records are signed on this device.";
}
else
{
    var server = new Uri(arguments.Get("server", "FADEN_SERVER") ?? throw Usage("--server or --endpoint"));
    var slug = arguments.Get("agent", "FADEN_AGENT") ?? throw Usage("--agent");
    var api = arguments.Get("api", "FADEN_API") is { } a ? new Uri(a) : null;
    var insecure = arguments.Has("insecure");

    client = FadenstackClient.ForAgent(server, slug, out var tokens, configure: o =>
    {
        o.ApiUrl = api;
        o.AllowInsecureHttp = insecure;
    });
    if (!await tokens.IsSignedInAsync())
    {
        var login = arguments.Get("login-token", "FADEN_LOGIN_TOKEN") ?? throw Usage("--login-token (first run only)");
        var device = await tokens.SignInAsync(login, $"{Environment.MachineName} console");
        Console.WriteLine($"Signed in for {slug} on this device ({device.DeviceId}).");
    }

    var builder = FadenstackAgent.CreateBuilder()
        .UseClient(client)
        .UseAgent(slug)
        .AddTools(new DemoTools())
        .UseApprovals(AskOnConsole);
    Extend(builder);
    agent = builder.Build();
}
using var disposeAgent = agent;
using var disposeClient = client;
await agent.StartAsync();
Console.WriteLine($"{agent.Profile.Name} v{agent.Profile.Version} ({agent.Connection}) -- tools: {string.Join(", ", agent.Tools.Select(t => $"{t.Name} ({t.Class.ToWire()})"))}");
if (standaloneLine is not null)
{
    Console.WriteLine(standaloneLine);
}

var session = arguments.Get("session", null) is { } id ? await agent.OpenSessionAsync(id) : await agent.StartSessionAsync();
Console.WriteLine($"Session {session.Id}, tool mode {session.EffectiveToolMode.ToWire()}. /mode, /sessions, /skills, /copy, /stop, /record, /quit.");

// Ctrl+C stops the running turn (the same as the chat's Stop button) instead of ending the sample.
Console.CancelKeyPress += (_, e) =>
{
    if (session.Stop())
    {
        e.Cancel = true;
    }
};

var lastTurnId = "";
var lastAnswer = "";
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null || line == "/quit")
    {
        break;
    }
    if (line.StartsWith("/mode", StringComparison.Ordinal))
    {
        if (ToolModes.ParseMode(line.Substring(5).Trim()) is { } mode)
        {
            session.ToolMode = mode;
        }
        Console.WriteLine($"Tool mode {session.ToolMode.ToWire()} (runs as {session.EffectiveToolMode.ToWire()}).");
        continue;
    }
    if (line == "/sessions")
    {
        foreach (var s in await agent.ListSessionsAsync())
        {
            Console.WriteLine($"  {s.Id}  {s.LastActivity:g}  {s.Title}");
        }
        continue;
    }
    if (line == "/skills")
    {
        var skills = await agent.ListSkillsAsync();
        Console.WriteLine(skills.Count == 0 ? "  No skills (see --skills <folder>)."
            : string.Join(Environment.NewLine, skills.Select(s => $"  {s.Slug}: {s.Description}")));
        continue;
    }
    if (line == "/copy")
    {
        await CopyAsync();
        continue;
    }
    if (line == "/stop")
    {
        // Stop everything: running turns, the grants and the "for this session" approvals. What was allowed always stays.
        var stopped = await agent.StopAllAsync();
        Console.WriteLine($"Stopped {stopped.TurnsStopped} turns and ended {stopped.GrantsEnded} grants.");
        continue;
    }
    if (line == "/record")
    {
        var record = await agent.VerifyAuditAsync();
        Console.WriteLine(record.Entries == 0 ? "Record of tool calls: no entries yet."
            : $"Record of tool calls: {record.Entries} entries since {record.FirstTime:d}, "
              + (record.Intact ? "intact." : $"damaged at entry {record.BrokenAtSeq}."));
        continue;
    }
    if (line.Length == 0)
    {
        continue;
    }
    lastAnswer = "";
    await foreach (var e in session.StreamAsync(line))
    {
        switch (e)
        {
            case TextDelta t:
                Console.Write(t.Text);
                break;
            case ToolCallStarted t:
                Console.WriteLine($"\n  [{t.Source}] {t.Name} ...");
                break;
            case ToolCallCompleted t:
                Console.WriteLine($"  [{t.Source}] {t.Name}: {t.Outcome} ({t.Duration.TotalMilliseconds:0} ms)");
                break;
            case NoticeRaised n:
                Console.WriteLine($"\n  ! {n.Notice.Outcome}: {n.Notice.Message}");
                break;
            case TurnCompleted c:
                lastTurnId = c.Result.TurnId;
                lastAnswer = c.Result.Text;
                Console.WriteLine($"\n  = {c.Result.Outcome}");
                break;
        }
    }
}
await session.CloseAsync();
return 0;

// The module lines both modes share: marking what the agent writes and copies, the person's own skills, one local MCP server.
void Extend(AgentBuilder builder)
{
    builder.Use(new ProvenanceModule());
    if (skillsFolder is not null)
    {
        var options = new SkillsOptions();
        options.Folders.Add(skillsFolder);
        builder.Use(new SkillsModule(options));
    }
    if (mcpServer is not null)
    {
        builder.AddLocalMcp(mcpServer);
    }
}

// What a person copies out of the chat is marked like what a tool places. A console has no clipboard: it prints the record's id.
async Task CopyAsync()
{
    if (lastAnswer.Length == 0)
    {
        Console.WriteLine("  Nothing to copy yet.");
        return;
    }
    var html = "<p>" + WebUtility.HtmlEncode(lastAnswer).Replace("\n", "<br>") + "</p>";
    var copied = await agent.MarkCopyAsync(new AnswerCopy(lastAnswer, html, session.Id, lastTurnId));
    Console.WriteLine(copied.Refused ? "  Not copied: this answer could not be marked as AI-generated, and the policy requires it."
        : copied.Marked ? $"  Copied, marked as AI-generated. Record {copied.RecordId}."
        : copied.ExemptBecause is { } why ? $"  Copied, not marked ({why})."
        : "  Copied, not marked.");
}

static LocalMcpServer? LocalMcpFrom(string? mcp)
{
    if (mcp is not null && mcp.Split(new[] { '=' }, 2) is [var label, var command])
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return LocalMcpServer.Stdio(label, parts[0], parts.Skip(1));
    }
    return null;
}

static string Describe(EndpointReport report)
{
    var found = new List<string>
    {
        report.Reachable ? "reachable" : "not reachable",
        report.Chat == true ? "answers" : "does not answer",
        report.ToolCalls == true ? "calls tools" : report.ToolCalls == false ? "no tool calls (it can only talk)" : "tool calls not known",
        report.Vision == true ? "sees pictures" : report.Vision == false ? "does not see pictures" : "pictures not known",
        report.ContextLength is { } context ? $"context {context} tokens" : "context unknown",
    };
    var problems = report.Problems.Count == 0 ? "no problems" : "problems: " + string.Join(", ", report.Problems);
    return $"Check: {string.Join(", ", found)}; {problems}.";
}

static async Task<ApprovalDecision> AskOnConsole(ApprovalRequest request, CancellationToken cancellationToken)
{
    var choices = request.CanAllowForSession ? "y/s/N" : "y/N"; // "s": for this session, where the request offers it
    Console.Write($"\n  ? Run {request.ToolName} ({request.ToolClass.ToWire()}, {request.Source})? {request.Reason}");
    // What the call is given, as the card shows it. A call the gateway holds can come with its text cut: the card says so.
    foreach (var argument in request.Arguments)
    {
        Console.Write($"\n      {(request.ArgumentsShortened ? "" : argument.Key + ": ")}{argument.Value}");
    }
    if (request.ArgumentsShortened)
    {
        Console.Write("\n      The arguments are shortened.");
    }
    Console.Write($" [{choices}] ");
    var answer = await Task.Run(Console.ReadLine, cancellationToken);
    return answer?.Trim().ToLowerInvariant() switch
    {
        "y" => ApprovalDecision.Approve(),
        "s" => ApprovalDecision.ApproveForSession(),
        _ => ApprovalDecision.Deny(),
    };
}

static Exception Usage(string missing) => new ArgumentException($"Missing {missing}. See the top of Program.cs.");

internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _values = new();

    public static Arguments Parse(string[] args)
    {
        var parsed = new Arguments();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            var name = args[i].Substring(2);
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            parsed._values[name] = value;
        }
        return parsed;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name, string? environment) =>
        _values.TryGetValue(name, out var v) && v is not null ? v
        : environment is null ? null : Environment.GetEnvironmentVariable(environment);
}
