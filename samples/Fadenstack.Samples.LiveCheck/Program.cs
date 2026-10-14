// LiveCheck: drives the .NET SDK against a running Fadenstack server and says, check by check, what works.
//
//   dotnet run -- --server https://ai.example.internal --ca ca.crt --agent sdk-livecheck --checks signin,chat,...
//
// The user signs in with FADEN_USER_EMAIL / FADEN_USER_PASSWORD (a plain user the agent is granted to).
// Checks run in the order given; the server-side steps between phases (publish, grants, tool rules) are the
// caller's. Exit code: the number of failed checks. Results also go to <data>/results-<phase>.json.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fadenstack;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Governance;
using Fadenstack.Http;
using Fadenstack.Mcp;
using Fadenstack.Samples.LiveCheck;
using Fadenstack.Tools;

var arguments = Arguments.Parse(args);
var run = new LiveCheck(
    server: new Uri(arguments.Get("server", "FADEN_SERVER") ?? throw new ArgumentException("--server")),
    ca: arguments.Get("ca", "FADEN_CA") is { } caPath ? File.ReadAllBytes(caPath) : null,
    slug: arguments.Get("agent", "FADEN_AGENT") ?? "sdk-livecheck",
    email: Environment.GetEnvironmentVariable("FADEN_USER_EMAIL") ?? throw new ArgumentException("FADEN_USER_EMAIL"),
    password: Environment.GetEnvironmentVariable("FADEN_USER_PASSWORD") ?? throw new ArgumentException("FADEN_USER_PASSWORD"),
    data: arguments.Get("data", null) ?? Path.Combine(Path.GetTempPath(), "faden-livecheck"),
    mcpCommand: arguments.Get("mcp", null) ?? "uvx mcp-server-time --local-timezone Europe/Berlin",
    thinking: !arguments.Has("no-thinking"),
    expectModel: arguments.Get("expect-model", null));
var checks = (arguments.Get("checks", null) ?? "signin,profile,chat").Split(',', StringSplitOptions.RemoveEmptyEntries);
return await run.RunAsync(checks, arguments.Get("phase", null) ?? "run");

internal sealed class LiveCheck
{
    private const string Model = "sdk-livecheck";
    private readonly Uri _server;
    private readonly byte[]? _ca;
    private readonly string _slug;
    private readonly string _email;
    private readonly string _password;
    private readonly string _data;
    private readonly string _mcpCommand;
    private readonly bool _thinking;
    private readonly string? _expectModel;
    private readonly DocumentTools _tools = new();
    private readonly ScriptedApprovals _approvals = new();
    private readonly RecordingTokenStore _tokenStore;
    private readonly FileSessionStore _sessions;
    private readonly List<CheckResult> _results = new();
    private FadenstackClient? _client;
    private AgentTokenProvider? _tokens;

    public LiveCheck(Uri server, byte[]? ca, string slug, string email, string password, string data, string mcpCommand,
        bool thinking, string? expectModel)
    {
        _thinking = thinking;
        _expectModel = expectModel;
        _server = server;
        _ca = ca;
        _slug = slug;
        _email = email;
        _password = password;
        _data = data;
        _mcpCommand = mcpCommand;
        Directory.CreateDirectory(data);
        _tokenStore = new RecordingTokenStore(new FileTokenStore(Path.Combine(data, "tokens")));
        _sessions = new FileSessionStore(Path.Combine(data, "sessions"));
    }

    private FadenstackClient Client => _client ??= FadenstackClient.ForAgent(_server, _slug, out _tokens, _tokenStore,
        o => o.TrustedCaCertificate = _ca);

    private AgentTokenProvider Tokens => (_ = Client) is not null ? _tokens! : throw new InvalidOperationException();

