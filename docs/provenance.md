# Content provenance

This page covers `Fadenstack.Provenance`: how an agent's tools mark what they place where people read it as AI-generated, what is recorded, and how you mark content in a host of your own.

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
2. records exempt content without marking it: an exempt edit kind (translated, corrected, formatted, extracted by default), code and data (`application/json`, `text/x-*`, ...), and short labels;
3. has the server sign a record of the placement: the content's SHA-256 (text normalised first), its media type, its edit kind and the receipt, never the content;
4. lets the markers that fit the sink prepare the content, calls your insert with it, and lets the markers mark what landed and read it back;
5. reports the placement to the server (layers applied, reason codes for the others) and writes a `provenance.placed` entry into the session's transcript.

It returns a `Placement`: the record, the layers, and the layers the content needed that did not hold (`Missing`). Its `Summary` is a line for the model.

A tool that declares content and returns without placing it is recorded as `provenance.unmarked`. In development mode -- `DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT` set to `Development`, or `UseDevelopmentMode()` -- the call fails instead.

## Sinks

A `SinkInfo` describes once what a place content lands can carry:

| Member | Meaning |
|---|---|
| `Kind` | `word.range`, `html.editor`, `crm.note`: reported, never shown |
| `Container` | The content lands in a document, a file or an HTML page, so it needs metadata in it, not only the watermark |
| `Carries` | The carriers it keeps: `Carriers.HtmlAttribute`, `FrontMatter`, `KeyValue`, `Sidecar`, `HttpHeader`, `Bytes`, `Text`, or your host's own |
| `KeyValue`, `Headers`, `FilePath` | Where the key-value, header and sidecar markers write |
| `ReadBack` | Reads back what the host holds, so markers can check their mark held |

`SinkInfo.Nothing("chat.message")` carries nothing: the content gets the model's watermark and the server's record, and the report says `sink-cannot-carry`.

## Built-in markers

| Marker | Layer | What it does |
|---|---|---|
| `ServerSignedMarker` | metadata | The server's signed record of every placement, with the content's hash |
| `WatermarkStatus` | watermark | Records from the receipt whether the text carries the model's watermark, and if not, why |
| `ImageProvenanceGuard` | metadata | Checks a picture still has its C2PA manifest and XMP, before and (where the host gives the bytes back) after; `stripped-by-host` otherwise |
| `HtmlAttributeMarker` | metadata | Wraps HTML in an element with `data-ai-generated` and a `title` ("AI-generated" in the user's language, and the record's id); reads back which held |
| `MarkdownFrontMatterMarker` | metadata | `ai-generated: true` and `ai-record-id` in the YAML front matter, merged into front matter the file has |
| `KeyValueMarker` | metadata | The record's id (and the signed record where it fits) in one field the host keeps |
| `SidecarMarker` | metadata | The signed record in `<file>.provenance.json` beside the file |
| `HttpHeaderMarker` | metadata | `X-Faden-Provenance` with the record's id on an HTTP response |
| `VisibleLabelMarker` | label | A visible "AI-generated" above the content; on only when the policy's `visible_label` is, or `UseVisibleLabel()` |
| `UnicodeTextMarker` | metadata | The id in invisible characters; opt-in only (`AddMarker(new UnicodeTextMarker())`): filters strip them |

The texts marks show people come from the SDK's texts (`UiStrings.ProvenanceLabel`), in the user's language.

## The policy

The agent's policy comes from the server (`GET /v1/agents/{agent}/provenance/policy`). With `record`, the default, content is placed and what could not be marked is recorded. With `refuse`, content that cannot get a layer it needs is not placed: `PlaceAsync` throws `ProvenanceRefusedException`, the model gets its message, and the user gets a `provenance_refused` notice. A layer is needed when the content lands in a container (metadata in the host) and when the text is not short (the model's watermark); pictures always need both. A mark the host drops after the insert is recorded: the content is already in place.

