using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fadenstack.Models;
using Fadenstack.UI;

namespace Fadenstack.Provenance;

/// <summary>
/// HTML inserted into an editor or page: wrapped in one element with <c>data-ai-generated</c> (the record's id) and a
/// <c>title</c> ("AI-generated" in the user's language, a space, the id): <see cref="Wrap"/>. Editors that keep
/// <c>data-</c> attributes (TinyMCE, CKEditor 5) keep the first; Gmail drops it and keeps the title through to the
/// recipient. Read back after insertion, it records which held: method <c>html-attribute</c> when the attribute did,
/// <c>html-title</c> when only the title did. For <c>text/html</c> into sinks that carry
/// <see cref="Carriers.HtmlAttribute"/> or <see cref="Carriers.HtmlTitle"/>; a tool with plain text escapes it into HTML first.
/// </summary>
public sealed class HtmlAttributeMarker : ContentMarker
{
    public const string MarkerName = "html-attribute";

    public const string Attribute = "data-ai-generated";

    private static readonly Regex Block = new(@"<(p|div|ul|ol|li|table|h[1-6]|blockquote|pre|section|article|header|footer|figure|hr|dl)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) =>
        Is(mediaType, "text/html") && (sink.CanCarry(Carriers.HtmlAttribute) || sink.CanCarry(Carriers.HtmlTitle));

    public override Task<MarkableContent> PrepareAsync(MarkableContent content, SignedRecord record, CancellationToken cancellationToken) =>
        Task.FromResult(content.WithText(Wrap(content.Text ?? "", record.Record.Id, Title(content.Texts ?? UiStrings.Current, record.Record.Id))));

    /// <summary>
    /// The element a mark wraps HTML in: <c>&lt;span data-ai-generated="id" title="label id"&gt;</c> (a <c>div</c> around
    /// block content), the attribute values escaped for <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c> and <c>"</c> only. The
    /// TypeScript SDK's <c>wrapHtml</c> writes the same.
    /// </summary>
    public static string Wrap(string html, string recordId, string title)
    {
        var tag = Block.IsMatch(html) ? "div" : "span";
        return $"<{tag} {Attribute}=\"{HtmlText.Attribute(recordId)}\" title=\"{HtmlText.Attribute(title)}\">{html}</{tag}>";
    }

    /// <summary>The <c>title</c> a mark carries: the label in the user's language, a space, the record's id.</summary>
    public static string Title(UiStrings texts, string recordId) => texts.AiGeneratedTitle(recordId);

    public override async Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken)
    {
        var html = await placed.ReadBackAsync(cancellationToken).ConfigureAwait(false);
        if (html is null)
        {
            return MarkOutcome.Written(MarkLayer.Metadata, MarkerName, "not read back");
        }
        var found = ProvenanceReader.Read(html, "text/html").Where(m => m.RecordId == record.Record.Id).Select(m => m.Method).ToList();
        var data = found.Contains(MarkerName);
        var title = found.Contains(ProvenanceReader.HtmlTitle);
        if (data)
        {
            return MarkOutcome.Held(MarkLayer.Metadata, MarkerName, title ? "data-ai-generated, title" : "data-ai-generated; title dropped");
        }
        return title
            ? MarkOutcome.Held(MarkLayer.Metadata, ProvenanceReader.HtmlTitle, "title; data-ai-generated dropped")
            : MarkOutcome.Missing(MarkLayer.Metadata, MarkerName, ProvenanceReasons.StrippedByHost, "data-ai-generated and title dropped");
    }
}

/// <summary>
/// Markdown files: <c>ai-generated: true</c> and <c>ai-record-id: "&lt;id&gt;"</c> in the YAML front matter, merged into
/// front matter the file already has (<see cref="Merge"/>; Nextcloud Text shows it as a metadata box and keeps it). For
/// sinks that carry <see cref="Carriers.FrontMatter"/>, for a whole file, never for a snippet inserted into one.
/// </summary>
public sealed class MarkdownFrontMatterMarker : ContentMarker
{
    public const string MarkerName = "front-matter";

    public const string GeneratedKey = "ai-generated";

