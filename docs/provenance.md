# Content provenance

This page covers `Fadenstack.Provenance`: how an agent's tools mark what they place where people read it as AI-generated, what is recorded, and how you mark content in a host of your own. The TypeScript SDK's `@fadenstack/provenance` does the same, step for step and byte for byte ([below](#the-same-in-both-sdks)).

## Why

Under the EU AI Act (Art. 50(2)), content an AI system generates must be marked in a machine-readable way and be detectable as AI-generated. The Fadenstack server marks what it makes: pictures get a signed C2PA manifest, IPTC metadata and an invisible watermark, text from local models gets the model's watermark where the runtime applies one, and every answer ends with a signed generation receipt. What an agent then puts into a document, a mail or a web page is marked again where it lands, by the SDK. If you build an agent on the SDK and use or offer it under your name, you are that agent's provider; this module is how you meet the duty, and your users should know that marks must not be removed.

## Turn it on

```csharp
using Fadenstack.Provenance;

builder.Use(new ProvenanceModule());            // or builder.UseProvenance(o => ...)
```

The module is named `provenance`, so an agent whose profile requires it (`required_client_modules`) starts only with it. It adds a chat stage that keeps each turn's receipts, a tool stage that checks what tools place, and a tool service, `IProvenance`.

## Declare and place

A tool that writes content people read declares it with `[Emits]` ([Content for people](tools.md#content-for-people)) and places it with `IProvenance.PlaceAsync`. The SDK binds the `IProvenance` parameter; the model never sees it.

```csharp
[AgentTool(ToolClass.Write), Emits("text", "text/html", EditKind.Generated), Description("Inserts a reply into the mail.")]
public Task<string> InsertReplyAsync(string text, IProvenance provenance, CancellationToken ct) =>
    provenance.PlaceAsync(new MarkableContent(text, "text/html", editor.Sink), marked => editor.InsertAsync(marked.Text!), ct);
```

`PlaceAsync`:

1. decides the edit kind: the content's own (`MarkableContent.EditKind`), else the declaration's, else `Generated`; a classifier (`UseClassifier`) may change it;
2. records exempt content without a record or a mark (`ExemptReasons`): the policy's internal switch, an exempt edit kind (translated, corrected, formatted, extracted by default), code and data (`application/json`, `text/x-*`, ...), and labels -- at most three words and 40 characters without a sentence mark, or 8 characters of CJK text. **Short text is not exempt**: it is recorded and marked like any other; only the model's watermark is not expected under the policy's threshold (200 tokens), and the watermark layer says `short-text`;
3. checks the layers the content needs (below) as far as it can before anything is signed; with the policy `refuse`, a missing one refuses here;
4. has the server sign a record of the placement: the content's SHA-256 (text normalised first), for HTML and Markdown also the SHA-256 of the text a reader sees (`text_sha256`, below), its media type, its edit kind and the receipt, never the content. Without a signed record no marker runs -- no mark carries an id the server does not know -- the content is inserted as it is, and the metadata layer is reported `signed-record`, `server-unavailable`;
5. lets the markers that fit the sink prepare the content, calls your insert with it, and lets the markers mark what landed and read it back;
6. reports the placement to the server (layers applied, reason codes for the others) and writes a `provenance.placed` entry into the session's transcript.

It returns a `Placement`: the record, the layers, and the layers the content needed that did not hold (`Missing`). Its `Summary` is a line for the model.

**When the insert fails.** If your insert throws after the server signed the record, the placement is still reported and recorded -- the record's id, every layer `Applied = false` (the transcript's `detail` says `insert failed: <exception>`) -- so the server's placements match its records; then `PlaceAsync` throws your exception on.

**Arguments that are not content.** A tool that writes text for people in one call and picks an option or ticks a check box in another says so per call: its `EditKindFrom` method returns `EditKind?`, null for a call whose argument is not content (see [Content for people](tools.md#content-for-people)). For such a call `PlaceAsync` inserts the value as it is -- no record, no mark, no report, no transcript entry -- and returns a placement with `Exempt` and `ExemptBecause` `not-content`; a call that does not use `PlaceAsync` at all is not recorded as unmarked. An `EditKind` on the content makes it content again.

**Host values.** A marker that needs your insert to write something with the content -- Word's bookmark, inside the insert's own undo entry -- prepares it for this one placement: `content.WithHostValue(key, value)` in `PrepareAsync`. Your insert reads `marked.HostValues[key]`, and `AttachAsync` reads `placed.Content.HostValues[key]`. `OfficeMarks.AnchorValue` (`office.anchor`) is the key for the anchor's name (`OfficeMarks.AnchorName`). Ids and names only: host values stay on the device.

A tool that declares content and returns without placing it is recorded as `provenance.unmarked` and reported without a record (sink kind `unmarked`, no layers), unless the content is exempt or the call's `EditKindFrom` said null. In development mode -- `DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT` set to `Development`, or `UseDevelopmentMode()` -- the call fails instead. A marker that throws is recorded (`failed: <error>`) and the placement goes on, in development mode too.

`Labels.IsLabel(text, mediaType)` is the rule for labels, public so a host can apply it to the parts it writes on its own (Excel, per cell).

## Sinks

A `SinkInfo` describes once what a place content lands can carry:

| Member | Meaning |
|---|---|
| `Kind` | `word.range`, `html.editor`, `crm.note`: reported, never shown |
| `Container` | The content lands in a document, a file or an HTML page, so it needs signed metadata in it, not only the watermark. Default false: free-form text (a chat message, a plain field) |
| `Carries` | The carriers it keeps: `Carriers.HtmlAttribute`, `HtmlTitle`, `FrontMatter`, `KeyValue`, `Sidecar`, `HttpHeader`, `Bytes`, `UnicodeText`, or your host's own |
| `KeyValue`, `Headers`, `FilePath` | Where the key-value, header and sidecar markers write |
| `ReadBack` | Reads back what the host holds, so markers can check their mark held |

`SinkInfo.Nothing("chat.message")` carries nothing: the content gets the model's watermark and the server's record, and the report says `host`, `sink-cannot-carry` -- missing only where the content needs metadata (a container, a picture).

## Built-in markers

| Marker (`Name`) | Layer | What it does |
|---|---|---|
| `ServerSignedMarker` (`server-record`) | metadata | The server's signed record of every placement, with the content's hash (`signed-record`); no mark in the content, so it never makes the metadata layer hold |
| `ImageProvenanceGuard` (`image-guard`) | metadata | Checks a picture still has its C2PA manifest and XMP, before and (where the host gives the bytes back) after; `stripped-by-host` otherwise. Method `c2pa` |
| `WatermarkStatus` (`watermark-status`) | watermark | Records from the receipt whether the content carries a watermark (`vllm-<algorithm>`, `upstream-<vendor>`, `trustmark`), and if not, why (method `model`) |
| `HtmlAttributeMarker` (`html-attribute`) | metadata | Wraps HTML in one element with `data-ai-generated` and a `title` ("AI-generated" in the user's language, a space, the record's id); one paragraph is marked inside it (`<p><span ...>...</span></p>`), several blocks get a `div`; reads back which held: `html-attribute`, or `html-title` when only the title did. For sinks that carry `HtmlAttribute` or `HtmlTitle`; HTML only |
| `MarkdownFrontMatterMarker` (`front-matter`) | metadata | `ai-generated: true` and `ai-record-id: "<id>"` in the YAML front matter, merged into front matter the file has |
| `KeyValueMarker` (`key-value`) | metadata | The same two keys in the host's fields: `ai-generated` = `true`, then `ai-record-id` = the record's id, read back |
| `SidecarMarker` (`sidecar`) | metadata | The signed record in `<file>.provenance.json` beside the file |
| `HttpHeaderMarker` (`http-header`) | metadata | `X-AI-Generated` with the record's id on an HTTP response, set before it goes out |
| `VisibleLabelMarker` (`visible-label`) | label | A visible "AI-generated" above the content; on when the policy's `visible_label` is, with `UseVisibleLabel()`, or when you add it as a marker |
| `UnicodeTextMarker` (`unicode-text`) | metadata | The id in invisible characters at the end of the text; opt-in only (`AddMarker(new UnicodeTextMarker())`, on a sink that carries `Carriers.UnicodeText`): filters strip them |

The texts marks show people come from the SDK's texts (`UiStrings.AiGenerated`, `UiStrings.AiGeneratedTitle`), in the user's language. Readers recognise the titles of every built-in language.

## The policy

The agent's policy comes from the server (`GET /v1/agents/{agent}/provenance/policy`); while the server does not answer, the last one it gave, else the contract's default. With `record`, the default, content is placed and what could not be marked is recorded. With `refuse`, content a needed layer is not possible for is not placed: nothing is signed, inserted or reported, `PlaceAsync` throws `ProvenanceRefusedException`, the model gets its message ("Not written: this agent only places content it can mark as AI-generated, and here ..."), the transcript a `provenance.refused` entry, and the user a `provenance_refused` notice.

| Needed layer | For | Holds when |
|---|---|---|
| metadata | a container sink, every picture | a host marker applied it (the server's record does not count) |
| watermark | text from `ShortTextTokens` on (a CJK character, or four other characters, a token unless you `UseTokenCounter`), every picture | the receipt says it was applied |
| label | text, while the policy's `visible_label` is on | a label marker applied it |

A mark the host drops after the insert is recorded: the content is already in place.

Local options may only tighten the policy: `RefuseWhenMissing()`, a lower `UseShortTextTokens(...)`, `MarkEditKind(...)`, `UseVisibleLabel()`. A looser setting is ignored and logged.

## What is recorded

| Transcript entry | When |
|---|---|
| `provenance.placed` | After every placement: `call_id`, `tool`, `argument`, `record_id`, `signed`, `receipt_id`, `sink_kind`, `media_type`, `edit_kind`, `length_class`, `exempt`, `exempt_because`, `classified` (`{ from, to, reason }`), `layers` (`{ layer, method, applied, reason, verified, detail }`), `missing` (`{ layer, method, reason }`), `reported`, `facts`, `dropped_facts` |
| `provenance.refused` | The policy refused: `call_id`, `tool`, `argument`, `sink_kind`, `media_type`, `edit_kind`, `length_class`, `missing` |
| `provenance.unmarked` | A declared output the tool did not place: `call_id`, `tool`, `argument`, `media_type`, `edit_kind`, `length_class`, `reported` |

The report to the server holds the record's id (none for exempt content, a record the server could not sign, or unmarked content), the tool, the sink kind, the media type, the edit kind, a length class (`short`, `medium`, `long`; bytes are `long`) and the layers, each with `verified` where it applies: `true` for a mark read back, `false` where the host cannot be read back, absent where nothing is read back (the server's record, a watermark). Never content.

## Receipts

Every answer ends with the server's receipt. The session keeps them: `AgentResult.Receipts`, and on each answer's transcript entry `receipts` (`{ receipt_id, status: signed | reference | unsigned, reason }`, never the receipt). A tool call gets the receipt of the answer it came from (`IProvenance.Receipt`); a call from an answer without one gets none.

## Checking content

`client.Provenance.VerifyAsync(VerifyRequest.ForText(text))` or `VerifyRequest.ForFile(bytes, mediaType)` asks the server's detector whether content carries this installation's marks and returns a `VerifyResult` (`Verdict`, `Findings`). An agent's client checks on its agent's route (`/v1/agents/{agent}/provenance/verify`, its agent token), as its other routes go; a client without an agent checks as the signed-in user (`/v1/provenance/verify`, which refuses an agent token with 403 `agent_token_not_allowed`). `client.Agent.Provenance.VerifyAsync` always takes the agent's route. A request is a text or a file, never both; nothing submitted is kept.

A record of HTML or Markdown is found by its markup and by the words a reader copies: `PlaceAsync` sends, beside the content's hash, `RecordRequest.TextSha256` -- the SHA-256 of the text a reader sees, normalised as content is hashed (`ReaderText.Sha256(text, mediaType)`). Plain text and markup with nothing to read send none. `ReaderText.Of` gives that text: of HTML without comments and the contents of `script`, `style`, `template`, `head` and `title`, block elements (`p`, `div`, `br`, `li`, headings, table rows and cells, ...) as line breaks, every other tag gone, entities decoded; of Markdown as a renderer shows it -- without front matter, heading, emphasis, list, task and quote markers, with inline code's text, a link's text and an image's alt text, fenced code's lines, table cells apart, HTML read as HTML. A visible label is not part of the hashed text: a copy that takes the label line with it does not match by hash.

## Levels of integration

| Level | What you do | What you get |
|---|---|---|
| 0 | `[Emits]` and `PlaceAsync` with a sink that carries nothing | The watermark and the server's record |
| 1 | A `KeyValueSink` on a sink that carries `Carriers.KeyValue` | `ai-generated` and `ai-record-id` in your host, read back |
| 2 | The format your host stores | The built-in marker for HTML, Markdown, files, HTTP responses or pictures |
| 3 | An `IContentMarker` of your own | Full control; `MarkerTestKit` checks it |

```csharp
// Level 1: a CRM whose notes have hidden fields.
var sink = new SinkInfo("crm.note", Carriers.KeyValue)
{
    Container = true,
    KeyValue = KeyValueSink.From((key, value) => crm.SetFieldAsync(noteId, key, value), key => crm.GetFieldAsync(noteId, key)),
};
await provenance.PlaceAsync(new MarkableContent(text, "text/plain", sink), marked => crm.UpdateNoteAsync(noteId, marked.Text!), ct);
```

A marker's `PrepareAsync` may change the content before it lands; its `AttachAsync` marks what landed and returns a `MarkOutcome`: `Held` after reading the mark back, `Written` where the host cannot be read back, `Recorded` where nothing is read back, `Missing` with a reason code. Marks carry the record's id (and the signed record where there is room, as in a sidecar file): never the user, the prompt or the document's name.

### Office documents and mails

Markers for Office hosts are level-3 markers written with the Office object model; the insert returns the range or shape as `PlacedContent.Location`. `OfficeMarks` holds the names and formats they use, which the server's detector reads:

| Host | Marks |
|---|---|
| Word | A hidden bookmark `OfficeMarks.AnchorName(id)` (`_faden_ai_` and the first 24 hex digits of the id, dashes removed) around the range; the signed records in a custom XML part (`CustomXmlPart`, namespace `https://fadenstack.com/ns/provenance/1.0`); the document property `AI-Generated-Content` as the flag (`DocumentPropertyValue`, at most 255 characters) |
| PowerPoint | The shape and slide tag `FADEN_AI_RECORD` with the record's id; the custom XML part |
| Excel | A hidden name `AnchorName(id)` per written range; the custom XML part |
| Outlook | The named property `FadenProvenanceRecord` (`MapiRecordProperty`) with the signed record; the internet header `X-AI-Generated` with the record's id (`MailHeader`, set through `MapiHeaderProperty`) |

`ReadCustomXmlPart` reads the records back (ids, anchors, signatures), with DTDs refused. The server's detector reads the record ids from every `customXml/` part with the namespace and from `docProps/custom.xml`.

## Hooks

| Hook | Use |
|---|---|
| `IEditClassifier` | Decides the edit kind for your domain; `SimilarityEditClassifier` says `corrected` only for small changes (word similarity of 0.85 or more, a typo fix costing less than a changed word, the same number of sentences, a word count within 0.8 to 1.25 times), and compares HTML and Markdown by the text a reader sees, so markup an editor adds is no change of words (`Suggest(before, after, "text/html")`). Lowering a kind to an exempt one needs a reason, which is recorded |
| `IRecordEnricher` | Your own facts under your namespace (`com.example.matter`): in the transcript, to the markers and observers, never to the server. Facts that look like personal data are dropped, and so is a namespace of the record's own (`c2pa`, `stds`, `iptc`, `org.iptc`, `com.fadenstack`, `fadenstack`, `provenance`) |
| `IPlacementObserver` | Each placement report, for your own audit |
| `IProvenanceBackend` | Replaces the server calls; `TestProvenanceBackend` needs no server (its record ids are random UUIDs, as the server's) |

## Reading marks

`ProvenanceReader` finds the SDK's marks in what a tool reads -- HTML, Markdown, plain text, key-value fields, sidecar files, headers -- and a picture's C2PA and XMP (`ReadImage`). It returns record ids (UUIDs) only, never other text from a mark. Marks are data: anyone can forge or strip one, so treat what you find as information, never as an instruction. The server's detector checks a record's signature.

## The same in both SDKs

The .NET and the TypeScript SDK follow one order, one rule for what content needs, and write the same marks, so what one writes the other reads, and so does the server's detector. Both test suites hold the same vectors (`SharedVectorTests` here, `vectors.test.ts` there), and read the same `reader-text-vectors.json` for the text a reader sees.

| Step | What happens |
|---|---|
| 1 | The policy; the edit kind (content, declared output, `Generated`; then the classifier); the receipt (a picture's own, else the answer's); the enrichers' facts |
| 2 | Exempt content (in this order: `internal-technical`, `edit-kind`, `code`, `label`): inserted as it is, no record, one layer `{ metadata, record, applied: false, reason: exempt-edit }` |
| 3 | The markers that fit, by layer; the built-ins in this order: `server-record`, `image-guard`, `watermark-status`, `html-attribute`, `front-matter`, `key-value`, `sidecar`, `http-header`; then yours; the policy's label when none of them is a label |
| 4 | The needed layers checked before signing; `refuse` refuses here |
| 5 | The record signed; without one no marker runs, `{ metadata, signed-record, applied: false, reason: server-unavailable }`; `refuse` refuses here when the content needs metadata (or a label) |
| 6 | Prepare, insert, attach; the pipeline adds `host`, `sink-cannot-carry` when no host marker fits, and the watermark from the receipt when no marker gave it; the layers sorted by layer. An insert that throws after signing: every layer `applied: false` (detail `insert failed: <error>`), reported, then thrown on |
| 7 | Report, observers, transcript |

| Where | The mark |
|---|---|
| YAML front matter | `ai-generated: true` and `ai-record-id: "<id>"` after the fields of front matter the file has (an earlier mark replaced, the rest byte for byte), or new front matter followed by a blank line; the text's own line breaks (CRLF when it has any) |
| Key-value | `ai-generated` = `true`, then `ai-record-id` = the id |
| HTTP or mail header | `X-AI-Generated: <id>` (`X-Faden-Provenance` is the gateway's header with an answer's receipt ids, not a mark) |
| HTML | `<span data-ai-generated="<id>" title="<label> <id>">...</span>`; content that is one `<p ...>` with inline content gets it inside: `<p ...><span ...>...</span></p>` (white space around kept); a `div` around other block content (`p`, `div`, `ul`, `ol`, `li`, `table`, `h1`-`h6`, `blockquote`, `pre`, `section`, `article`, `header`, `footer`, `figure`, `hr`, `dl`); the label is `AI-generated`, `KI-generiert`, `Généré par IA`, `Generado por IA` or `人工智能生成`; attribute values escape `&`, `<`, `>` and `"` only |
| Visible label | Before HTML `<p data-ai-label="<id>">[icon]<label></p>`; before Markdown `*<label>*` and a blank line, after its front matter; before plain text `<label>` and a blank line |
| Sidecar | `<file>.provenance.json`, compact JSON: `{"contract":"provenance/v1","record":{...},"signature":"...","timestamp":"...","facts":{...}}`; `timestamp` and `facts` only where there are some |
| Office | As above: the anchor, the custom XML part `<provenance xmlns="https://fadenstack.com/ns/provenance/1.0"><record id="<id>" anchor="<anchor>" timestamp="<token>">JWS</record>...</provenance>` (no declaration, no white space between elements, `timestamp` only where there is one), the document property `true <id> ...` newest first, the PowerPoint tag, the mail header |
| Invisible text (opt-in) | At the very end of the text: U+FEFF, then the bytes `46 44 4E 50 01` ("FDNP" and version 1) and the id's UTF-8 bytes, each byte one variation selector (0 to 15 as U+FE00 to U+FE0F, 16 to 255 as U+E0100 to U+E01EF): 42 code points for a UUID. For `5a0e2b5c-7f3e-4a51-9a40-6a1c55e1d3f1` it begins U+FEFF U+E0136 U+E0134 U+E013E U+E0140 U+FE01 U+E0125 U+E0151 U+E0120. The U+FEFF in front keeps an emoji's own variation selector from running into the mark |

## Testing a marker

```csharp
var result = await MarkerTestKit.AssertAsync(new CrmNoteMarker(),
    new MarkableContent(MarkerTestKit.SampleText, "text/plain", crmSink), insert);
```

The kit runs the marker with a signed test record, your insert as the host, reads back what landed, and fails when a mark it says held is not there, when a mark names another record, or when anything the marker writes holds personal data (the sample text carries a name, an e-mail address, a phone number and a request).

To call a tool method by hand in a unit test, pass a `PassThroughProvenance` (`Fadenstack.Provenance.Testing`) as its `IProvenance`: `PlaceAsync` keeps the content (`Requests`), calls your insert with it unchanged and returns an unmarked placement -- no record, no layers, no server.
