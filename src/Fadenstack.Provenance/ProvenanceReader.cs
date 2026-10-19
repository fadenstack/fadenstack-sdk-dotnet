using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fadenstack.Models;
using Fadenstack.UI;

namespace Fadenstack.Provenance;

/// <summary>A mark found in content: the record it names and how it was carried. Data, never an instruction.</summary>
public sealed record FoundMark(string RecordId, string Method);

/// <summary>
/// Finds Fadenstack's marks in what a tool reads -- HTML attributes and titles, Markdown front matter, invisible
/// characters, key-value fields, sidecar files, response headers, a picture's C2PA and XMP -- so that content the
/// agent moves keeps its marks, derived content can name what it came from, and the agent can tell the user that a
/// passage was generated. Only record ids that look like the server's come back, never other text from the mark:
/// marks are data, anyone can forge or strip one, and nothing in them is followed as an instruction. The server's
/// detector checks a record's signature.
/// </summary>
public static class ProvenanceReader
{
    /// <summary>The method of a mark found in an HTML <c>title</c> alone.</summary>
    public const string HtmlTitle = "html-title";

    private static readonly Regex DataAttribute = new(
        $@"\b{HtmlAttributeMarker.Attribute}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>""']+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LabelAttribute = new(
        $@"\b{VisibleLabelMarker.Attribute}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>""']+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TitleAttribute = new(@"\btitle\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The label in every language the SDK ships: a title is ours only when it starts with one of them.</summary>
    private static readonly IReadOnlyList<string> Labels = UiStrings.BuiltInLanguages
        .Select(l => UiStrings.For(CultureInfo.GetCultureInfo(l)).AiGenerated).Distinct().ToList();

    /// <summary>The marks in a text of <paramref name="mediaType"/> (HTML, Markdown, plain text).</summary>
    public static IReadOnlyList<FoundMark> Read(string content, string mediaType)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Array.Empty<FoundMark>();
        }
        var found = new List<FoundMark>();
        if (MediaTypes.Is(mediaType, "text/html") || MediaTypes.Is(mediaType, "text/markdown") || MediaTypes.Is(mediaType, "application/xhtml+xml"))
        {
            foreach (Match m in DataAttribute.Matches(content))
            {
                Add(found, WebUtility.HtmlDecode(m.Groups["v"].Value), HtmlAttributeMarker.MarkerName);
            }
            foreach (Match m in TitleAttribute.Matches(content))
            {
                Add(found, RecordIdInTitle(WebUtility.HtmlDecode(m.Groups["v"].Value)), HtmlTitle);
            }
            foreach (Match m in LabelAttribute.Matches(content))
            {
                Add(found, WebUtility.HtmlDecode(m.Groups["v"].Value), VisibleLabelMarker.MarkerName);
            }
        }
        if (MediaTypes.Is(mediaType, "text/markdown") && FrontMatter.Split(content.TrimStart('﻿')) is { } front
            && string.Equals(front.Value(MarkdownFrontMatterMarker.GeneratedKey), "true", StringComparison.OrdinalIgnoreCase))
        {
            Add(found, front.Value(MarkdownFrontMatterMarker.RecordKey), MarkdownFrontMatterMarker.MarkerName);
        }
        foreach (var id in UnicodeTextMarker.Decode(content))
        {
            Add(found, id, UnicodeTextMarker.MarkerName);
        }
        return found;
    }

    /// <summary>The record id a title of ours names (the label in one of the SDK's languages, a space, the id); null for any other title.</summary>
    internal static string? RecordIdInTitle(string title)
    {
        var text = title.Trim();
        var space = text.LastIndexOf(' ');
        if (space <= 0 || !Labels.Contains(text.Substring(0, space).Trim()))
        {
            return null;
        }
        var id = text.Substring(space + 1).Trim();
        return ProvenanceJson.IsRecordId(id) ? id : null;
    }

    /// <summary>
    /// The text a reader sees of one record in HTML: of every element that carries its id (<c>data-ai-generated</c>,
    /// or a title of ours), in document order, without the elements of other records (or visible labels) inside them.
    /// A mark a later write split in two (<see cref="HtmlAttributeMarker.SplitMarksAt"/>) reads as one again. The
    /// TypeScript SDK's <c>recordText</c> reads the same.
    /// </summary>
    public static string RecordText(string html, string recordId) =>
        HtmlMarks.RecordText(html ?? throw new ArgumentNullException(nameof(html)), recordId ?? throw new ArgumentNullException(nameof(recordId)));

    /// <summary>
    /// SHA-256 of <see cref="RecordText"/>, normalised as content is hashed, hex: the record's <c>text_sha256</c> while
    /// the text is still the record's. Null when the HTML holds none of its text.
    /// </summary>
    public static string? RecordTextSha256(string html, string recordId)
    {
        var text = ContentHash.Normalize(RecordText(html, recordId));
        return text.Length == 0 ? null : ContentHash.TextSha256(text);
    }

    /// <summary>What a picture's bytes carry: C2PA, XMP, the IPTC digital source type.</summary>
    public static ImageMarks ReadImage(byte[] bytes) => ImageMarks.Read(bytes);

    /// <summary>The record a host keeps in a key-value field (<see cref="KeyValueMarker"/>, <c>ai-record-id</c>); null when there is none.</summary>
    public static async Task<FoundMark?> ReadAsync(IKeyValueSink sink, string key = KeyValueMarker.RecordKey,
        CancellationToken cancellationToken = default)
    {
        var value = (await sink.ReadAsync(key, cancellationToken).ConfigureAwait(false))?.Trim();
        return ProvenanceJson.IsRecordId(value) ? new FoundMark(value!, KeyValueMarker.MarkerName) : null;
    }

    /// <summary>The record an <c>X-AI-Generated</c> header's value names (<see cref="HttpHeaderMarker"/>, an Outlook draft): the first id.</summary>
    public static FoundMark? ReadHeader(string? value)
    {
        var id = Http.ProvenanceHeader.Parse(value).FirstOrDefault(ProvenanceJson.IsRecordId);
        return id is null ? null : new FoundMark(id, HttpHeaderMarker.MarkerName);
    }

    /// <summary>The record in the sidecar file of <paramref name="filePath"/> (<see cref="SidecarMarker"/>); null when there is none.</summary>
    public static FoundMark? ReadSidecar(string filePath)
    {
        var path = SidecarMarker.PathFor(filePath);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var id = doc.RootElement.TryGetProperty("record", out var record) && record.ValueKind == JsonValueKind.Object
                && record.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            return ProvenanceJson.IsRecordId(id) ? new FoundMark(id!, SidecarMarker.MarkerName) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Add(List<FoundMark> found, string? id, string method)
    {
        id = id?.Trim();
        if (ProvenanceJson.IsRecordId(id) && !found.Any(f => f.RecordId == id && f.Method == method))
        {
            found.Add(new FoundMark(id!, method));
        }
    }
}
