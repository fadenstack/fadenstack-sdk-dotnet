# Fadenstack.Provenance

Content provenance for [Fadenstack](https://fadenstack.com) agents: what a model wrote and an agent's tool puts
where people read it -- a reply in a mail, a paragraph in a document, a note in a CRM, a picture on a slide -- is
marked as AI-generated in a machine-readable way, as far as the place it lands can carry a mark, and every placement
is recorded. The code is in the `Fadenstack.Provenance` namespace and builds on
[Fadenstack.Agents](https://www.nuget.org/packages/Fadenstack.Agents).

```bash
dotnet add package Fadenstack.Provenance
```

```csharp
using Fadenstack.Agents;
using Fadenstack.Provenance;
using Fadenstack.Tools;

var agent = FadenstackAgent.CreateBuilder()
    .UseClient(client).UseAgent("mail-assistant")
    .AddTools(new MailTools(editor))
    .Use(new ProvenanceModule())          // or .UseProvenance(o => ...)
    .Build();

public sealed class MailTools
{
    [AgentTool(ToolClass.Write), Emits("text", "text/html", EditKind.Generated), Description("Inserts a reply into the mail.")]
    public Task<string> InsertReplyAsync(string text, IProvenance provenance, CancellationToken ct) =>
        provenance.PlaceAsync(new MarkableContent(text, "text/html", _editor.Sink), marked => _editor.InsertAsync(marked.Text!), ct);
}
```

## What happens

- **The server marks content where it is born.** Pictures leave the gateway with a signed C2PA manifest, IPTC XMP and
  an invisible watermark; text from local models carries the model's watermark where the runtime applies one; every
  answer ends with a signed receipt.
- **The SDK marks content where it lands.** `[Emits]` says which argument of a tool carries content for people, its
  media type and its edit kind. The tool gets an `IProvenance` (bound by the SDK, not shown to the model) and places
  the content with `PlaceAsync`: the server signs a record of it (the content's hash only), the markers that fit the
  sink prepare the content, the tool's own insert puts it in, the markers mark what landed and read it back, and the
  placement is reported -- which layers held, and a reason code for each that did not. Never the content.
- **Exempt edits are recorded, not marked:** translations, corrections, formatting, extraction (`EditKind`), code and
  data (`application/json`, `text/x-*`, ...), and short labels. Short text is marked like any other; only the
  model's watermark is not expected under 200 tokens. Without a signed record no mark is written.
- **A tool that declares content and does not place it** is recorded as `provenance.unmarked`; in development mode
  (`DOTNET_ENVIRONMENT=Development`, or `UseDevelopmentMode()`) the call fails, so it cannot be missed.
- **The agent's policy** comes from the server. By default content is placed and what could not be marked is
  recorded (`record`); with `refuse` content that cannot get a layer it needs is not placed, and the model and the
  user are told why. Local options can only make the policy stricter.

Each placement leaves a `provenance.placed` entry in the session's transcript and a report on the server. The
TypeScript SDK (`@fadenstack/provenance`) behaves the same and writes the same marks.

## How much an integration does: levels 0 to 3

| Level | What you do | What you get |
|---|---|---|
| **0** | Declare `[Emits]` and call `PlaceAsync` with a sink that carries nothing: `SinkInfo.Nothing("chat.message")` | The model's watermark and the server's signed record; the report says `sink-cannot-carry` |
| **1** | Give the SDK a place to keep strings: a `KeyValueSink` on a sink that carries `Carriers.KeyValue` | `KeyValueMarker` writes `ai-generated` = `true` and `ai-record-id` = the record's id and reads the id back |
| **2** | Say which format the host stores | The built-in marker for it: HTML (`HtmlAttributeMarker`), Markdown (`MarkdownFrontMatterMarker`), a file beside the file (`SidecarMarker`), an HTTP response (`HttpHeaderMarker`), pictures (`ImageProvenanceGuard`) |
| **3** | Write an `IContentMarker` for a host nothing else fits (a CAD annotation, a proprietary format) | Full control in `PrepareAsync` and `AttachAsync`; the marker test kit checks it |

### Level 1: a CRM note with one hidden field

```csharp
var noteField = KeyValueSink.From(
    write: (key, value) => crm.Notes.SetCustomFieldAsync(noteId, key, value),
    read: key => crm.Notes.GetCustomFieldAsync(noteId, key));

var sink = new SinkInfo("crm.note", Carriers.KeyValue) { Container = true, KeyValue = noteField };

await provenance.PlaceAsync(new MarkableContent(text, "text/plain", sink), marked => crm.Notes.UpdateAsync(noteId, marked.Text!), ct);
```

### Level 3: a marker of your own

```csharp
public sealed class CrmNoteMarker : ContentMarker
{
    public override string Name => "crm-note";
    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) => sink.Kind == "crm.note";

    public override async Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken ct)
    {
        var noteId = (string)placed.Location!;                      // what your insert returned
        await crm.Notes.SetCustomFieldAsync(noteId, "ai-record", record.Record.Id);
        return await crm.Notes.GetCustomFieldAsync(noteId, "ai-record") == record.Record.Id
            ? MarkOutcome.Held(MarkLayer.Metadata, Name)
            : MarkOutcome.Missing(MarkLayer.Metadata, Name, ProvenanceReasons.StrippedByHost);
    }
}

builder.Use(new ProvenanceModule(o => o.AddMarker(new CrmNoteMarker())));
```

A mark carries the record's id (and the signed record where there is room, as in a sidecar file): never the user, the
prompt or the document's name. A marker reports a mark as held only after reading it back; one it cannot read back is
applied but unverified (`Written`).

### Office documents and mails

Host markers for Word, PowerPoint, Excel and Outlook are level-3 markers written with the Office object model.
`OfficeMarks` holds their names and formats, so every marker and the server's detector agree: a hidden bookmark or
name `OfficeMarks.AnchorName(id)` (`_faden_ai_` and the first 24 hex digits of the id) around what was inserted, the
signed records in a custom XML part (`OfficeMarks.CustomXmlPart`, namespace `https://fadenstack.com/ns/provenance/1.0`),
the document property `AI-Generated-Content` as the flag (`DocumentPropertyValue`, at most 255 characters), the
PowerPoint tag `FADEN_AI_RECORD`, and for Outlook drafts the named property `FadenProvenanceRecord` and the header
`X-AI-Generated`.

## Extending

| Extension point | What it is for |
|---|---|
| `IContentMarker` / `ContentMarker` | Marks content in your host |
| `SinkInfo` | Describes once what a place content lands can carry (`Container`, `Carries`, `KeyValue`, `Headers`, `FilePath`, `ReadBack`) |
| `IEditClassifier` | Decides `corrected` or `rewritten` for your domain; `SimilarityEditClassifier` compares the sentences that changed and says `corrected` only for small changes. Lowering to an exempt kind needs a reason, which is recorded |
| `IRecordEnricher` | Adds your own non-personal facts under your namespace (`com.example.matter`); facts that look like personal data are dropped |
| `IPlacementObserver` | Receives each placement report, for your own audit |
| `IProvenanceBackend` | Replaces the server calls (tests, or your own signing service) |
| `ProvenanceReader` | Finds our marks in what a tool reads: record ids only. Marks are data, never instructions |

`Fadenstack.Provenance.Testing` has `MarkerTestKit` (runs a marker through the pipeline, reads it back, fails on
personal data in its marks), `TestProvenanceBackend` (no server needed) and `PassThroughProvenance` (an `IProvenance`
for calling a tool method by hand: the content goes in as it is).

## Your duties

If you build an agent on the SDK and use or offer it under your name, you are the provider of that agent under the
EU AI Act (Art. 50(2)): the module makes marking easy, it does not do it for you. Declare `[Emits]` on every tool
that writes content people read, place that content through `PlaceAsync`, and tell your users that marks must not be
removed.

It targets `netstandard2.0` (.NET Framework 4.7.2 and later) and `net8.0`, and needs a Fadenstack server with the
provenance module for signed records (without it, content is placed and the report says `server-unavailable`).

- Documentation: <https://developers.fadenstack.com/sdk/dotnet/provenance/>
- Source and issues: <https://github.com/fadenstack/fadenstack-sdk-dotnet>