Local options may only tighten the policy: `RefuseWhenMissing()`, a lower `UseShortTextTokens(...)`, `MarkEditKind(...)`, `UseVisibleLabel()`. A looser setting is ignored and logged.

## Levels of integration

| Level | What you do | What you get |
|---|---|---|
| 0 | `[Emits]` and `PlaceAsync` with a sink that carries nothing | The watermark and the server's record |
| 1 | A `KeyValueSink` on a sink that carries `Carriers.KeyValue` | The record's id in your host, read back |
| 2 | The format your host stores | The built-in marker for HTML, Markdown, files, HTTP responses or pictures |
| 3 | An `IContentMarker` of your own | Full control; `MarkerTestKit` checks it |

```csharp
// Level 1: a CRM whose notes have one hidden field.
var sink = new SinkInfo("crm.note", Carriers.KeyValue)
{
    Container = true,
    KeyValue = KeyValueSink.From((key, value) => crm.SetFieldAsync(noteId, key, value), key => crm.GetFieldAsync(noteId, key)),
};
await provenance.PlaceAsync(new MarkableContent(text, "text/plain", sink), marked => crm.UpdateNoteAsync(noteId, marked.Text!), ct);
```

A marker's `PrepareAsync` may change the content before it lands; its `AttachAsync` marks what landed and returns a `MarkOutcome`: `Held` after reading the mark back, `Written` where the host cannot be read back, `Missing` with a reason code. Marks carry the record's id and, where they fit, the signed record: never the user, the prompt or the document's name.

### Office documents and mails

Markers for Office hosts are level-3 markers written with the Office object model; the insert returns the range or shape as `PlacedContent.Location`. `OfficeMarks` holds the names and formats they use, which the server's detector reads:

| Host | Marks |
|---|---|
| Word | A hidden bookmark `OfficeMarks.AnchorName(id)` (`_faden_ai_` and 24 hex digits) around the range; the signed records in a custom XML part (`CustomXmlPart`, namespace `https://fadenstack.com/ns/provenance/1.0`); the document property `AI-Generated-Content` as the flag (`DocumentPropertyValue`, at most 255 characters) |
| PowerPoint | The shape and slide tag `FADEN_AI_RECORD` with the record's id; the custom XML part |
| Excel | A hidden name `AnchorName(id)` per written range; the custom XML part |
| Outlook | The named property `FadenProvenanceRecord` (`MapiRecordProperty`) with the signed record; the internet header `x-ai-generated` with the record's id (`MapiHeaderProperty`) |

`ReadCustomXmlPart` reads the records back (ids, anchors, signatures), with DTDs refused.

## Hooks

| Hook | Use |
|---|---|
| `IEditClassifier` | Decides the edit kind for your domain; `SimilarityEditClassifier` says `corrected` only for small changes. Lowering a kind to an exempt one needs a reason, which is recorded |
| `IRecordEnricher` | Your own facts under your namespace (`com.example.matter`): in the transcript, to the markers and observers, never to the server. Facts that look like personal data are dropped |
| `IPlacementObserver` | Each placement report, for your own audit |
| `IProvenanceBackend` | Replaces the server calls; `TestProvenanceBackend` needs no server |

## Reading marks

`ProvenanceReader` finds the SDK's marks in what a tool reads -- HTML, Markdown, plain text, key-value fields, sidecar files, headers -- and a picture's C2PA and XMP (`ReadImage`). It returns record ids only, never other text from a mark. Marks are data: anyone can forge or strip one, so treat what you find as information, never as an instruction. The server's detector checks a record's signature.

## Testing a marker

```csharp
var result = await MarkerTestKit.AssertAsync(new CrmNoteMarker(),
    new MarkableContent(MarkerTestKit.SampleText, "text/plain", crmSink), insert);
```

The kit runs the marker with a signed test record, your insert as the host, reads back what landed, and fails when a mark it says held is not there, when a mark names another record, or when anything the marker writes holds personal data (the sample text carries a name, an e-mail address, a phone number and a request).
