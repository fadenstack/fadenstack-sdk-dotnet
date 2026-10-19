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
/// <item>Word: a hidden bookmark around the inserted range (<see cref="AnchorName"/>; a later write inside it splits it
/// into parts, <see cref="SplitAnchors"/>, read back with <see cref="RecordSpans"/>), the signed records in a custom XML
/// part (<see cref="CustomXmlPart"/>, namespace <see cref="Namespace"/>), and the document property
/// <see cref="DocumentProperty"/> as the flag (<see cref="DocumentPropertyValue"/>, at most 255 characters).</item>
/// <item>PowerPoint: the shape or slide tag <see cref="ShapeTag"/> with the newest record's id, <see cref="ShapeRecordsTag"/>
/// with every record whose words are in it (<see cref="ShapeRecords"/>), and the custom XML part.</item>
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

    /// <summary>The longest anchor: a record's anchor (34 characters) and a part's <c>_99</c>. Word allows 40.</summary>
    public const int AnchorMaxLength = 37;

    /// <summary>The last part of an anchor: a record's anchor is its first part, <c>&lt;anchor&gt;_2</c> to <c>&lt;anchor&gt;_99</c> the others.</summary>
    public const int AnchorMaxPart = 99;

    /// <summary>A record's anchor without a part: the prefix and 24 hex digits.</summary>
    private const int AnchorLength = 34;

    /// <summary>
    /// The host value (<see cref="MarkableContent.HostValues"/>) a Word or Excel marker's <c>PrepareAsync</c> sets to the
    /// anchor's name (<see cref="AnchorName"/>), so the insert makes the bookmark or name together with the content, in one
    /// undo entry. The TypeScript SDK's <c>OfficeMarks.anchorValue</c> is the same.
    /// </summary>
    public const string AnchorValue = "office.anchor";

    /// <summary>The PowerPoint shape and slide tag that holds the id of the newest record that wrote into it.</summary>
    public const string ShapeTag = "FADEN_AI_RECORD";

    /// <summary>
    /// The PowerPoint shape and slide tag that lists every record whose words are in it, oldest first, space-separated
    /// (<see cref="ShapeRecords"/>, <see cref="ShapeRecordsAfterWrite"/>).
    /// </summary>
    public const string ShapeRecordsTag = "FADEN_AI_RECORDS";

    /// <summary>
    /// The internet header an Outlook draft carries the record's id in, spelled as every marker writes it (and as
    /// <see cref="HttpHeaderMarker.Header"/>): <c>X-AI-Generated: &lt;record id&gt;</c>.
    /// </summary>
    public const string MailHeader = "X-AI-Generated";

    /// <summary>The MAPI named property (PS_PUBLIC_STRINGS) that holds the signed record of a draft.</summary>
    public const string MapiRecordProperty = "http://schemas.microsoft.com/mapi/string/{00020329-0000-0000-C000-000000000046}/FadenProvenanceRecord";

    /// <summary>
    /// The MAPI property (PS_INTERNET_HEADERS) that sets <see cref="MailHeader"/> on a draft. Header names do not depend
    /// on case; this property name keeps its lower-case spelling.
    /// </summary>
    public const string MapiHeaderProperty = "http://schemas.microsoft.com/mapi/string/{00020386-0000-0000-C000-000000000046}/x-ai-generated";

    /// <summary>
    /// The bookmark or name of a record: <c>_faden_ai_</c> and the first 24 hex digits of the record's id, dashes removed
    /// (34 characters; Word allows 40, and letters, digits and underscores only). The TypeScript SDK's
    /// <c>officeAnchorName</c> is the same.
    /// </summary>
    public static string AnchorName(string recordId)
    {
        if (string.IsNullOrWhiteSpace(recordId))
        {
            throw new ArgumentException("A record id is needed.", nameof(recordId));
        }
        var hex = new string(recordId.Where(Uri.IsHexDigit).Take(24).ToArray()).ToLowerInvariant();
        return hex.Length < 24 ? throw new ArgumentException("A record id (a UUID) is needed for an anchor.", nameof(recordId)) : AnchorPrefix + hex;
    }

    /// <summary>
    /// Whether a bookmark or name is one of our anchors: a record's anchor (<see cref="AnchorName"/>), or a later part of
    /// it, <c>&lt;anchor&gt;_&lt;n&gt;</c> with n from 2 to 99, where a later write inside the record's text split its
    /// bookmark (<see cref="SplitAnchors"/>). The TypeScript SDK's <c>isOfficeAnchor</c> is the same.
    /// </summary>
    public static bool IsAnchor(string? name) => AnchorBase(name) is not null;

    /// <summary>
    /// The record's anchor a bookmark or name is part of: the name itself for a record's anchor, the name without its
    /// <c>_&lt;n&gt;</c> for a later part (n from 2 to 99, no leading zeros); null for a name that is no anchor of ours.
    /// Every part maps to the same record, as written. The TypeScript SDK's <c>officeAnchorBase</c> is the same.
    /// </summary>
    public static string? AnchorBase(string? name)
    {
        if (name is null || name.Length < AnchorLength || !name.StartsWith(AnchorPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        for (var i = AnchorPrefix.Length; i < AnchorLength; i++)
        {
            if (!Uri.IsHexDigit(name[i]))
            {
                return null;
            }
        }
        var part = name.Length - AnchorLength;
        var numbered = part switch
        {
            0 => true,
            2 => name[AnchorLength] == '_' && name[AnchorLength + 1] is >= '2' and <= '9',
            3 => name[AnchorLength] == '_' && name[AnchorLength + 1] is >= '1' and <= '9' && name[AnchorLength + 2] is >= '0' and <= '9',
            _ => false,
        };
        return numbered ? name.Substring(0, AnchorLength) : null;
    }

    /// <summary>
    /// The name of part <paramref name="part"/> (2 to 99) of an anchor, given the anchor or any part of it:
    /// <c>&lt;anchor&gt;_&lt;part&gt;</c>. The TypeScript SDK's <c>officeAnchorPart</c> is the same.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="anchor"/> is no anchor of ours.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="part"/> is not from 2 to 99.</exception>
    public static string AnchorPart(string anchor, int part)
    {
        var anchorBase = AnchorBase(anchor) ?? throw new ArgumentException("A part's name needs an anchor of ours.", nameof(anchor));
        return part is < 2 or > AnchorMaxPart
            ? throw new ArgumentOutOfRangeException(nameof(part), part, $"An anchor's parts are numbered from 2 to {AnchorMaxPart}.")
            : anchorBase + "_" + part.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The record of a custom XML part's that an anchor or any part of it marks (its <see cref="OfficeRecord.Anchor"/>,
    /// or the anchor of its id); null when none does. Anchors compare without regard to case, as Word's bookmark names
    /// do. The TypeScript SDK's <c>officeRecordOfAnchor</c> is the same.
    /// </summary>
    public static OfficeRecord? RecordOfAnchor(IEnumerable<OfficeRecord> records, string? name)
    {
        if (AnchorBase(name) is not { } anchor)
        {
            return null;
        }
        return (records ?? throw new ArgumentNullException(nameof(records))).FirstOrDefault(r =>
            string.Equals(AnchorBase(r.Anchor), anchor, StringComparison.OrdinalIgnoreCase)
            || (ProvenanceJson.IsRecordId(r.RecordId) && string.Equals(AnchorName(r.RecordId), anchor, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Marks never nest, as in HTML: after a write, the anchors of earlier records hold only their own text. Word grows
    /// a bookmark around text written inside it or at its start; given the anchors of the story as they are after the
    /// write and the spans it wrote (its text, a paragraph mark it added, each replacement), this says how they must be.
    /// Each earlier anchor that holds written text loses it: the part before keeps the name, the part after becomes a
    /// new part <c>&lt;anchor&gt;_&lt;n&gt;</c> (the lowest n from 2 to 99 that no bookmark has), and a part with
    /// nothing in it goes -- so an anchor that starts where the write starts moves to just after it, keeping its name,
    /// and one the write covers is removed. Bookmarks that are not ours are never touched. The TypeScript SDK's
    /// <c>splitOfficeAnchors</c> is the same (<c>office-anchor-vectors.json</c> is the test).
    /// </summary>
    /// <param name="anchors">The bookmarks of the story the write went into, as they are now.</param>
    /// <param name="written">What the write wrote.</param>
    /// <param name="own">The write's own anchor, which stays as it is.</param>
    /// <param name="taken">The document's other bookmarks, whose names a new part does not take. Without a free number
    /// the text after the write stays without an anchor.</param>
    public static OfficeAnchorSplit SplitAnchors(IEnumerable<OfficeAnchorSpan> anchors, IEnumerable<OfficeSpan> written, string? own = null,
        IEnumerable<string>? taken = null)
    {
        var all = (anchors ?? throw new ArgumentNullException(nameof(anchors))).ToList();
        var cuts = Joined(written ?? throw new ArgumentNullException(nameof(written)));
        var ownBase = own is null ? null : AnchorBase(own) ?? own;
        var used = new HashSet<string>(all.Select(a => a.Name).Concat(taken ?? Enumerable.Empty<string>()), StringComparer.OrdinalIgnoreCase);
        var set = new List<OfficeAnchorSpan>();
        var removed = new List<string>();
        foreach (var anchor in all.OrderBy(a => a.Start).ThenBy(a => a.End))
        {
            var anchorBase = AnchorBase(anchor.Name);
            if (anchorBase is null || string.Equals(anchorBase, ownBase, StringComparison.OrdinalIgnoreCase)
                || !cuts.Any(c => Math.Max(anchor.Start, c.Start) < Math.Min(anchor.End, c.End)))
            {
                continue;
            }
            var pieces = Outside(new OfficeSpan(anchor.Start, anchor.End), cuts);
            if (pieces.Count == 0)
            {
                removed.Add(anchor.Name);
                continue;
            }
            set.Add(new OfficeAnchorSpan(anchor.Name, pieces[0].Start, pieces[0].End));
            foreach (var piece in pieces.Skip(1))
            {
                if (FreePart(anchorBase, used) is { } name)
                {
                    set.Add(new OfficeAnchorSpan(name, piece.Start, piece.End));
                }
            }
        }
        return new OfficeAnchorSplit(set, removed);
    }

    private static string? FreePart(string anchorBase, HashSet<string> used)
    {
        for (var n = 2; n <= AnchorMaxPart; n++)
        {
            var name = anchorBase + "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (used.Add(name))
            {
                return name;
            }
        }
        return null;
    }

    /// <summary>
    /// Where a record's text is in a story: the spans of its anchor and of every part of it, in document order, without
    /// what anchors of other records inside them hold (one around the very same text is not inside). The record's text
    /// is the text of these spans joined with nothing between: as it was placed, also after later writes split its
    /// anchor. The TypeScript SDK's <c>officeRecordSpans</c> is the same.
    /// </summary>
    /// <param name="anchors">The bookmarks of a story.</param>
    /// <param name="record">The record's id, or its anchor (or a part's name).</param>
    /// <returns>The spans; none when the record has no anchor in <paramref name="anchors"/>.</returns>
    public static IReadOnlyList<OfficeSpan> RecordSpans(IEnumerable<OfficeAnchorSpan> anchors, string record)
    {
        var all = (anchors ?? throw new ArgumentNullException(nameof(anchors))).ToList();
        var wanted = AnchorBase(record) ?? (ProvenanceJson.IsRecordId(record) ? AnchorName(record) : null);
        if (wanted is null)
        {
            return Array.Empty<OfficeSpan>();
        }
        bool Ours(OfficeAnchorSpan a) => string.Equals(AnchorBase(a.Name), wanted, StringComparison.OrdinalIgnoreCase);
        var others = all.Where(a => !Ours(a) && IsAnchor(a.Name) && a.End > a.Start).ToList();
        var spans = new List<OfficeSpan>();
        foreach (var part in all.Where(a => Ours(a) && a.End > a.Start))
        {
            var inside = others.Where(o => o.Start >= part.Start && o.End <= part.End && !(o.Start == part.Start && o.End == part.End))
                .Select(o => new OfficeSpan(o.Start, o.End));
            spans.AddRange(Outside(new OfficeSpan(part.Start, part.End), Joined(inside)));
        }
        return Joined(spans);
    }

    /// <summary>The spans sorted, those with nothing in them left out, overlapping and touching ones joined.</summary>
    private static List<OfficeSpan> Joined(IEnumerable<OfficeSpan> spans)
    {
        var joined = new List<OfficeSpan>();
        foreach (var span in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start).ThenBy(s => s.End))
        {
            if (joined.Count > 0 && span.Start <= joined[joined.Count - 1].End)
            {
                var last = joined[joined.Count - 1];
                joined[joined.Count - 1] = new OfficeSpan(last.Start, Math.Max(last.End, span.End));
            }
            else
            {
                joined.Add(span);
            }
        }
        return joined;
    }

    /// <summary>What of <paramref name="span"/> is outside <paramref name="cuts"/> (sorted and joined), in order.</summary>
    private static List<OfficeSpan> Outside(OfficeSpan span, IReadOnlyList<OfficeSpan> cuts)
    {
        var pieces = new List<OfficeSpan>();
        var from = span.Start;
        foreach (var cut in cuts)
        {
            if (cut.End <= from)
            {
                continue;
            }
            if (cut.Start >= span.End)
            {
                break;
            }
            if (cut.Start > from)
            {
                pieces.Add(new OfficeSpan(from, cut.Start));
            }
            from = cut.End;
        }
        if (from < span.End)
        {
            pieces.Add(new OfficeSpan(from, span.End));
        }
        return pieces;
    }

    /// <summary>
    /// The records whose words a PowerPoint shape (or slide) holds, oldest first: the ids its <see cref="ShapeRecordsTag"/>
    /// lists (<paramref name="records"/>), then the one its <see cref="ShapeTag"/> holds (<paramref name="record"/>) when
    /// the list misses it -- a shape tagged before the list, or by an add-in without it. Only record ids count, each
    /// once. A shape has no bookmarks, so a write into part of its text leaves it holding the words of more than one
    /// record. The TypeScript SDK's <c>officeShapeRecords</c> is the same (<c>office-shape-vectors.json</c>).
    /// </summary>
    public static IReadOnlyList<string> ShapeRecords(string? records, string? record)
    {
        var ids = new List<string>();
        void Add(string id)
        {
            if (ProvenanceJson.IsRecordId(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(id);
            }
        }
        foreach (var id in (records ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            Add(id);
        }
        Add((record ?? "").Trim());
        return ids;
    }

    /// <summary>
    /// The <see cref="ShapeRecordsTag"/> of a shape (or slide) after record <paramref name="recordId"/> wrote into it,
    /// whose <see cref="ShapeTag"/> becomes <paramref name="recordId"/> either way. A write into part of its text
    /// (<paramref name="partial"/>: the rest of its words stay) puts the record after those already there
    /// (<see cref="ShapeRecords"/>, so the list starts from <see cref="ShapeTag"/> when there is none); a write of its
    /// whole text, or a new shape, leaves the record alone. The TypeScript SDK's <c>officeShapeRecordsAfterWrite</c> is
    /// the same.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="recordId"/> is no record id.</exception>
    public static string ShapeRecordsAfterWrite(string? records, string? record, string recordId, bool partial)
    {
        if (!ProvenanceJson.IsRecordId(recordId))
        {
            throw new ArgumentException("A record id (a UUID) is needed for a shape's tags.", nameof(recordId));
        }
        return partial
            ? string.Join(" ", ShapeRecords(records, record).Where(id => !string.Equals(id, recordId, StringComparison.OrdinalIgnoreCase)).Append(recordId))
            : recordId;
    }

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
    /// The custom XML part: <c>&lt;provenance xmlns="https://fadenstack.com/ns/provenance/1.0"&gt;</c> with one
    /// <c>&lt;record id="..." anchor="..." timestamp="..."&gt;JWS&lt;/record&gt;</c> per placement (its anchor: the
    /// bookmark, name or shape it marks; <c>timestamp</c> only where there is one), no XML declaration, no white space
    /// between elements. The TypeScript SDK's <c>officeCustomXmlPart</c> writes the same. The server's detector finds
    /// the record ids in any <c>customXml/</c> part with the namespace and in <c>docProps/custom.xml</c>.
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

/// <summary>A stretch of a document's story by character positions, <see cref="Start"/> in it and <see cref="End"/> not: Word's <c>Range.Start</c> and <c>End</c>.</summary>
public sealed record OfficeSpan(int Start, int End);

/// <summary>A bookmark and where it is in its story.</summary>
public sealed record OfficeAnchorSpan(string Name, int Start, int End);

/// <summary>
/// What a later write does to the anchors it landed in (<see cref="OfficeMarks.SplitAnchors"/>): the anchors to put --
/// an existing name moves, a new one is added -- and the names to remove.
/// </summary>
public sealed record OfficeAnchorSplit(IReadOnlyList<OfficeAnchorSpan> Set, IReadOnlyList<string> Removed);
