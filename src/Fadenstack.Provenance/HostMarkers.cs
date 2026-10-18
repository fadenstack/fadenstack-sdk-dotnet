using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fadenstack.Models;
using Fadenstack.UI;

namespace Fadenstack.Provenance;

/// <summary>
/// HTML inserted into an editor or page: wrapped in an element with <c>data-ai-generated</c> (the record's id) and a
/// <c>title</c> ("AI-generated" in the user's language, and the id). Editors that keep <c>data-</c> attributes
/// (TinyMCE, CKEditor 5) keep the first; Gmail drops it and keeps the title through to the recipient. Read back
/// after insertion, it records which held. For sinks that carry <see cref="Carriers.HtmlAttribute"/>.
/// </summary>
public sealed class HtmlAttributeMarker : ContentMarker
{
    public const string MarkerName = "html-attribute";

    public const string Attribute = "data-ai-generated";

    private static readonly Regex Block = new(@"<(p|div|ul|ol|li|table|h[1-6]|blockquote|pre|section|article|header|footer|figure|hr)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) => Is(mediaType, "text/html") && sink.CanCarry(Carriers.HtmlAttribute);

    public override Task<MarkableContent> PrepareAsync(MarkableContent content, SignedRecord record, CancellationToken cancellationToken)
    {
        var id = record.Record.Id;
        var html = content.Text ?? "";
        var tag = Block.IsMatch(html) ? "div" : "span";
        var title = Title(content.Texts ?? UiStrings.Current, id);
        return Task.FromResult(content.WithText(
            $"<{tag} {Attribute}=\"{WebUtility.HtmlEncode(id)}\" title=\"{WebUtility.HtmlEncode(title)}\">{html}</{tag}>"));
    }

    /// <summary>The <c>title</c> a mark carries: the label in the user's language, a space, the record's id.</summary>
    public static string Title(UiStrings texts, string recordId) => $"{texts.ProvenanceLabel} {recordId}";

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
/// Markdown files: <c>ai-generated: true</c> and <c>ai-record-id</c> in the YAML front matter, merged into front
/// matter the file already has (Nextcloud Text shows it as a metadata box and keeps it). For sinks that carry
/// <see cref="Carriers.FrontMatter"/>.
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

    /// <summary>The text with the two keys in its front matter: added to front matter it has, or new front matter before it.</summary>
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
/// Any host that can keep one string for the content (a hidden CRM field, a ticket's custom field, an object's
/// metadata): the record's id under <see cref="IdKey"/> and, where it fits, the signed record under
/// <see cref="SignedKey"/>, read back to check they held. The integrator passes the place as <see cref="SinkInfo.KeyValue"/>
/// on a sink that carries <see cref="Carriers.KeyValue"/>.
/// </summary>
public sealed class KeyValueMarker : ContentMarker
{
    public const string MarkerName = "key-value";

    public const string DefaultIdKey = "ai-generated-record";

    public const string DefaultSignedKey = "ai-generated-signed";

    /// <param name="idKey">The key the record's id goes under.</param>
    /// <param name="signedKey">The key the signed record goes under, where it fits; null for the id alone.</param>
    public KeyValueMarker(string idKey = DefaultIdKey, string? signedKey = DefaultSignedKey)
    {
        IdKey = string.IsNullOrWhiteSpace(idKey) ? throw new ArgumentException("A key is needed.", nameof(idKey)) : idKey;
        SignedKey = signedKey;
    }

    public string IdKey { get; }

    public string? SignedKey { get; }

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) => sink.KeyValue is not null && sink.CanCarry(Carriers.KeyValue);

    public override async Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken)
    {
        var sink = placed.Sink.KeyValue!;
        var id = record.Record.Id;
        await sink.WriteAsync(IdKey, id, cancellationToken).ConfigureAwait(false);
        var detail = "record id";
        if (SignedKey is not null)
        {
            var signed = ProvenanceJson.Compact(record);
            if (sink.MaxValueLength is not { } max || signed.Length <= max)
            {
                await sink.WriteAsync(SignedKey, signed, cancellationToken).ConfigureAwait(false);
                detail = "record id, signed record";
            }
        }
        try
        {
            var back = await sink.ReadAsync(IdKey, cancellationToken).ConfigureAwait(false);
            return back == id
                ? MarkOutcome.Held(MarkLayer.Metadata, MarkerName, detail)
                : MarkOutcome.Missing(MarkLayer.Metadata, MarkerName, ProvenanceReasons.StrippedByHost, "the host did not keep the value");
        }
        catch (NotSupportedException)
        {
            return MarkOutcome.Written(MarkLayer.Metadata, MarkerName, detail + "; not read back");
        }
    }
}

/// <summary>
/// Files written where nothing else fits: the signed record in a file beside the file, <c>&lt;file&gt;.provenance.json</c>.
/// The integrator gives the file as <see cref="SinkInfo.FilePath"/> on a sink that carries <see cref="Carriers.Sidecar"/>.
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
/// Content an integration serves over HTTP: <c>X-Faden-Provenance</c> with the record's id on the response. The
/// integrator gives the response's headers as <see cref="SinkInfo.Headers"/> on a sink that carries
/// <see cref="Carriers.HttpHeader"/>.
/// </summary>
public sealed class HttpHeaderMarker : ContentMarker
{
    public const string MarkerName = "http-header";

    public const string Header = Http.ProvenanceHeader.Name;

    public override string Name => MarkerName;

    public override MarkLayer Layer => MarkLayer.Metadata;

    public override bool Supports(SinkInfo sink, string mediaType) => sink.Headers is not null && sink.CanCarry(Carriers.HttpHeader);

    public override async Task<MarkOutcome> AttachAsync(PlacedContent placed, SignedRecord record, CancellationToken cancellationToken)
    {
        var headers = placed.Sink.Headers!;
        await headers.WriteAsync(Header, record.Record.Id, cancellationToken).ConfigureAwait(false);
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
