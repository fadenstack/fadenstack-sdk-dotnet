# Samples

Open source (Apache-2.0): the SDK's showcase. Run them from the repository's root. [docs/samples.md](../docs/samples.md) has every option.

| Sample | What it shows |
|---|---|
| `Fadenstack.Samples.Console` | An agent in a console, with a server or on your own model: a tool that asks before it writes, your own skills, a local MCP server, content marking, Stop everything and the record of tool calls. |
| `Fadenstack.Samples.Wpf` | The chat panel in a window, against a built-in demo gateway. Windows only. |
| `Fadenstack.UI.Avalonia.Sample` | The same chat panel in Avalonia. |
| `Fadenstack.Samples.LiveCheck` | A check of an SDK setup against a real server. |

## The console agent on your own model

No server and no account: the agent talks to a model on your computer. A key never goes on the command line; name the environment variable that holds it with `--key-env`.

**Ollama.** Start Ollama, pull a model that calls tools (`ollama pull qwen3:14b`), then:

```bash
dotnet run --project samples/Fadenstack.Samples.Console -- --endpoint http://localhost:11434/v1 --model qwen3:14b --dialect ollama --check
```

**LM Studio.** Load a model in LM Studio and start its local server (Developer tab, default port 1234), then:

```bash
dotnet run --project samples/Fadenstack.Samples.Console -- --endpoint http://localhost:1234/v1 --model <the model's id in LM Studio> --dialect lm-studio --check
```

At the start the sample prints the endpoint, the model, the dialect, what `--check` found, and a notice that you are talking to an AI model. Try:

- "Add a comment to page 2": `add_comment` is a write tool, so in Ask mode the sample asks first (`y` runs it once, `s` allows it for the session). "Delete page 2" asks every time.
- `--skills <folder>` loads your own skills (a folder holding folders with a `SKILL.md`); `/skills` lists them.
- `--mcp "notes=notes-mcp.exe --db notes.db"` starts a local MCP server over stdio; each of its tools asks until you allow it.
- `/copy` marks the last answer as AI-generated, as the chat's Copy does, and prints the record's id (a console has no clipboard). In standalone mode the record is signed on this device.
- `/stop` is Stop everything. `/record` checks the record of tool calls and prints its line.
