# Samples

This page covers the samples in the SDK's repository, which are open source (Apache-2.0) and meant to be read: a console agent and the WPF chat panel, and a tool to check an SDK setup against a server.

Run them from the repository's root with the .NET 10 SDK. The WPF sample runs on Windows only.

## Console agent

`samples/Fadenstack.Samples.Console` is an agent in a console window. It signs in for one agent, keeps its sessions on the machine, runs a few host tools of each class under the session's tool mode, and asks on the console before a call the mode does not let run on its own.

```bash
dotnet run --project samples/Fadenstack.Samples.Console -- --server https://ai.example.internal --agent docs-assistant --login-token <token>
```

| Option | Meaning |
|---|---|
| `--server` | The Fadenstack server. Or set `FADEN_SERVER`. |
| `--agent` | The agent's slug. Or set `FADEN_AGENT`. |
| `--login-token` | The user's login token, needed on the first run only. Or set `FADEN_LOGIN_TOKEN`. |
| `--api` | The backend, when it is not at the server URL. Or set `FADEN_API`. |
| `--insecure` | Allows `http://`, for a development server on your machine. |
| `--mcp "label=command args"` | A local MCP server to add, started over stdio, for example `--mcp "notes-db=notes-mcp.exe"`; in standalone mode too. It is attached only if the agent allows it; see [Local MCP servers](local-mcp.md). |
| `--skills` | A folder of your own skills (each a folder with a `SKILL.md`), loaded through the Skills package; see [Skills](skills.md). |
| `--session` | The id of a session to continue. |

**On your own model, with no server** ([Standalone mode](standalone.md)):

```bash
dotnet run --project samples/Fadenstack.Samples.Console -- --endpoint http://localhost:11434/v1 --model qwen3:14b --dialect ollama --check
```

| Option | Meaning |
|---|---|
| `--endpoint` | The endpoint's base, including `/v1`. Or set `FADEN_ENDPOINT`. |
| `--model` | The model's id on that server. Or set `FADEN_MODEL`. |
| `--dialect` | A preset's id (`ollama`, `lm-studio`, `vllm`, `openai`, `azure-openai`, ...); the plain OpenAI-compatible one if not given. |
| `--key-env` | The name of an environment variable that holds the key. A key never goes on the command line. |
| `--definition` | An agent definition file (an app's `contracts/*.json`); a demo definition for the demo tools if not given. |
| `--instructions` | Your own instructions, after the definition's. |
| `--check` | Runs the endpoint check first and prints what it found. |

At the start the sample prints the endpoint (address, model, dialect), what the check found, and a notice that you are talking to an AI model. `--skills` and `--mcp` work here as with a server. Content marking is on ([Content marking](provenance.md)); with no server, the records are signed on this device. At the prompt, anything you type goes to the agent. These commands control the sample:

| Command | Effect |
|---|---|
| `/mode off`, `/mode read_only`, `/mode ask`, `/mode auto` | Sets the session's tool mode. |
| `/sessions` | Lists the sessions on this machine. |
| `/skills` | Lists the skills the agent has. |
| `/copy` | Marks the last answer as AI-generated, as the chat's Copy does (`MarkCopyAsync`), and prints whether it was marked, with the record's id: a console has no clipboard. |
| `/stop` | Stop everything: running turns, grants and the "for this session" approvals. |
| `/record` | Checks the record of tool calls and prints its line. |
| `/quit` | Ends the sample. |

Ctrl+C stops the running turn.

When a tool asks (the write tool `add_comment` does in Ask mode), it shows what it is given. Answer `y` to run it once, `s` to allow it for the session where the prompt offers it, or anything else to decline. Destructive and unclassified tools ask every time.

## WPF chat panel

`samples/Fadenstack.Samples.Wpf` shows the [chat panel](chat-panel.md) in a window. With `--demo`, or when no server is given, it runs against a built-in demo gateway that streams Markdown and calls the sample's host tools, so you can try the panel without any setup:

```bash
dotnet run --project samples/Fadenstack.Samples.Wpf -- --demo
```

Try "read page 3", "add a comment to page 2", "delete page 2" or "stamp", and switch the tool mode in the header to see how calls run, ask or are hidden. The demo keeps its sessions in memory.

Against a server, it takes the same `--server`, `--agent`, `--api` and `--login-token` options as the console sample:

```bash
dotnet run --project samples/Fadenstack.Samples.Wpf -- --server https://ai.example.internal --agent docs-assistant --login-token <token>
```

The sample's whole composition is in `App.xaml.cs`: it builds the agent with an `ApprovalBroker`, creates the `AgentChatViewModel`, and sets it as the window's data context.

## LiveCheck

`samples/Fadenstack.Samples.LiveCheck` drives the SDK against a running server and reports, check by check, what works, from the sign-in and streaming to host tools in each tool mode, approvals and local MCP servers. Use it to check an SDK setup against your server.
