# Delegating tasks to agents on the device

This page covers how one agent hands a task to another agent on the same computer with `Fadenstack.Agents`: for example a desktop agent that gives Office work to the add-in agent running in Word.

`IAgentDelegate` and its request and result types are in `Fadenstack.Agents`. The implementation (`DelegationHost`, `LocalAgentDelegate`, `DelegateToolModule`) is in `Fadenstack.Agents.Delegation`, a package for applications that is not published to nuget.org with the SDK.

The other agent runs the task as a new session of its own: its own server agent, tool contract, tool modes and approvals. Only the task text and the references you choose go over, and only its final answer comes back. An approval the other agent needs is asked of the caller's person.

## In the add-in: take tasks

Start a `DelegationHost` once the add-in's agent is ready. It registers the agent in the device agent registry with a short card, and listens on a local pipe that only the current OS user can open. The first message on the pipe must carry a secret that exists only for this launch, and it is written to the registry folder, which only the same user can read.

```csharp
using Fadenstack.Agents.Delegation;
using Fadenstack.Agents.Registry;

await agent.StartAsync();
var host = new DelegationHost(agent, new DeviceAgentRegistry(), new DelegationHostOptions
{
    Name = "Word",
    Description = "Reads and edits the open Word document.",
    Skills = new[] { new AgentCardSkill("Comments", "Adds and answers comments") },
});
host.TaskChanged += (_, e) => pane.Show(e.Task.Label, e.Task.StatusText);   // "Task from Desktop agent"
host.Start();   // dispose the host when the add-in closes
```

One task runs at a time; another is rejected as busy. A task that runs longer than `TaskTimeout`, or an approval nobody answers within `ApprovalTimeout`, ends or is denied. `TaskChanged` is raised on a background thread.

## In the caller: hand a task over

`LocalAgentDelegate` implements `IAgentDelegate`. It finds the agent in the registry, sends the task and relays progress and approvals. An agent that is not running is a result, not an exception.

```csharp
var delegation = new LocalAgentDelegate(new DeviceAgentRegistry(), "desktop-assistant", "Desktop agent");
var result = await delegation.RunAsync(
    new DelegationRequest("word-assistant", "Summarise the open contract.") { References = new[] { "contract.docx" } },
    new DelegationCallbacks
    {
        Approve = (request, token) => ShowApprovalCard(request, token),   // returns an ApprovalDecision
        Status = status => ShowProgress(status.Text),
    },
    cancellationToken);
// result.Outcome: Completed, Failed, Canceled, Rejected or NotRunning; result.Text is the answer or the reason
```

## As a tool: `delegate`

`DelegateToolModule` gives the model one `delegate(agent, task, references)` tool for all other agents. The `agent` argument is an enum of the slugs in the agent profile's `ui.delegates`, which is also the allowlist; with none, or with a tool contract that does not list `delegate`, the tool is not offered. The description lists each agent as `slug: name, summary`, taken from the card of the agent when it is running. The tool is class `write`, and the answer comes back fenced as untrusted content.

```csharp
var agent = FadenstackAgent.CreateBuilder()
    .UseClient(client).UseAgent("desktop-assistant")
    .Use(new DelegateToolModule(delegation, new DelegateToolOptions
    {
        FromAgent = "desktop-assistant",
        Approve = (request, token) => ShowApprovalCard(request, token),
        Names = new Dictionary<string, string> { ["word-assistant"] = "Word" },
    }))
    .Build();
```

Each call is reported to the server as a `tool.executed` event for the tool `delegate` (name, outcome, duration), never with the task or the answer. With `ReportDelegations` the module also sends an `agent.delegated` event (from, to, task id, outcome, duration) once the server accepts that kind.