    public async Task<int> RunAsync(IEnumerable<string> names, string phase)
    {
        var checks = new Dictionary<string, Func<Task<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["denied-before-grant"] = DeniedBeforeGrantAsync,
            ["signin"] = SignInAsync,
            ["profile"] = ProfileAsync,
            ["chat"] = ChatAsync,
            ["host-read"] = HostReadAsync,
            ["ask-deny"] = AskDenyAsync,
            ["ask-approve-session"] = AskApproveForSessionAsync,
            ["mode-read-only"] = ModeReadOnlyAsync,
            ["mode-auto"] = ModeAutoAsync,
            ["mode-off"] = ModeOffAsync,
            ["gateway-tool"] = GatewayToolAsync,
            ["sessions"] = SessionsAsync,
            ["contract-enforced"] = ContractEnforcedAsync,
            ["plain-route-refused"] = PlainRouteRefusedAsync,
            ["local-mcp-skipped"] = LocalMcpSkippedAsync,
            ["local-mcp-allowed"] = LocalMcpAllowedAsync,
            ["tool-rule-deny"] = ToolRuleDenyAsync,
            ["gateway-approval"] = GatewayApprovalAsync,
            ["profile-v2"] = ProfileV2Async,
            ["refresh"] = RefreshAsync,
            ["refresh-reuse"] = RefreshReuseAsync,
            ["revoked"] = RevokedAsync,
            ["report"] = ReportAsync,
            ["reasoning"] = ReasoningAsync,
            ["raw-rule-deny"] = RawRuleDenyAsync,
            ["raw-force-time"] = RawForceTimeAsync,
            ["gateway-decline"] = GatewayDeclineAsync,
        };
        Console.WriteLine($"LiveCheck {phase}: {_server} agent {_slug} as {_email}");
        foreach (var name in names)
        {
            if (!checks.TryGetValue(name, out var check))
            {
                Record(name, false, "no such check", TimeSpan.Zero);
                continue;
            }
            var watch = Stopwatch.StartNew();
            try
            {
                Record(name, true, await check(), watch.Elapsed);
            }
            catch (CheckFailed e)
            {
                Record(name, false, e.Message, watch.Elapsed);
            }
            catch (Exception e)
            {
                Record(name, false, $"{e.GetType().Name}: {e.Message}", watch.Elapsed);
            }
        }
        var failed = _results.Count(r => !r.Passed);
        Console.WriteLine($"\n{_results.Count - failed}/{_results.Count} passed ({phase}).");
        await File.WriteAllTextAsync(Path.Combine(_data, $"results-{phase}.json"),
            JsonSerializer.Serialize(_results, new JsonSerializerOptions { WriteIndented = true }));
        _client?.Dispose();
        return failed;
    }

    private void Record(string name, bool passed, string detail, TimeSpan elapsed)
    {
        _results.Add(new CheckResult(name, passed, detail, Math.Round(elapsed.TotalSeconds, 1)));
        var colour = Console.ForegroundColor;
        Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(passed ? "PASS " : "FAIL ");
        Console.ForegroundColor = colour;
        Console.WriteLine($"{name} ({elapsed.TotalSeconds:0.0} s): {detail}");
    }

    // ── Sign-in ───────────────────────────────────────────────────────────────────────────────────

    private async Task<string> DeniedBeforeGrantAsync()
    {
        var login = await LoginAsync();
        try
        {
            await Tokens.SignInAsync(login, "LiveCheck (should be refused)");
        }
        catch (FadenstackApiException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return $"refused: {(int)e.Status} {e.Code} \"{e.Message}\"";
        }
        throw new CheckFailed("a user without a grant could sign in to an unpublished agent");
    }

    private async Task<string> SignInAsync()
    {
        await Tokens.SignOutAsync();
        var device = await Tokens.SignInAsync(await LoginAsync(), $"LiveCheck on {Environment.MachineName}");
        Expect(await Tokens.IsSignedInAsync(), "not signed in after SignInAsync");
        var stored = Directory.GetFiles(Path.Combine(_data, "tokens"));
        Expect(stored.Length > 0, "no token file written");
        var raw = File.ReadAllText(stored[0]);
        Expect(!raw.Contains(device.RefreshToken, StringComparison.Ordinal), "the refresh token is stored in plain text");
        var lifetime = device.AccessExpiresAt - DateTimeOffset.UtcNow;
        return $"device {device.DeviceId}, access token for {lifetime.TotalMinutes:0} min, token file protected";
    }

    private async Task<string> RefreshAsync()
    {
        var before = _tokenStore.Latest ?? throw new CheckFailed("not signed in");
        await Tokens.InvalidateAsync(before.AccessToken);
        var profile = await Client.Agent!.GetProfileAsync();
        var after = _tokenStore.Latest ?? throw new CheckFailed("signed out by the refresh");
        Expect(after.AccessToken != before.AccessToken, "the access token was not renewed");
        Expect(after.RefreshToken != before.RefreshToken, "the refresh token was not rotated");
        return $"renewed and rotated, same device {after.DeviceId == before.DeviceId}, profile v{profile.Version} read";
    }

    private async Task<string> RefreshReuseAsync()
    {
        // The refresh token from before the last rotation, used again: the server must sign the device out.
        var old = _tokenStore.History.Count >= 2 ? _tokenStore.History[^2] : throw new CheckFailed("run refresh first");
        using var http = Http();
        var response = await http.PostAsync(Api("api/agent-tokens/refresh"),
            Json(new { refresh_token = old.RefreshToken }));
        Expect(response.StatusCode == HttpStatusCode.Unauthorized, $"reusing a rotated refresh token gave {(int)response.StatusCode}");
        var current = _tokenStore.Latest ?? throw new CheckFailed("token store empty");
        await Tokens.InvalidateAsync(current.AccessToken);
        try
        {
            await Client.Agent!.GetProfileAsync();
            throw new CheckFailed("the current refresh token still works after the old one was replayed");
        }
        catch (FadenstackSignInRequiredException e)
        {
            var again = await Tokens.SignInAsync(await LoginAsync(), $"LiveCheck on {Environment.MachineName} (again)");
            return $"replay refused (401), the device was signed out everywhere (\"{e.Message}\"), signed in again as {again.DeviceId}";
        }
    }

    private async Task<string> RevokedAsync()
    {
        // The grant was taken away on the server. The gateway caches access for a minute, a refresh checks at once.
        // A fresh process: the sign-in comes from the token store (DPAPI) as an application restart would read it.
        await Tokens.GetAccessTokenAsync();
        var current = _tokenStore.Latest ?? throw new CheckFailed("not signed in");
        await Tokens.InvalidateAsync(current.AccessToken);
        try
        {
            await Client.Agent!.GetProfileAsync();
        }
        catch (Exception e) when (e is FadenstackSignInRequiredException or FadenstackPermissionException)
        {
            return $"refresh refused after the grant went: {e.GetType().Name} \"{e.Message}\"";
        }
        throw new CheckFailed("a refresh after the grant was removed still worked");
    }

    // ── The agent ─────────────────────────────────────────────────────────────────────────────────

    private async Task<string> ProfileAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        var p = agent.Profile;
        var tools = agent.Tools.ToDictionary(t => t.Name, t => t.Class);
        Expect(_expectModel is null || p.Model == _expectModel, $"profile model {p.Model}, expected {_expectModel}");
        Expect(p.HostToolContract.Count == 3, $"contract has {p.HostToolContract.Count} tools");
        Expect(!tools.ContainsKey("stamp_page"), "stamp_page is outside the contract but was kept");
        Expect(tools.GetValueOrDefault("read_page") == ToolClass.Read, "read_page is not read");
        Expect(tools.GetValueOrDefault("add_comment") == ToolClass.Write, "add_comment is not write");
        Expect(tools.GetValueOrDefault("delete_page") == ToolClass.Destructive, "delete_page is not destructive");
        Expect(p.InstructionsSha256 is { Length: > 0 }, "no instructions hash");
        Expect(p.LocalMcp.Mode == "disable", $"local MCP {p.LocalMcp.Mode}");
        return $"{p.Name} v{p.Version} ({p.Edition}), model {p.Model}, tool modes default {p.ToolModes.Default} ceiling {p.ToolModes.Ceiling ?? "none"}, "
            + $"tools {string.Join(", ", agent.Tools.Select(t => $"{t.Name}:{t.Class.ToWire()}"))} (stamp_page dropped)";
    }

    private async Task<string> ChatAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        var session = await agent.StartSessionAsync();
        var turn = await TurnAsync(session, "Answer in one short sentence: what is a table of contents used for?");
        Expect(turn.Result?.Outcome == ChatOutcome.Completed, $"outcome {turn.Result?.Outcome}");
        Expect(turn.Text.Length > 10, "no answer text");
        Expect(turn.Deltas > 1, $"the answer came in {turn.Deltas} piece(s), not streamed");
        Expect(turn.Result!.Usage is { TotalTokenCount: > 0 }, "no usage");
        return $"{turn.Deltas} deltas, {turn.Result.Usage!.TotalTokenCount} tokens, first after {turn.FirstDeltaMs} ms: \"{Short(turn.Text)}\"";
    }

    private async Task<string> HostReadAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        var session = await agent.StartSessionAsync();
        var turn = await TurnAsync(session, "Use the read_page tool to read page 3, then tell me which table is on it.");
        Expect(_tools.Calls.Contains("read_page:3"), $"read_page(3) did not run (calls: {string.Join(",", _tools.Calls)})");
        Expect(_approvals.Asked.Count == 0, "a read tool asked for approval in Ask");
        Expect(turn.Text.Contains("T31", StringComparison.Ordinal), "the answer does not use the tool's result (T31)");
        return $"read_page ran without asking; \"{Short(turn.Text)}\"";
    }

    private async Task<string> AskDenyAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        var session = await agent.StartSessionAsync();
        _approvals.Decide = _ => ApprovalDecision.Deny("not now");
        var turn = await TurnAsync(session, "Use the add_comment tool to add the comment 'checked' to page 2.");
        var asked = _approvals.Asked.FirstOrDefault(a => a.ToolName == "add_comment")
            ?? throw new CheckFailed($"add_comment did not ask (asked: {Names(_approvals.Asked)})");
        Expect(!_tools.Calls.Any(c => c.StartsWith("add_comment", StringComparison.Ordinal)), "add_comment ran although denied");
        Expect(turn.Completed.Any(c => c.Name == "add_comment" && c.Outcome == "denied"), "no denied tool event");
        return $"asked ({asked.ToolClass.ToWire()}, \"{asked.Reason}\"), denied, not run; outcome {turn.Result?.Outcome}";
    }

    private async Task<string> AskApproveForSessionAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        var session = await agent.StartSessionAsync();
        _approvals.Decide = _ => ApprovalDecision.ApproveForSession();
        await TurnAsync(session, "Use the add_comment tool to add the comment 'first' to page 4.");
        var firstAsks = _approvals.Asked.Count(a => a.ToolName == "add_comment");
        Expect(firstAsks == 1, $"first call asked {firstAsks} times");
        _approvals.Clear();
        await TurnAsync(session, "Now use add_comment again to add the comment 'second' to page 5.");
        Expect(_tools.Calls.Contains("add_comment:4") && _tools.Calls.Contains("add_comment:5"),
            $"both comments did not run (calls: {string.Join(",", _tools.Calls)})");
        Expect(_approvals.Asked.Count == 0, "the second call asked again despite 'for this session'");
        return $"asked once, approved for the session; the second call ran unasked ({string.Join(", ", session.ApprovedForSession)})";
    }

    private async Task<string> ModeReadOnlyAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        var session = await agent.StartSessionAsync(ToolMode.ReadOnly);
        var turn = await TurnAsync(session, "Use the add_comment tool to add 'ro' to page 6. If you cannot, say so.");
        Expect(!_tools.Calls.Any(c => c.StartsWith("add_comment", StringComparison.Ordinal)), "add_comment ran in read_only");
        Expect(_approvals.Asked.Count == 0, "read_only asked for approval");
        var mode = turn.Gateway.FirstOrDefault(g => g == "faden.tool_mode");
        return $"add_comment hidden, nothing asked; mode event {(mode is null ? "not seen" : "seen")}; \"{Short(turn.Text)}\"";
    }

    private async Task<string> ModeAutoAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        var session = await agent.StartSessionAsync(ToolMode.Auto);
        _approvals.Decide = _ => ApprovalDecision.Deny("scripted");
        await TurnAsync(session, "Use the add_comment tool to add the comment 'auto' to page 7.");
        Expect(_tools.Calls.Contains("add_comment:7"), $"add_comment did not run in auto (calls: {string.Join(",", _tools.Calls)})");
        Expect(_approvals.Asked.All(a => a.ToolName != "add_comment"), "a write tool asked in auto");
        _approvals.Clear();
        await TurnAsync(session, "Use the delete_page tool to delete page 8.");
        Expect(_approvals.Asked.Any(a => a.ToolName == "delete_page"), "a destructive tool did not ask in auto");
        Expect(!_tools.Calls.Contains("delete_page:8"), "delete_page ran although denied");
        return "write ran unasked; destructive asked, was denied and did not run";
    }

    private async Task<string> ModeOffAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        var session = await agent.StartSessionAsync(ToolMode.Off);
        var turn = await TurnAsync(session, "Use the read_page tool to read page 1. If you have no tools, say so.");
        Expect(_tools.Calls.Count == 0, $"a tool ran in off ({string.Join(",", _tools.Calls)})");
        Expect(turn.Started.Count == 0, $"tool calls started in off: {string.Join(",", turn.Started.Select(s => s.Name))}");
        return $"no tool offered or run; \"{Short(turn.Text)}\"";
    }

    private async Task<string> GatewayToolAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        var session = await agent.StartSessionAsync();
        var turn = await TurnAsync(session, "Use the current_time tool to find out the server's current time and tell me the hour.");
        var started = turn.Started.FirstOrDefault(s => s.Source == "gateway")
            ?? throw new CheckFailed($"no gateway tool ran (started: {string.Join(",", turn.Started.Select(s => $"{s.Source}:{s.Name}"))}; events: {string.Join(",", turn.Gateway)})");
        Expect(turn.Result?.Outcome == ChatOutcome.Completed, $"outcome {turn.Result?.Outcome}");
        // The gateway's round is kept on the device and sent back: the model still knows what the tool said.
        var replayed = session.History.Any(m => m.Contents.Any(c => c is Microsoft.Extensions.AI.FunctionResultContent));
        var follow = await TurnAsync(session, "Which tool did you just use, and what exactly did it return? Quote it.");
        return $"{started.Name} ran in the gateway ({turn.Gateway.Count} gateway events: {string.Join(",", turn.Gateway.Distinct())}); "
            + $"round kept in the session history: {replayed}; follow-up: \"{Short(follow.Text)}\"";
    }

    private async Task<string> SessionsAsync()
    {
        string id;
        using (var agent = Build())
        {
            await agent.StartAsync();
            var session = await agent.StartSessionAsync();
            id = session.Id;
            await TurnAsync(session, "Remember the code word PELICAN-42 for later. Just reply OK.");
            await session.CloseAsync();
        }
        // A new agent object, as after the host application restarted.
        using var again = Build();
        await again.StartAsync();
        var listed = await again.ListSessionsAsync();
        Expect(listed.Any(s => s.Id == id), "the session is not listed");
        var reopened = await again.OpenSessionAsync(id);
        var turn = await TurnAsync(reopened, "What was the code word I asked you to remember?");
        Expect(turn.Text.Contains("PELICAN-42", StringComparison.OrdinalIgnoreCase), $"the reopened session forgot: \"{Short(turn.Text)}\"");
        var files = Directory.GetFiles(Path.Combine(_data, "sessions"), "*", SearchOption.AllDirectories);
        var plain = files.Any(f => File.ReadAllText(f).Contains("PELICAN", StringComparison.Ordinal));
        Expect(!plain, "the transcript is stored in plain text");
        return $"{listed.Count} session(s) on the device; reopened {id} and the model remembered; {files.Length} transcript file(s), encrypted";
    }

    private async Task<string> ReasoningAsync()
    {
        // Thinking on: the SDK streams the model's reasoning apart from the answer.
        using var agent = Build(thinking: true);
        await agent.StartAsync();
        var watch = Stopwatch.StartNew();
        var turn = await TurnAsync(await agent.StartSessionAsync(), "Is 391 a prime number? Answer yes or no and give the factors if not.");
        Expect(turn.Result?.Outcome == ChatOutcome.Completed, $"outcome {turn.Result?.Outcome}");
        Expect(turn.ReasoningChars > 0, "no reasoning streamed (ReasoningDelta)");
        Expect(turn.Text.Contains("17", StringComparison.Ordinal) && turn.Text.Contains("23", StringComparison.Ordinal), $"wrong answer: {Short(turn.Text)}");
        return $"thought {turn.ReasoningChars} chars, answered in {watch.Elapsed.TotalSeconds:0} s: \"{Short(turn.Text)}\"";
    }

    private async Task<string> ProfileV2Async()
    {
        using var agent = Build();
        await agent.StartAsync();
        Expect(agent.Profile.Version > 1, $"profile version {agent.Profile.Version}, expected a newer one than 1");
        Expect(agent.Profile.LocalMcp.Mode == "allow", $"local MCP {agent.Profile.LocalMcp.Mode}");
        return $"v{agent.Profile.Version} live, local MCP {agent.Profile.LocalMcp.Mode}";
    }

    private async Task<string> LocalMcpSkippedAsync()
    {
        using var mcp = new LocalMcpModule(new[] { McpServer() });
        using var agent = Build(mcp);
        await agent.StartAsync();
        Expect(mcp.Attached.Count == 0, "attached although the agent does not allow local MCP");
        var skipped = mcp.Skipped.FirstOrDefault() ?? throw new CheckFailed("not reported as skipped");
        Expect(agent.Tools.All(t => t.Source != "mcp"), "MCP tools offered");
        return $"'{skipped.Label}' not started: {skipped.Reason}";
    }

    private async Task<string> LocalMcpAllowedAsync()
    {
        using var mcp = new LocalMcpModule(new[] { McpServer() });
        using var agent = Build(mcp);
        await agent.StartAsync();
        var attached = mcp.Attached.FirstOrDefault()
            ?? throw new CheckFailed($"not attached ({string.Join("; ", mcp.Skipped.Select(s => $"{s.Label}: {s.Reason} {s.Error?.Message}"))})");
        var mcpTools = agent.Tools.Where(t => t.Source == "mcp").ToList();
        Console.WriteLine($"     tools offered by the SDK: {string.Join(", ", agent.Tools.Select(t => $"{t.Source}:{t.Name}:{t.Class.ToWire()}"))}");
        Expect(mcpTools.Count > 0 && mcpTools.All(t => t.Class == ToolClass.Unknown), "MCP tools are not unclassified");
        var session = await agent.StartSessionAsync();
        _approvals.Decide = _ => ApprovalDecision.Approve();
        var turn = await TurnAsync(session, "Use the get_current_time tool to find out the current time in the timezone Europe/Berlin and tell me the hour.");
        var asked = _approvals.Asked.FirstOrDefault(a => a.Source == "mcp")
            ?? throw new CheckFailed($"the MCP tool did not ask (started: {string.Join(",", turn.Started.Select(s => $"{s.Source}:{s.Name}"))}; outcome {turn.Result?.Outcome}; {Short(turn.Text)})");
        var done = turn.Completed.FirstOrDefault(c => c.Source == "mcp") ?? throw new CheckFailed("no MCP tool completed");
        Expect(done.Outcome == "ok", $"MCP tool {done.Name}: {done.Outcome}");
        return $"'{attached.Label}' attached with {string.Join(", ", attached.Tools)}; {asked.ToolName} asked (unclassified), approved, ran "
            + $"in {done.Duration.TotalMilliseconds:0} ms; \"{Short(turn.Text)}\"";
    }

    private async Task<string> ToolRuleDenyAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        var session = await agent.StartSessionAsync(ToolMode.Auto);
        _approvals.Decide = _ => ApprovalDecision.Approve();
        var turn = await TurnAsync(session, "Use the delete_page tool to delete page 9.");
        Expect(!_tools.Calls.Any(c => c.StartsWith("delete_page", StringComparison.Ordinal)), "delete_page ran despite the deny rule");
        var notice = turn.Notices.FirstOrDefault(n => n.Outcome == "tool_denied")
            ?? throw new CheckFailed($"no tool_denied notice (notices: {string.Join("; ", turn.Notices.Select(n => n.Outcome))}; text \"{Short(turn.Text)}\")");
        return $"delete_page not offered; notice \"{notice.Message}\" (decision {notice.DecisionId ?? "-"}); outcome {turn.Result?.Outcome}";
    }

    private async Task<string> GatewayApprovalAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        const string Prompt = "Use the current_time tool to find out the server's current time and tell me the hour.";

        // A small model does not always call the tool: up to three fresh sessions per half, counted in the result.
        async Task<(TurnLog Turn, int Attempts)> AskedAsync(ApprovalDecision decision)
        {
            for (var attempt = 1; ; attempt++)
            {
                _approvals.Clear();
                _approvals.Decide = _ => decision;
                var turn = await TurnAsync(await agent.StartSessionAsync(), Prompt);
                if (_approvals.Asked.Any(a => a.Source == "gateway") || attempt == 3)
                {
                    return (turn, attempt);
                }
            }
        }

        var (approved, approveTries) = await AskedAsync(ApprovalDecision.Approve());
        var asked = _approvals.Asked.FirstOrDefault(a => a.Source == "gateway")
            ?? throw new CheckFailed($"the gateway never asked in {approveTries} tries (events: {string.Join(",", approved.Gateway)}; started {string.Join(",", approved.Started.Select(s => $"{s.Source}:{s.Name}"))})");
        var ran = approved.Completed.FirstOrDefault(c => c.Source == "gateway");
        Expect(ran is { Outcome: "ok" }, $"approved gateway tool: {ran?.Outcome ?? "did not run"}");

        var (declined, declineTries) = await AskedAsync(ApprovalDecision.Deny("no"));
        Expect(_approvals.Asked.Any(a => a.Source == "gateway"), $"the model never called the tool in {declineTries} tries for the decline");
        Expect(!declined.Completed.Any(c => c.Source == "gateway" && c.Outcome == "ok"), "the declined gateway tool ran");
        return $"{asked.ToolName} waited for approval in the gateway; approved -> ran (try {approveTries}); "
            + $"declined -> not run (try {declineTries}); \"{Short(declined.Text)}\"";
    }

    // ── The server enforces it too ──────────────────────────────────────────────────────────────────

    private async Task<string> ContractEnforcedAsync()
    {
        // stamp_page is outside the contract: the SDK drops it, and a client that sends it anyway does not get it
        // to the model. Servers from 0.4.5 hold such a tool back for review and answer, naming it in
        // X-Faden-Held-Tools; older ones refused the request (4xx).
        var token = await Tokens.GetAccessTokenAsync();
        using var http = Http(token);
        var body = new
        {
            model = Model,
            messages = new[] { new { role = "user", content = "Stamp the drawing." } },
            tools = new[] { new { type = "function", function = new { name = "stamp_page", description = "Stamps.", parameters = new { type = "object", properties = new { } } } } },
        };
        var response = await http.PostAsync(Gateway($"v1/agents/{_slug}/chat/completions"), Json(body));
        var text = await response.Content.ReadAsStringAsync();
        var held = response.Headers.TryGetValues("X-Faden-Held-Tools", out var values) ? string.Join(",", values) : "";
        Expect((int)response.StatusCode is >= 400 and < 500 || (response.IsSuccessStatusCode && held.Contains("stamp_page")),
            $"a tool outside the contract got {(int)response.StatusCode} without being held back");
        return response.IsSuccessStatusCode ? $"held back: {held}" : $"{(int)response.StatusCode}: {Short(text, 140)}";
    }

    private async Task<string> PlainRouteRefusedAsync()
    {
        var token = await Tokens.GetAccessTokenAsync();
        using var http = Http(token);
        var body = new { model = "qwen-chat", messages = new[] { new { role = "user", content = "hi" } } };
        var response = await http.PostAsync(Gateway("v1/chat/completions"), Json(body));
        Expect(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"an agent token on the plain route got {(int)response.StatusCode}");
        return $"agent token on /v1/chat/completions: {(int)response.StatusCode}";
    }

    private async Task<string> RawRuleDenyAsync()
    {
        // The tool-rule-deny request without the SDK: every SSE line the gateway sends, as it sends it.
        var token = await Tokens.GetAccessTokenAsync();
        using var http = Http(token);
        object Tool(string name, string description) => new
        {
            type = "function",
            function = new
            {
                name, description,
                parameters = new { type = "object", properties = new { page = new { type = "integer", description = "Page number" } }, required = new[] { "page" } },
            },
        };
        var body = new
        {
            model = Model, stream = true,
            messages = new[] { new { role = "user", content = "Use the delete_page tool to delete page 9." } },
            tools = new[] { Tool("read_page", "Reads a page."), Tool("add_comment", "Adds a comment."), Tool("delete_page", "Deletes a page.") },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Gateway($"v1/agents/{_slug}/chat/completions")) { Content = Json(body) };
        request.Headers.Add("X-Faden-Events", "ag-ui");
        request.Headers.Add("X-Faden-Tool-Mode", "auto");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var text = await response.Content.ReadAsStringAsync();
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        foreach (var line in lines)
        {
            Console.WriteLine($"       sse  {Short(line, 400)}");
        }
        // An error event of the answer itself: a data line that is not one of the gateway's own (event: faden).
        var error = lines.Where((l, i) => l.StartsWith("data: {\"error\"", StringComparison.Ordinal)
            && (i == 0 || !lines[i - 1].StartsWith("event: faden", StringComparison.Ordinal))).FirstOrDefault();
        Expect(error is null, $"the stream carries an error event: {error}");
        return $"{(int)response.StatusCode}, {lines.Count(l => l.StartsWith("data:", StringComparison.Ordinal))} data lines, no error event";
    }

    private async Task<string> GatewayDeclineAsync()
    {
        // The decline half of gateway approvals without depending on the model: a chat stage (the SDK's own
        // extension point) makes the turn's first model call name current_time; the gateway asks, the user says no.
        using var agent = Build(configure: b => b.AddChatStage(StageOrder.Custom, inner => new ForceToolOnce(inner, "current_time")));
        await agent.StartAsync();
        _approvals.Decide = _ => ApprovalDecision.Deny("not now");
        var turn = await TurnAsync(await agent.StartSessionAsync(), "What time is it on the server?");
        var asked = _approvals.Asked.Where(a => a.Source == "gateway").ToList();
        Expect(asked.Count > 0, $"the gateway did not ask (events: {string.Join(",", turn.Gateway)})");
        Expect(!turn.Completed.Any(c => c.Source == "gateway" && c.Outcome == "ok"), "the declined gateway tool ran");
        return $"{asked[0].ToolName} asked {asked.Count}x (\"{asked[0].Reason}\"), declined each time, never ran; outcome {turn.Result?.Outcome}; \"{Short(turn.Text)}\"";
    }

    private async Task<string> RawForceTimeAsync()
    {
        // Forces a call of the gateway's current_time, no events: is the tool offered to the model at all?
        // Offered: it runs (or, with an approval rule and no events to ask with, is refused to the model).
        // Not offered: the model's server refuses a tool_choice naming a tool it was not given.
        var token = await Tokens.GetAccessTokenAsync();
        using var http = Http(token);
        var body = new
        {
            model = Model,
            messages = new[] { new { role = "user", content = "What time is it on the server?" } },
            tool_choice = new { type = "function", function = new { name = "current_time" } },
        };
        var response = await http.PostAsync(Gateway($"v1/agents/{_slug}/chat/completions"), Json(body));
        var text = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"       body {Short(text, 600)}");
        Expect(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: current_time was not offered ({Short(text, 200)})");
        return $"{(int)response.StatusCode}: {Short(text, 220)}";
    }

    private async Task<string> ReportAsync()
    {
        using var agent = Build();
        await agent.StartAsync();
        await agent.FlushAsync();
        return "events flushed";
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private FadenstackAgent Build(LocalMcpModule? mcp = null, Action<AgentBuilder>? configure = null, bool? thinking = null)
    {
        _tools.Clear();
        _approvals.Clear();
        _approvals.Decide = _ => ApprovalDecision.Deny("scripted: deny");
        var builder = FadenstackAgent.CreateBuilder()
            .UseClient(Client)
            .UseAgent(_slug)
            .AddTools(_tools)
            .UseApprovals(_approvals)
            .UseSessionStore(_sessions);
        if (mcp is not null)
        {
            builder.AddLocalMcp(mcp);
        }
        configure?.Invoke(builder);
        if (!(thinking ?? _thinking))
        {
            // Qwen's switch: the answer without the thinking before it (the gateway passes the field through).
            builder.AddChatStage(StageOrder.Custom, inner => new NoThinking(inner));
        }
        return builder.Build();
    }

    private LocalMcpServer McpServer()
    {
        var parts = _mcpCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return LocalMcpServer.Stdio("time", parts[0], parts.Skip(1));
    }

    private async Task<TurnLog> TurnAsync(AgentSession session, string prompt)
    {
        var log = new TurnLog();
        var watch = Stopwatch.StartNew();
        Console.WriteLine($"     > [{session.EffectiveToolMode.ToWire()}] {prompt}");
        await foreach (var e in session.StreamAsync(prompt))
        {
            switch (e)
            {
                case ReasoningDelta r:
                    log.ReasoningChars += r.Text.Length;
                    break;
                case TextDelta t:
                    log.Deltas++;
                    log.FirstDeltaMs ??= watch.ElapsedMilliseconds;
                    log.Builder.Append(t.Text);
                    break;
                case ToolCallStarted t:
                    log.Started.Add(t);
                    Console.WriteLine($"       tool {t.Source}:{t.Name} ({t.Class.ToWire()}) {JsonSerializer.Serialize(t.Arguments)}");
                    break;
                case ToolCallCompleted t:
                    log.Completed.Add(t);
                    Console.WriteLine($"       done {t.Source}:{t.Name} {t.Outcome} {t.Duration.TotalMilliseconds:0} ms");
                    break;
                case ApprovalRequested a:
                    Console.WriteLine($"       ask  {a.Request.Source}:{a.Request.ToolName} ({a.Request.ToolClass.ToWire()}): {a.Request.Reason}");
                    break;
                case ApprovalDecided d:
                    Console.WriteLine($"       {(d.Decision.Approved ? "yes" : "no ")}  {d.Request.ToolName}{(d.Decision.ForSession ? " (for the session)" : "")}");
                    break;
                case NoticeRaised n:
                    log.Notices.Add(n.Notice);
                    Console.WriteLine($"       note {n.Notice.Outcome}: {n.Notice.Message}");
                    break;
                case GatewayEvent g:
                    log.Gateway.Add(g.Event.Kind);
                    if (g.Event.Kind is "faden.tool_mode" or "faden.tool_denied")
                    {
                        Console.WriteLine($"       evt  {g.Event.Kind} {g.Event.Value.GetRawText()}");
                    }
                    break;
                case TurnCompleted c:
                    log.Result = c.Result;
                    break;
            }
        }
        Console.WriteLine($"     < {log.Result?.Outcome} in {watch.Elapsed.TotalSeconds:0.0} s{(log.ReasoningChars > 0 ? $", thought {log.ReasoningChars} chars" : "")}: {Short(log.Text, 200)}");
        return log;
    }

    private async Task<string> LoginAsync()
    {
        using var http = Http();
        var response = await http.PostAsync(Api("api/auth/jwt/login"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = _email,
            ["password"] = _password,
        }));
        var text = await response.Content.ReadAsStringAsync();
        Expect(response.IsSuccessStatusCode, $"login failed: {(int)response.StatusCode} {Short(text)}");
        return JsonDocument.Parse(text).RootElement.GetProperty("access_token").GetString()!;
    }

    private HttpClient Http(string? bearer = null)
    {
        var http = new HttpClient(FadenstackTransport.CreateHandler(new FadenstackClientOptions { ServerUrl = _server, TrustedCaCertificate = _ca }));
        if (bearer is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        return http;
    }

    private Uri Api(string path) => new(_server, path);

    private Uri Gateway(string path) => new(_server, path);

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string Names(IEnumerable<ApprovalRequest> asked) => string.Join(",", asked.Select(a => a.ToolName));

    private static string Short(string text, int max = 120)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
    }

    private static void Expect(bool condition, string failure)
    {
        if (!condition)
        {
            throw new CheckFailed(failure);
        }
    }
}

