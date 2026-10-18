using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        .Select(l => UiStrings.For(CultureInfo.GetCultureInfo(l)).ProvenanceLabel).Distinct().ToList();

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
                var title = WebUtility.HtmlDecode(m.Groups["v"].Value).Trim();
                var space = title.LastIndexOf(' ');
                if (space > 0 && Labels.Contains(title.Substring(0, space).Trim()))
                {
                    Add(found, title.Substring(space + 1), HtmlTitle);
                }
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

    /// <summary>What a picture's bytes carry: C2PA, XMP, the IPTC digital source type.</summary>
    public static ImageMarks ReadImage(byte[] bytes) => ImageMarks.Read(bytes);

    /// <summary>The record a host keeps in a key-value field (<see cref="KeyValueMarker"/>); null when there is none.</summary>
    public static async Task<FoundMark?> ReadAsync(IKeyValueSink sink, string key = KeyValueMarker.DefaultIdKey,
        CancellationToken cancellationToken = default)
    {
        var value = (await sink.ReadAsync(key, cancellationToken).ConfigureAwait(false))?.Trim();
        return ProvenanceJson.IsRecordId(value) ? new FoundMark(value!, KeyValueMarker.MarkerName) : null;
    }

    /// <summary>The record an HTTP response names in <c>X-Faden-Provenance</c> (<see cref="HttpHeaderMarker"/>).</summary>
    public static FoundMark? ReadHeader(string? value)
    {
        var id = Http.ProvenanceHeader.Parse(value).FirstOrDefault();
        return ProvenanceJson.IsRecordId(id) ? new FoundMark(id!, HttpHeaderMarker.MarkerName) : null;
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