    public const string RecordKey = "ai-record-id";

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) => Is(mediaType, "text/markdown") && sink.CanCarry(Carriers.FrontMatter);

    public override Task<MarkableContent> PrepareAsync(MarkableContent content, SignedRecord record, CancellationToken cancellationToken) =>
        Task.FromResult(content.WithText(Merge(content.Text ?? "", record.Record.Id)));

    public override async Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken)
    {
        var text = await placed.ReadBackAsync(cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return MarkOutcome.Written(MarkLayer.Metadata, MarkerName, "not read back");
        }
        return ProvenanceReader.Read(text, "text/markdown").Any(m => m.Method == MarkerName && m.RecordId == record.Record.Id)
            ? MarkOutcome.Held(MarkLayer.Metadata, MarkerName)
            : MarkOutcome.Missing(MarkLayer.Metadata, MarkerName, ProvenanceReasons.StrippedByHost);
    }

    /// <summary>
    /// The text with the two keys in its front matter: added after the fields of front matter it has (an earlier mark of
    /// ours replaced; the other fields, the closing line and the body byte for byte), or new front matter and a blank
    /// line before it. The text's own line breaks (CRLF when it has any). The TypeScript SDK's <c>withFrontMatter</c>
    /// writes the same.
    /// </summary>
    public static string Merge(string text, string recordId)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var bom = text.Length > 0 && text[0] == '﻿' ? "﻿" : "";
        var body = text.Substring(bom.Length);
        var ours = new[] { $"{GeneratedKey}: true", $"{RecordKey}: \"{recordId}\"" };
        if (FrontMatter.Split(body) is { } existing)
        {
            var kept = existing.Lines.Where(l => !IsKey(l, GeneratedKey) && !IsKey(l, RecordKey)).Concat(ours);
            return bom + "---" + newline + string.Join(newline, kept) + newline + existing.Close + existing.Rest;
        }
        return bom + "---" + newline + string.Join(newline, ours) + newline + "---" + newline + newline + body;
    }

    private static bool IsKey(string line, string key) => line.StartsWith(key + ":", StringComparison.Ordinal);
}

/// <summary>YAML front matter at the start of a Markdown text: <c>---</c>, the lines, <c>---</c> (or <c>...</c>).</summary>
internal sealed record FrontMatter(IReadOnlyList<string> Lines, string Close, string Rest)
{
    public static FrontMatter? Split(string text)
    {
        var first = text.IndexOf('\n');
        if (first < 0 || text.Substring(0, first).TrimEnd('\r') != "---")
        {
            return null;
        }
        var lines = new List<string>();
        var at = first + 1;
        while (at < text.Length)
        {
            var end = text.IndexOf('\n', at);
            var line = (end < 0 ? text.Substring(at) : text.Substring(at, end - at)).TrimEnd('\r');
            var next = end < 0 ? text.Length : end + 1;
            if (line is "---" or "...")
            {
                return new FrontMatter(lines, text.Substring(at, next - at), text.Substring(next));
            }
            lines.Add(line);
            at = next;
        }
        return null; // never closed: not front matter
    }

    /// <summary>The value of a top-level key, without quotes; null when it has none.</summary>
    public string? Value(string key)
    {
        var line = Lines.FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        return line?.Substring(key.Length + 1).Trim().Trim('"', '\'');
    }
}

/// <summary>
/// Any host that can keep strings for the content (a hidden CRM field, a ticket's custom field, an object's
/// metadata): after the insert, <c>ai-generated</c> = <c>true</c>, then <c>ai-record-id</c> = the record's id -- the
/// same two keys as front matter -- and the id read back to check it held. A host with a single field keeps the last
/// write, the id. The server keeps the signed record; the id finds it. The integrator passes the place as
/// <see cref="SinkInfo.KeyValue"/> on a sink that carries <see cref="Carriers.KeyValue"/>.
/// </summary>
public sealed class KeyValueMarker : ContentMarker
{
    public const string MarkerName = "key-value";

    /// <summary>The flag's key: <c>ai-generated</c>, with the value <c>true</c>.</summary>
    public const string GeneratedKey = MarkdownFrontMatterMarker.GeneratedKey;

    /// <summary>The record id's key: <c>ai-record-id</c>.</summary>
    public const string RecordKey = MarkdownFrontMatterMarker.RecordKey;

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) => sink.KeyValue is not null && sink.CanCarry(Carriers.KeyValue);

    public override async Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken)
    {
        var sink = placed.Sink.KeyValue!;
        var id = record.Record.Id;
        await sink.WriteAsync(GeneratedKey, "true", cancellationToken).ConfigureAwait(false);
        await sink.WriteAsync(RecordKey, id, cancellationToken).ConfigureAwait(false);
        try
        {
            var back = await sink.ReadAsync(RecordKey, cancellationToken).ConfigureAwait(false);
            return back?.Trim() == id
                ? MarkOutcome.Held(MarkLayer.Metadata, MarkerName)
                : MarkOutcome.Missing(MarkLayer.Metadata, MarkerName, ProvenanceReasons.StrippedByHost, "the host did not keep the value");
        }
        catch (NotSupportedException)
        {
            return MarkOutcome.Written(MarkLayer.Metadata, MarkerName, "not read back");
        }
    }
}

