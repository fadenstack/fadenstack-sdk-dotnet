// A console agent: signs in for one agent, keeps its sessions on this machine, runs host tools under the
// session's tool mode and asks before anything the mode does not let run on its own.
//
//   dotnet run -- --server https://faden.example --agent eplan-assistant [--api https://...] [--login-token <jwt>]
//                 [--insecure] [--mcp label=command arg ...] [--session <id>]
//
// Commands: /mode off|read_only|ask|auto, /sessions, /quit. Anything else is sent to the agent.
using Faden;
using Faden.Agents;
using Faden.Auth;
using Faden.Mcp;
using Faden.Samples;
using Faden.Tools;

var arguments = Arguments.Parse(args);
var server = new Uri(arguments.Get("server", "FADEN_SERVER") ?? throw Usage("--server"));
var slug = arguments.Get("agent", "FADEN_AGENT") ?? throw Usage("--agent");
var api = arguments.Get("api", "FADEN_API") is { } a ? new Uri(a) : null;
var insecure = arguments.Has("insecure");

using var client = FadenClient.ForAgent(server, slug, out var tokens, configure: o =>
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

var builder = FadenAgent.CreateBuilder()
    .UseClient(client)
    .UseAgent(slug)
    .AddTools(new DemoTools())
    .UseApprovals(AskOnConsole);
if (arguments.Get("mcp", null) is { } mcp && mcp.Split(new[] { '=' }, 2) is [var label, var command])
{
    var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    builder.AddLocalMcp(LocalMcpServer.Stdio(label, parts[0], parts.Skip(1)));
}
using var agent = builder.Build();
await agent.StartAsync();
Console.WriteLine($"{agent.Profile.Name} v{agent.Profile.Version} -- tools: {string.Join(", ", agent.Tools.Select(t => $"{t.Name} ({t.Class.ToWire()})"))}");

var session = arguments.Get("session", null) is { } id ? await agent.OpenSessionAsync(id) : await agent.StartSessionAsync();
Console.WriteLine($"Session {session.Id}, tool mode {session.EffectiveToolMode.ToWire()}. /mode, /sessions, /quit.");

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
    if (line.Length == 0)
    {
        continue;
    }
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
                Console.WriteLine($"\n  = {c.Result.Outcome}");
                break;
        }
    }
}
await session.CloseAsync();
return 0;

static async Task<ApprovalDecision> AskOnConsole(ApprovalRequest request, CancellationToken cancellationToken)
{
    Console.Write($"\n  ? Run {request.ToolName} ({request.ToolClass.ToWire()}, {request.Source})? {request.Reason} [y/s/N] ");
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
