# Samples

This page covers the samples in the SDK's repository: a console agent and the WPF chat panel, and a tool to check an SDK setup against a server.

Run them from the repository's root with the .NET 10 SDK. The WPF sample runs on Windows only.

## Console agent

`samples/Faden.Samples.Console` is an agent in a console window. It signs in for one agent, keeps its sessions on the machine, runs a few host tools of each class under the session's tool mode, and asks on the console before a call the mode does not let run on its own.

```bash
dotnet run --project samples/Faden.Samples.Console -- --server https://ai.example.internal --agent eplan-assistant --login-token <token>
```

| Option | Meaning |
|---|---|
| `--server` | The Fadenstack server. Or set `FADEN_SERVER`. |
| `--agent` | The agent's slug. Or set `FADEN_AGENT`. |
| `--login-token` | The user's login token, needed on the first run only. Or set `FADEN_LOGIN_TOKEN`. |
| `--api` | The backend, when it is not at the server URL. Or set `FADEN_API`. |
| `--insecure` | Allows `http://`, for a development server on your machine. |
| `--mcp label=command` | A local MCP server to add, started over stdio, for example `--mcp "eplan-db=eplan-mcp.exe"`. It is attached only if the agent allows it; see [Local MCP servers](local-mcp.md). |
| `--session` | The id of a session to continue. |

At the prompt, anything you type goes to the agent. Three commands control the sample:

| Command | Effect |
|---|---|
| `/mode off`, `/mode read_only`, `/mode ask`, `/mode auto` | Sets the session's tool mode. |
| `/sessions` | Lists the sessions on this machine. |
| `/quit` | Ends the sample. |

When a tool asks, answer `y` to run it once, `s` to allow it for the session where the prompt offers it, or anything else to decline. Destructive and unclassified tools ask every time.

## WPF chat panel

`samples/Faden.Samples.Wpf` shows the [chat panel](chat-panel.md) in a window. With `--demo`, or when no server is given, it runs against a built-in demo gateway that streams Markdown and calls the sample's host tools, so you can try the panel without any setup:

```bash
dotnet run --project samples/Faden.Samples.Wpf -- --demo
```

Try "read page 3", "add a comment to page 2", "delete page 2" or "stamp", and switch the tool mode in the header to see how calls run, ask or are hidden. The demo keeps its sessions in memory.

Against a server, it takes the same `--server`, `--agent`, `--api` and `--login-token` options as the console sample:

```bash
dotnet run --project samples/Faden.Samples.Wpf -- --server https://ai.example.internal --agent eplan-assistant --login-token <token>
```

The sample's whole composition is in `App.xaml.cs`: it builds the agent with an `ApprovalBroker`, creates the `AgentChatViewModel`, and sets it as the window's data context.

## LiveCheck

`samples/Faden.Samples.LiveCheck` drives the SDK against a running server and reports, check by check, what works, from the sign-in and streaming to host tools in each tool mode, approvals and local MCP servers. Use it to check an SDK setup against your server.