/// <summary>
/// Files written where nothing else fits: the signed record in a file beside the file, <c>&lt;file&gt;.provenance.json</c>,
/// as compact JSON: the contract, the signed record (<c>record</c>, <c>signature</c>, <c>timestamp</c>) and the
/// integrator's facts where there are any; the TypeScript SDK's <c>sidecarDocument</c> holds the same. The
/// integrator gives the file as <see cref="SinkInfo.FilePath"/> on a sink that carries <see cref="Carriers.Sidecar"/>.
/// </summary>
public sealed class SidecarMarker : ContentMarker
{
    public const string MarkerName = "sidecar";

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) => sink.FilePath is not null && sink.CanCarry(Carriers.Sidecar);

    /// <summary>Where the record of <paramref name="filePath"/> goes: <c>report.pdf</c> → <c>report.pdf.provenance.json</c>.</summary>
    public static string PathFor(string filePath) => filePath + ".provenance.json";

    public override async Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken)
    {
        var path = PathFor(placed.Sink.FilePath!);
        var json = ProvenanceJson.Compact(new SidecarFile
        {
            Record = record.Record, Signature = record.Signature, Timestamp = record.Timestamp,
            Facts = placed.Content.Facts.Count == 0 ? null : placed.Content.Facts,
        });
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(json);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return MarkOutcome.Missing(MarkLayer.Metadata, MarkerName, null, "not written: " + e.GetType().Name);
        }
        return ProvenanceReader.ReadSidecar(placed.Sink.FilePath!)?.RecordId == record.Record.Id
            ? MarkOutcome.Held(MarkLayer.Metadata, MarkerName)
            : MarkOutcome.Missing(MarkLayer.Metadata, MarkerName, ProvenanceReasons.StrippedByHost);
    }

    /// <summary>A sidecar file: the signed record, and the integrator's facts.</summary>
    internal sealed record SidecarFile
    {
        public string Contract { get; init; } = ProvenanceContract.Version;

        public ProvenanceRecord Record { get; init; } = new();

        public string Signature { get; init; } = "";

        public string? Timestamp { get; init; }

        public IReadOnlyDictionary<string, string>? Facts { get; init; }
    }
}

/// <summary>
/// Content an integration serves over HTTP: <c>X-AI-Generated</c> with the record's id on the response, set before the
/// host sends it (in <see cref="PrepareAsync"/>) and read back after. (<c>X-Faden-Provenance</c> is the gateway's
/// header with an answer's receipt ids, not a mark.) The integrator gives the response's headers as
/// <see cref="SinkInfo.Headers"/> on a sink that carries <see cref="Carriers.HttpHeader"/>.
/// </summary>
public sealed class HttpHeaderMarker : ContentMarker
{
    public const string MarkerName = "http-header";

    /// <summary>The header that names the record: the same as an Outlook draft's (<see cref="OfficeMarks.MailHeader"/>).</summary>
    public const string Header = "X-AI-Generated";

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) => sink.Headers is not null && sink.CanCarry(Carriers.HttpHeader);

    public override async Task<MarkableContent> PrepareAsync(MarkableContent content, SignedRecord record, CancellationToken cancellationToken)
    {
        await content.Sink.Headers!.WriteAsync(Header, record.Record.Id, cancellationToken).ConfigureAwait(false);
        return content;
    }

    public override async Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken)
    {
        var headers = placed.Sink.Headers!;
        try
        {
            return ProvenanceReader.ReadHeader(await headers.ReadAsync(Header, cancellationToken).ConfigureAwait(false))?.RecordId == record.Record.Id
                ? MarkOutcome.Held(MarkLayer.Metadata, MarkerName)
                : MarkOutcome.Missing(MarkLayer.Metadata, MarkerName, ProvenanceReasons.StrippedByHost);
        }
        catch (NotSupportedException)
        {
            return MarkOutcome.Written(MarkLayer.Metadata, MarkerName, "not read back");
        }
    }
}
