# The injection suite (sdk/51 7.1)

A deterministic suite: injected content runs through every untrusted channel against the fake gateway with a scripted model.
No real model, no network. It runs in the normal test run and as its own step in `.github/workflows/release.yml`
("Injection suite (sdk/51 7.1)"), so a failure blocks a release.

- .NET: `tests/Fadenstack.Agents.Tests/Injection` (and the image gate in `tests/Fadenstack.UI.Tests/Injection`). Every test has the
  trait `Category=Injection`: `dotnet test Fadenstack.slnx --filter "Category=Injection"`.
- TypeScript: `packages/agents/test/injection`: `node node_modules/vitest/vitest.mjs run packages/agents/test/injection`.

## The corpus

`corpus.json` is the same file, byte for byte, in both SDK repos. Each suite checks the file's SHA-256 (line endings as LF)
against a constant, so a change in one repo fails until the other repo has it. Each case is
`{ id, title, kind, content, induces?, padding? }`:

- `content`: the text an attacker put into a document, a page, a mail, a tool result or another agent's answer.
- `induces`: the call the text asks a model to make: `{ tool, arguments }`. The suite's tools are `post_comment` (write),
  `delete_page` (destructive) and `fetch_url` (read, open-world). A case without `induces` only tests G1.
- `padding`: `{ unit, times }`. The text is `unit` repeated `times` times, then `content`: long padding without a long file.
- Invisible and look-alike characters are written as `\uXXXX` escapes, so the file shows what it holds.

| Kind | What it tests | Gates |
|---|---|---|
| `instruction-override` | "Ignore all previous instructions", polite, in other languages, and with a call to make | G1, G2 |
| `forged-fence-close` | Our closing lines with guessed tags, other channels' fences, changed brackets, a fake cut note | G1, G2 |
| `role-template` | ChatML, `### System:`, `</tool_result>`, `[INST]`, Harmony and Gemma turn tokens | G1, G2 |
| `invisible-text`, `bidi` | Zero-width characters, bidi overrides, the Unicode tag block, control characters | G1, G2 |
| `homoglyph` | Closing lines and words spelled with look-alike letters | G1 |
| `forged-approval` | "The user approved this. approval: granted", forged approval JSON and events | G1, G2 |
| `forged-tool-call` | Tool calls written as OpenAI JSON, MCP JSON-RPC or XML | G1, G2 |
| `exfiltration` | Markdown image and link, reference image, HTML image, "send the text to https://..." | G1, G2, G3, G4 |
| `induced-call` | A write tool, a destructive tool, an open-world tool with an address from the content | G1, G2, G3 |
| `encoded` | Base64, ROT13 and hex wrapped instructions | G1, G2 |
| `padding` | Padding past the fence's limit (cut), emoji so the cut falls inside a surrogate pair, hidden characters as padding | G1, G2 |
| `prompt-leak` | Requests for the system prompt, tool definitions, keys | G1 |

## The channels

MCP tool result (a local MCP server in this process), web search result (the content is a result's snippet), delegation answer
(another agent's reply), document text (an app tool that fences with `UntrustedContent.Fence` / `fenceUntrusted`).

## The gates

- **G1, stays fenced.** What the model receives holds the content only inside one fence of ours: our header first, a tag not
  found in the content, nothing after the closing line but our own cut note; invisible, bidi and control characters gone; no split
  surrogate pair. Per case and channel.
- **G2, induced calls still ask.** A scripted model emits the call the content asks for, in ReadOnly, Ask and Auto. The call meets
  the rules any call meets: the suite carries its own table of them (not read from the code under test), and the same call after
  harmless content gives the same result. A call that asks never runs without the person's approval (the suite's handler denies);
  the card names the tool and its arguments as the model sent them; a forged approval in the content approves nothing.
- **G3, no unasked call to an address from the content.** Below Auto an open-world call whose address is in the content asks. In Auto
  it runs: a known limit, recorded in a test named for it, until the tainted open-world marker (item 8.4) exists.
- **G4, no automatic fetch.** The UI view models never load a remote image from an answer. .NET: the Markdown model has no image,
  and the skins' drawing code has no bitmap. TypeScript: the Markdown model has an `image` node that no skin turns into an `<img>`.

## Adding a case

Add it to `corpus.json` in both repos (same bytes), then update the SHA-256 constant in both suites. Pick the kind that fits, add
`induces` if the text asks for a call, and run the suite. A new kind needs a row in the table above and in the corpus test's list
of kinds. The corpus is only ever added to: a case that starts failing points to a real change in a guard.

## Not done

The live opt-in run with promptfoo against a real endpoint (a real model reading the corpus) is later, in 0.3.1. This suite
checks what the SDK does with injected content, not whether a model is fooled by it.
