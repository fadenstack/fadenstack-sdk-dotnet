# Testing

This page covers how to test an app that uses the SDK: what the packages give you for tests, a fake server in a few lines, and how to test approvals, the chat view models, logs and the provenance marks.

The .NET SDK has no test kit that plays the server, as the TypeScript SDK has in [`@fadenstack/testing`](../typescript/testing.md). What it has is the seams a test needs: every part that touches the network, the disk or a person can be replaced. The SDK's own tests use one pattern, shown below: a fake `HttpMessageHandler` that answers the routes an agent uses.

## What the packages give you

| You need | Use |
|---|---|
| No server | An `HttpMessageHandler` of your own, passed as `FadenstackClientOptions.HttpClient` ([A fake server](#a-fake-server)). |
| No sign-in | `StaticTokenProvider("test-token")`, or `MemoryTokenStore` for a real `AgentTokenProvider`. |
| No files | `MemorySessionStore` and `MemoryApprovalStore`. Or a store on a temporary folder: every file store takes its folder (`new FileSessionStore(directory)`). |
| No person to approve | An approval handler that answers from the test ([Approvals](#approvals)). |
| No UI thread | `InlineDispatcher.Instance` and `MemoryPreferenceStore` in `Fadenstack.UI.Services` ([The chat view model](#the-chat-view-model)). |
| No server for provenance | `TestProvenanceBackend`, `PassThroughProvenance` and `MarkerTestKit` in `Fadenstack.Provenance` ([Content provenance](provenance.md#testing-a-marker)). |

## A fake server

An agent needs two routes to run a turn: its profile and its chat completions. The fake below answers both. It plays the model from a script: each call to `Says` or `Calls` is the answer to one model call, in order.

```csharp
using System.Net;
using System.Text;
using System.Text.Json;

public sealed class FakeGateway : HttpMessageHandler
{
    private const string Profile = """
        {"object":"agent.profile","id":"a-1","slug":"docs-assistant","name":"Docs","version":1,"draft":false,
         "model":"test-model","host_tool_contract":[{"name":"read_page","risk":"read"}],
         "tool_modes":{"default":"ask"},"features":["agents"]}
        """;

    private readonly Queue<string> _answers = new();

    /// <summary>The bodies of the chat requests the agent sent.</summary>
    public List<string> ChatRequests { get; } = new();

    /// <summary>The next model call answers with text.</summary>
    public FakeGateway Says(string text) => Answer(new { content = text }, "stop");

    /// <summary>The next model call asks for a tool.</summary>
    public FakeGateway Calls(string id, string name, string arguments) =>
        Answer(new { tool_calls = new[] { new { index = 0, id, type = "function", function = new { name, arguments } } } }, "tool_calls");

    private FakeGateway Answer(object delta, string finishReason)
    {
        static string Data(object chunk) => "data: " + JsonSerializer.Serialize(chunk) + "\n\n";
        _answers.Enqueue(
            Data(new { id = "c1", choices = new[] { new { index = 0, delta } } }) +
            Data(new { id = "c1", choices = new[] { new { index = 0, delta = new { }, finish_reason = finishReason } } }) +
            "data: [DONE]\n\n");
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/v1/agents/docs-assistant/profile":
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Profile, Encoding.UTF8, "application/json") };
            case "/v1/agents/docs-assistant/chat/completions":
                ChatRequests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_answers.Dequeue(), Encoding.UTF8, "text/event-stream") };
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
```

The profile is the agent's `host_tool_contract` as an administrator would have set it: `read_page` is a read tool. When the contract is not empty, a tool it does not list is dropped when the agent starts, so the fake must list every host tool the test uses.

A test then builds the agent on the fake and runs a turn:

```csharp
var gateway = new FakeGateway()
    .Calls("call-1", "read_page", """{"page":7}""")
    .Says("Page 7 shows table T1.");

using var agent = FadenstackAgent.CreateBuilder()
    .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("test-token"),
        options => options.HttpClient = new HttpClient(gateway))
    .UseAgent("docs-assistant")
    .AddTools(new PageTools())
    .UseSessionStore(new MemorySessionStore())
    .UseApprovalStore(new MemoryApprovalStore())
    .UseEventReporting(false)
    .Build();

var session = await agent.StartSessionAsync();
var result = await session.SendAsync("What is on page 7?");

Assert.Equal(ChatOutcome.Completed, result.Outcome);
Assert.Equal("Page 7 shows table T1.", result.Text);
Assert.Equal(2, gateway.ChatRequests.Count);
Assert.Contains("table T1, 4 rows", gateway.ChatRequests[1]);
```

`PageTools` is the host tool class of [Host tools](tools.md), with `ReadPage` as a read tool. The agent calls the model twice: first the model asks for `read_page`, then, with the tool's result in the request, it answers. `ChatRequests[1]` holds that second request, so a test can check what the model was given.

Two things keep the test small:

- `UseEventReporting(false)` keeps the agent from posting its events and the app's manifest. With reporting on, those routes may answer 404; the agent ignores the failure and the turn goes on.
- The stores are in memory, so the test leaves nothing on the disk and never touches the user's data folder.

An agent reads other routes when a feature needs them, such as the skills list, the host-call check, grants or provenance. A fake answers only the routes a test uses. Where a route is optional, a 404 reads as a server without that feature: no skills, for example, or `null` from `ListToolsAsync`.

The SDK's own tests use a larger version of this fake: `FakeServer` in `tests/Fadenstack.Client.Tests` (routes, recorded requests, streamed answers) and `Gateway` in `tests/Fadenstack.Agents.Tests` (the agent's profile with its contract, modes, grants and provenance routes). They are test code and are not published as a package. You may copy them: the SDK is under the Apache-2.0 licence.

## Approvals

An approval handler can be a function, so a test decides what the user would have said:

```csharp
builder.UseApprovals((request, cancellationToken) =>
    Task.FromResult(request.ToolName == "delete_page"
        ? ApprovalDecision.Deny("Not in a test.")
        : ApprovalDecision.Approve()));
```

Without a handler every call that needs approval is declined, which is the right default for a test that does not expect one. A declined call ends the turn with the outcome `ToolDenied`. The tool's class comes from the profile's contract, so a write tool in a test needs `"risk":"write"` in the fake's profile.

## The chat view model

The view models run headless. A test can drive `AgentChatViewModel` with the broker that is the agent's approval handler, a dispatcher that runs everything on the calling thread, and preferences in memory:

```csharp
var broker = new ApprovalBroker();
var agent = builder.UseApprovals(broker).Build();

var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance,
    new AgentChatOptions { RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore() });
await chat.InitializeAsync();
await chat.SendAsync("What is on page 7?");

var turn = chat.Turns.Last();
```

`RenderInterval = TimeSpan.Zero` shows streamed updates at once instead of in frames. Without `Preferences`, the panel reads and writes the user's real `preferences.json`, so a test should always pass a `MemoryPreferenceStore`. An approval card is an `ApprovalPart` in `turn.Parts`; its `AllowCommand` and `DenyCommand` stand for the clicks. The SDK's own tests of the panel are in `tests/Fadenstack.UI.Tests`, and of the Avalonia skin in `tests/Fadenstack.UI.Avalonia.Tests`.

## Logs and telemetry

The SDK logs through the `ILoggerFactory` you give `UseLogging`, so a test passes a factory that keeps what it is given and checks the event ids ([Logging](agent.md#logging)). The SDK's tests do this with a `CapturingLoggerFactory` in `tests/Fadenstack.Client.Tests`, which is not published either; an `ILoggerProvider` of a few lines does the same.

Spans and metrics are read with the listeners of the .NET runtime, on the names in `FadenstackTelemetry`:

```csharp
var spans = new List<Activity>();
using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == FadenstackTelemetry.AgentsSource,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = spans.Add,
};
ActivitySource.AddActivityListener(listener);

var counted = new ConcurrentDictionary<string, long>();
using var meters = new MeterListener
{
    InstrumentPublished = (instrument, meterListener) =>
    {
        if (instrument.Meter.Name == FadenstackTelemetry.AgentsMeter)
        {
            meterListener.EnableMeasurementEvents(instrument);
        }
    },
};
meters.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
    counted.AddOrUpdate(instrument.Name, measurement, (_, total) => total + measurement));
meters.Start();
```

## Files, folders and keys

Where the SDK keeps things is fixed by the operating system ([Sessions on the device](sessions.md#where-they-are-stored)), and a test must not write there. Use the memory stores, or give a file store a temporary folder. `SecretProtection.Use` and `FadenstackSecrets.UseOsKeyStore` choose a process-wide protector once, before the first store exists, and cannot be undone, so a test that needs one belongs in a process of its own; most tests need neither.

## Marks and tools by hand

A tool is a method, so a unit test can call it directly. A tool that writes content people read takes an `IProvenance`; pass a `PassThroughProvenance` from `Fadenstack.Provenance.Testing` and the call needs no server. `MarkerTestKit.AssertAsync` checks a marker of your own. Both are described in [Content provenance](provenance.md#testing-a-marker).

## The SDK's own tests

The repository has one test project per package, on xUnit, in `tests/`. They target `net10.0`, and the WPF skin's tests `net10.0-windows`. Run them from the repository's root with `dotnet test`.
