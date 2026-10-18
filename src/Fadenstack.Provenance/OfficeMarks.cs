using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Fadenstack.Models;

namespace Fadenstack.Provenance;

/// <summary>
/// The names and formats of the marks in Office documents and mails, for host markers written with the Office object
/// model (Word, PowerPoint, Excel, Outlook) and for anyone who reads them, so that every marker and the server's
/// detector agree:
/// <list type="bullet">
/// <item>Word: a hidden bookmark around the inserted range (<see cref="AnchorName"/>), the signed records in a custom XML
/// part (<see cref="CustomXmlPart"/>, namespace <see cref="Namespace"/>), and the document property
/// <see cref="DocumentProperty"/> as the flag (<see cref="DocumentPropertyValue"/>, at most 255 characters).</item>
/// <item>PowerPoint: the shape or slide tag <see cref="ShapeTag"/> with the record's id, and the custom XML part.</item>
/// <item>Excel: a hidden name per written range (<see cref="AnchorName"/>), and the custom XML part.</item>
/// <item>Outlook: the named property <see cref="MapiRecordProperty"/> with the signed record, and the internet header
/// <see cref="MailHeader"/> with the record's id (<see cref="MapiHeaderProperty"/>).</item>
/// </list>
/// A host marker is an ordinary <see cref="IContentMarker"/>: its <see cref="IContentMarker.AttachAsync"/> gets the range
/// or shape from <see cref="PlacedContent.Location"/>, writes these marks and reads them back.
/// </summary>
public static class OfficeMarks
{
    /// <summary>The namespace of the custom XML part that holds the signed records.</summary>
    public const string Namespace = "https://fadenstack.com/ns/provenance/1.0";

    /// <summary>The custom document property that flags a document as holding AI-generated content.</summary>
    public const string DocumentProperty = "AI-Generated-Content";

    /// <summary>Desktop Word cuts custom document properties at 255 characters.</summary>
    public const int DocumentPropertyMaxLength = 255;

    /// <summary>A hidden bookmark (Word) or hidden name (Excel) starts with this; an underscore hides it.</summary>
    public const string AnchorPrefix = "_faden_ai_";

    /// <summary>The PowerPoint shape and slide tag that holds the record's id.</summary>
    public const string ShapeTag = "FADEN_AI_RECORD";

    /// <summary>The internet header an Outlook draft carries the record's id in.</summary>
    public const string MailHeader = "x-ai-generated";

    /// <summary>The MAPI named property (PS_PUBLIC_STRINGS) that holds the signed record of a draft.</summary>
    public const string MapiRecordProperty = "http://schemas.microsoft.com/mapi/string/{00020329-0000-0000-C000-000000000046}/FadenProvenanceRecord";

    /// <summary>The MAPI property (PS_INTERNET_HEADERS) that sets <see cref="MailHeader"/> on a draft.</summary>
    public const string MapiHeaderProperty = "http://schemas.microsoft.com/mapi/string/{00020386-0000-0000-C000-000000000046}/" + MailHeader;

    /// <summary>
    /// The bookmark or name of a record: <c>_faden_ai_</c> and 24 hex digits of the record's id (34 characters; Word
    /// allows 40, and letters, digits and underscores only).
    /// </summary>
    public static string AnchorName(string recordId)
    {
        if (string.IsNullOrWhiteSpace(recordId))
        {
            throw new ArgumentException("A record id is needed.", nameof(recordId));
        }
        var hex = new string(recordId.Where(Uri.IsHexDigit).Take(24).ToArray()).ToLowerInvariant();
        if (hex.Length < 24)
        {
            using var sha = SHA256.Create();
            hex = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(recordId)).Take(12).Select(b => b.ToString("x2")));
        }
        return AnchorPrefix + hex;
    }

    /// <summary>Whether a bookmark or name is one of our anchors.</summary>
    public static bool IsAnchor(string? name) =>
        name is not null && name.Length == AnchorPrefix.Length + 24 && name.StartsWith(AnchorPrefix, StringComparison.Ordinal)
        && name.Substring(AnchorPrefix.Length).All(Uri.IsHexDigit);

    /// <summary>
    /// The document property's value: <c>true</c> and the records' ids, newest first, as many as fit in 255 characters
    /// (the custom XML part holds them all).
    /// </summary>
    public static string DocumentPropertyValue(IEnumerable<string> recordIds)
    {
        var value = new StringBuilder("true");
        foreach (var id in (recordIds ?? throw new ArgumentNullException(nameof(recordIds))).Reverse().Distinct())
        {
            if (value.Length + 1 + id.Length > DocumentPropertyMaxLength)
            {
                break;
            }
            value.Append(' ').Append(id);
        }
        return value.ToString();
    }

    /// <summary>
    /// The custom XML part: one <c>record</c> per placement, with the record's id, its anchor (the bookmark, name or shape
    /// it marks) and the signed record (the JWS) as its text; the time-stamp token where there is one.
    /// </summary>
    public static string CustomXmlPart(IEnumerable<OfficeRecord> records)
    {
        XNamespace ns = Namespace;
        var root = new XElement(ns + "provenance",
            (records ?? throw new ArgumentNullException(nameof(records))).Select(r => new XElement(ns + "record",
                new XAttribute("id", r.RecordId),
                new XAttribute("anchor", r.Anchor),
                r.Timestamp is null ? null : new XAttribute("timestamp", r.Timestamp),
                r.Signature)));
        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The records in a custom XML part; none when it is not ours or not well formed. Data, never instructions.</summary>
    public static IReadOnlyList<OfficeRecord> ReadCustomXmlPart(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return Array.Empty<OfficeRecord>();
        }
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10_000_000,
            });
            var root = XElement.Load(reader);
            XNamespace ns = Namespace;
            if (root.Name != ns + "provenance")
            {
                return Array.Empty<OfficeRecord>();
            }
            return root.Elements(ns + "record")
                .Select(e => new OfficeRecord((string?)e.Attribute("id") ?? "", (string?)e.Attribute("anchor") ?? "", e.Value.Trim(),
                    (string?)e.Attribute("timestamp")))
                .Where(r => ProvenanceJson.IsRecordId(r.RecordId))
                .ToList();
        }
        catch (XmlException)
        {
            return Array.Empty<OfficeRecord>();
        }
    }

    /// <summary>The part's entry for a placement: the signed record and the anchor it marks.</summary>
    public static OfficeRecord ToOfficeRecord(SignedRecord record, string anchor) =>
        new(record.Record.Id, anchor, record.Signature, record.Timestamp);
}

/// <summary>One record in a document's custom XML part: its id, the anchor it marks, its signature (a JWS), its time-stamp.</summary>
public sealed record OfficeRecord(string RecordId, string Anchor, string Signature, string? Timestamp = null);