internal sealed class TurnLog
{
    public StringBuilder Builder { get; } = new();
    public string Text => Builder.ToString();
    public int Deltas { get; set; }
    public int ReasoningChars { get; set; }
    public long? FirstDeltaMs { get; set; }
    public List<ToolCallStarted> Started { get; } = new();
    public List<ToolCallCompleted> Completed { get; } = new();
    public List<GovernanceNotice> Notices { get; } = new();
    public List<string> Gateway { get; } = new();
    public AgentResult? Result { get; set; }
}

/// <summary>Asks Qwen-style models to answer without thinking first (chat_template_kwargs.enable_thinking).</summary>
internal sealed class NoThinking(Microsoft.Extensions.AI.IChatClient inner) : Microsoft.Extensions.AI.DelegatingChatClient(inner)
{
    public override IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = (options ?? new Microsoft.Extensions.AI.ChatOptions()).Clone();
        options.AdditionalProperties ??= new Microsoft.Extensions.AI.AdditionalPropertiesDictionary();
        options.AdditionalProperties["chat_template_kwargs"] = new Dictionary<string, object?> { ["enable_thinking"] = false };
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}

/// <summary>Names one tool in the first request only, as a host would to force a lookup.</summary>
internal sealed class ForceToolOnce(Microsoft.Extensions.AI.IChatClient inner, string tool)
    : Microsoft.Extensions.AI.DelegatingChatClient(inner)
{
    private int _calls;

    public override IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            options = (options ?? new Microsoft.Extensions.AI.ChatOptions()).Clone();
            options.ToolMode = Microsoft.Extensions.AI.ChatToolMode.RequireSpecific(tool);
        }
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}

internal sealed record CheckResult(string Name, bool Passed, string Detail, double Seconds);

internal sealed class CheckFailed(string message) : Exception(message);

/// <summary>The SDK's token store, with every token set it saved remembered (for the refresh-reuse check).</summary>
internal sealed class RecordingTokenStore(ITokenStore inner) : ITokenStore
{
    public List<TokenSet> History { get; } = new();

    /// <summary>The token set in use: the last one saved or loaded.</summary>
    public TokenSet? Latest { get; private set; }

    public async Task<TokenSet?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var loaded = await inner.LoadAsync(key, cancellationToken);
        Latest ??= loaded;
        return loaded;
    }

    public Task SaveAsync(string key, TokenSet tokens, CancellationToken cancellationToken = default)
    {
        History.Add(tokens);
        Latest = tokens;
        return inner.SaveAsync(key, tokens, cancellationToken);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        Latest = null;
        return inner.DeleteAsync(key, cancellationToken);
    }
}

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
