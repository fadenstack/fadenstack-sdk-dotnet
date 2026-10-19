using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Fadenstack.Provenance;

/// <summary>
/// Marks never nest. What an agent writes later into HTML that holds an earlier mark goes beside that mark, not into
/// it: the earlier element is split at the place into two siblings that both carry its <c>data-ai-generated</c> and
/// title, and the new write goes between them as its own marked element. Every element then holds the text of exactly
/// one record, and a record's text is read from all the elements that carry its id (<see cref="RecordText"/>).
/// <para>
/// An inline mark (a <c>span</c>) is split where the write goes. A block mark (a <c>div</c> around several blocks) is
/// not split inside a line, which would break the line in two: it first takes the form of a one-paragraph mark -- a
/// <c>span</c> with its attribute and title around the text of each line, the <c>div</c> left without them -- and the
/// span at the place is split. A half with nothing in it goes, so a write at the very start or end of a mark goes
/// beside it. The TypeScript SDK's <c>splitMarksInHtml</c> and <c>recordText</c> do the same
/// (<c>mark-split-vectors.json</c> is the test); its <c>splitMarksAt</c> does it to a page's DOM.
/// </para>
/// </summary>
internal static class HtmlMarks
{
    private const string LabelAttribute = VisibleLabelMarker.Attribute;

    /// <summary>Elements people see that hold no text: a half or a line with one of them is not empty.</summary>
    private static readonly HashSet<string> Replaced = new(StringComparer.Ordinal)
    {
        "audio", "button", "canvas", "embed", "iframe", "img", "input", "math", "meter", "object", "picture", "progress", "select", "svg",
        "textarea", "video",
    };

    private static readonly Regex AttributePattern = new(@"([^\s=/>]+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'=<>`]+)))?",
        RegexOptions.CultureInvariant);

    /// <summary>A character reference cut at the place: <c>&amp;am|p;</c>.</summary>
    private static readonly Regex InReference = new("&(?:#[0-9]*|#[xX][0-9a-fA-F]*|[A-Za-z][A-Za-z0-9]*)$", RegexOptions.CultureInvariant);

    private static readonly Regex ReferenceRest = new("^[0-9A-Za-z]*;", RegexOptions.CultureInvariant);

    /// <summary>An attribute of a start tag: its name in lower case, its value decoded and trimmed, and the attribute as written.</summary>
    private sealed record TagAttribute(string Name, string Value, string Written);

    private static List<TagAttribute> AttributesOf(string text) =>
        AttributePattern.Matches(text).Cast<Match>().Select(m => new TagAttribute(m.Groups[1].Value.ToLowerInvariant(),
            WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value).Trim(),
            m.Value)).ToList();

    /// <summary>
    /// The mark an element's attributes carry: the record its <c>data-ai-generated</c> names, else the one a title of
    /// ours names; a visible label (<c>data-ai-label</c>) is a mark of no record's text; anything else no mark.
    /// </summary>
    private static (bool Marked, string? Record) MarkOf(IEnumerable<TagAttribute> attributes)
    {
        string? title = null;
        var label = false;
        foreach (var a in attributes)
        {
            if (a.Name == HtmlAttributeMarker.Attribute && ProvenanceJson.IsRecordId(a.Value))
            {
                return (true, a.Value);
            }
            if (a.Name == LabelAttribute && ProvenanceJson.IsRecordId(a.Value))
            {
                label = true;
            }
            else if (a.Name == "title")
            {
                title ??= ProvenanceReader.RecordIdInTitle(a.Value);
            }
        }
        return label ? (true, null) : (title is not null, title);
    }

    // -- reading ----------------------------------------------------------------------------------

    /// <summary>
    /// The text a reader sees of one record in HTML: the text of every element that carries its id
    /// (<c>data-ai-generated</c>, or a title of ours), in document order, without what elements of other records and
    /// visible labels inside them hold. A mark split in two by a later write reads as its record's text again. Block
    /// elements are line breaks wherever they are, as in <see cref="ReaderText.FromHtml"/>: white space, which hashing
    /// makes one space.
    /// </summary>
    public static string RecordText(string html, string recordId) =>
        ReaderText.TextOfHtml(html.Length > 0 && html[0] == (char)0xFEFF ? html.Substring(1) : html, new RecordHook(recordId));

    /// <summary>Follows the open elements and the record whose text each holds: its own mark's, none for a label, else its parent's.</summary>
    private sealed class RecordHook(string recordId) : ReaderText.ITagHook
    {
        private readonly List<(string Name, string? Record)> _open = new();

        public bool Keeping => _open.Count > 0 && _open[_open.Count - 1].Record == recordId;

        public void Tag(string name, bool closing, string attributes, bool selfClosing)
        {
            if (closing)
            {
                var at = _open.FindLastIndex(e => e.Name == name);
                if (at >= 0)
                {
                    _open.RemoveRange(at, _open.Count - at);
                }
            }
            else if (!selfClosing && !ReaderText.Void.Contains(name))
            {
                var (marked, record) = MarkOf(AttributesOf(attributes));
                _open.Add((name, marked ? record : _open.Count > 0 ? _open[_open.Count - 1].Record : null));
            }
        }
    }

    // -- the tree of an HTML text -----------------------------------------------------------------

    private enum Kind
    {
        Element,
        Text,
        Comment,
    }

    private sealed class Node
    {
        public Kind Kind { get; init; }

        /// <summary>An element's name in lower case.</summary>
        public string Name { get; init; } = "";

        /// <summary>An element's start tag as written (or written again without some attributes); a text's or a comment's characters.</summary>
        public string Start { get; set; } = "";

        /// <summary>The end tag as written; "" for an element without one (void, self-closing, never closed, dropped).</summary>
        public string End { get; set; } = "";

        public List<TagAttribute> Attributes { get; set; } = new();

        public List<Node> Children { get; } = new();

        public Node? Parent { get; set; }
    }

    private static bool IsRecordMark(Node node) => node.Kind == Kind.Element && MarkOf(node.Attributes) is { Marked: true, Record: not null };

    private static void InsertBefore(Node parent, Node node, Node? before)
    {
        node.Parent?.Children.Remove(node);
        if (before is null)
        {
            parent.Children.Add(node);
        }
        else
        {
            parent.Children.Insert(parent.Children.IndexOf(before), node);
        }
        node.Parent = parent;
    }

    private static void Remove(Node node)
    {
        node.Parent?.Children.Remove(node);
        node.Parent = null;
    }

    private static Node? NextSibling(Node node)
    {
        var siblings = node.Parent!.Children;
        var at = siblings.IndexOf(node) + 1;
        return at < siblings.Count ? siblings[at] : null;
    }

    /// <summary>A start tag written again with only <paramref name="attributes"/>.</summary>
    private static string StartTag(Node element, IEnumerable<TagAttribute> attributes)
    {
        var name = Regex.Match(element.Start, @"^<([^\s/>]+)").Groups[1].Value;
        return "<" + (name.Length > 0 ? name : element.Name) + string.Concat(attributes.Select(a => " " + a.Written)) + ">";
    }

    /// <summary>A copy of an element without its children, and without its <c>id</c> (an id names one element).</summary>
    private static Node Clone(Node element)
    {
        var attributes = element.Attributes.Where(a => a.Name != "id").ToList();
        return new Node
        {
            Kind = Kind.Element, Name = element.Name, End = element.End, Attributes = attributes,
            Start = attributes.Count == element.Attributes.Count ? element.Start : StartTag(element, attributes),
        };
    }

    private static List<TagAttribute> MarkAttributes(Node element) =>
        element.Attributes.Where(a => a.Name == HtmlAttributeMarker.Attribute || (a.Name == "title" && ProvenanceReader.RecordIdInTitle(a.Value) is not null)).ToList();

    /// <summary>A <c>span</c> with the mark of <paramref name="marked"/> (its <c>data-ai-generated</c>, and its title when it is ours).</summary>
    private static Node MarkedSpan(Node marked)
    {
        var attributes = MarkAttributes(marked);
        return new Node
        {
            Kind = Kind.Element, Name = "span", Start = "<span" + string.Concat(attributes.Select(a => " " + a.Written)) + ">", End = "</span>",
            Attributes = attributes,
        };
    }

    private static void Unmark(Node element)
    {
        var marks = MarkAttributes(element);
        element.Attributes = element.Attributes.Where(a => !marks.Contains(a)).ToList();
        element.Start = StartTag(element, element.Attributes);
    }

    // -- the rule ---------------------------------------------------------------------------------

    /// <summary>Whether a half of a split keeps anything: text (white space too), an element people see, a comment.</summary>
    private static bool HasContent(Node node) => node.Kind switch
    {
        Kind.Text => node.Start.Length > 0,
        Kind.Comment => true,
        _ => ReaderText.Void.Contains(node.Name) || Replaced.Contains(node.Name) || node.Children.Any(HasContent),
    };

    /// <summary>Whether a run of a line is worth a mark: text other than white space, or an element people see.</summary>
    private static bool Visible(Node node) => node.Kind switch
    {
        Kind.Text => node.Start.Any(c => c is not (' ' or '\t' or '\n' or '\f' or '\r')),
        Kind.Comment => false,
        _ => !ReaderText.Dropped.Contains(node.Name) && (Replaced.Contains(node.Name) || node.Children.Any(Visible)),
    };

    private static bool HoldsBlock(Node node) =>
        node.Children.Any(c => c.Kind == Kind.Element && (ReaderText.IsBlockElement(c.Name) || HoldsBlock(c)));

    /// <summary>Whether a node is part of a line's run: text, a comment, an inline element with no block in it that is no mark.</summary>
    private static bool InRun(Node node) =>
        node.Kind != Kind.Element
        || (!ReaderText.IsBlockElement(node.Name) && !ReaderText.Dropped.Contains(node.Name) && !MarkOf(node.Attributes).Marked && !HoldsBlock(node));

    /// <summary>A mark's text in the form of a one-paragraph mark: a span with its mark around each run of a line in it, and the mark taken off it.</summary>
    private static void Demote(Node marked)
    {
        void Wrap(Node element)
        {
            var run = new List<Node>();
            void Flush()
            {
                if (run.Any(Visible))
                {
                    var span = MarkedSpan(marked);
                    InsertBefore(element, span, run[0]);
                    foreach (var node in run)
                    {
                        InsertBefore(span, node, null);
                    }
                }
                run.Clear();
            }
            foreach (var child in element.Children.ToList())
            {
                if (InRun(child))
                {
                    run.Add(child);
                    continue;
                }
                Flush();
                // Another record's mark and a label keep what they hold; dropped and void elements hold no line.
                if (child.Kind == Kind.Element && !ReaderText.Dropped.Contains(child.Name) && !ReaderText.Void.Contains(child.Name)
                    && !MarkOf(child.Attributes).Marked)
                {
                    Wrap(child);
                }
            }
            Flush();
        }
        Wrap(marked);
        Unmark(marked);
    }

    /// <summary>Splits the inline mark <paramref name="marked"/> at <paramref name="marker"/>: what follows it goes into a copy after it, the marker between the two.</summary>
    private static void SplitAt(Node marker, Node marked)
    {
        var path = new List<Node>();
        var copies = new List<Node>();
        var child = marker;
        Node? right = null;
        while (true)
        {
            var parent = child.Parent!;
            var copy = Clone(parent);
            if (right is not null)
            {
                InsertBefore(copy, right, null);
            }
            foreach (var sibling in parent.Children.Skip(parent.Children.IndexOf(child) + 1).ToList())
            {
                InsertBefore(copy, sibling, null);
            }
            path.Add(parent);
            copies.Add(copy);
            right = copy;
            if (parent == marked)
            {
                break;
            }
            child = parent;
        }
        var after = NextSibling(marked);
        InsertBefore(marked.Parent!, marker, after);
        InsertBefore(marked.Parent!, right, after);
        // The elements the place was in, on either side, inside out: one with nothing left goes.
        foreach (var node in path.Concat(copies).Where(n => !HasContent(n)).ToList())
        {
            Remove(node);
        }
    }

    /// <summary>The innermost mark of a record around <paramref name="node"/>, below <paramref name="root"/>.</summary>
    private static Node? MarkAround(Node node, Node root)
    {
        for (var at = node.Parent; at is not null && at != root; at = at.Parent)
        {
            if (IsRecordMark(at))
            {
                return at;
            }
        }
        return null;
    }

    /// <summary>Splits every mark around <paramref name="marker"/> below <paramref name="root"/>, so that the marker is in none.</summary>
    private static void SplitMarks(Node marker, Node root)
    {
        for (var marked = MarkAround(marker, root); marked is not null; marked = MarkAround(marker, root))
        {
            var blockBetween = false;
            for (var at = marker.Parent!; at != marked; at = at.Parent!)
            {
                blockBetween |= ReaderText.IsBlockElement(at.Name);
            }
            if (blockBetween || ReaderText.IsBlockElement(marked.Name))
            {
                Demote(marked);
            }
            else
            {
                SplitAt(marker, marked);
            }
        }
    }

    // -- HTML text --------------------------------------------------------------------------------

    /// <summary>HTML as a tree, with an empty text at <paramref name="at"/> (the place).</summary>
    private static (Node Root, Node Place) Parse(string html, int at)
    {
        if (at < 0 || at > html.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(at), at, "no place in the HTML");
        }
        var root = new Node { Kind = Kind.Element, Name = "#root" };
        var current = root;
        Node? place = null;
        void Add(Node node) => InsertBefore(current, node, null);
        void Text(int from, int to)
        {
            if (from < to)
            {
                Add(new Node { Kind = Kind.Text, Start = html.Substring(from, to - from) });
            }
        }
        void Mark() => Add(place = new Node { Kind = Kind.Text });
        ReaderText.Scan(html, piece =>
        {
            if (place is null && at == piece.Start)
            {
                Mark();
            }
            var inside = place is null && at > piece.Start && at < piece.End;
            if (piece.Kind == ReaderText.HtmlPieceKind.Text)
            {
                if (!inside)
                {
                    Text(piece.Start, piece.End);
                    return;
                }
                if (InReference.IsMatch(html.Substring(piece.Start, at - piece.Start)) && ReferenceRest.IsMatch(html.Substring(at, piece.End - at)))
                {
                    throw new ArgumentOutOfRangeException(nameof(at), at, "inside a character reference");
                }
                Text(piece.Start, at);
                Mark();
                Text(at, piece.End);
                return;
            }
            if (inside)
            {
                throw new ArgumentOutOfRangeException(nameof(at), at, "inside a tag or a comment");
            }
            var written = html.Substring(piece.Start, piece.End - piece.Start);
            switch (piece.Kind)
            {
                case ReaderText.HtmlPieceKind.Comment:
                    Add(new Node { Kind = Kind.Comment, Start = written });
                    return;
                case ReaderText.HtmlPieceKind.Dropped:
                    Add(new Node { Kind = Kind.Element, Name = piece.Name, Start = written });
                    return;
            }
            if (piece.Closing)
            {
                // The open element it ends, and every one opened inside it; an end tag that ends none stays as it is.
                for (var e = current; e != root; e = e.Parent!)
                {
                    if (e.Name == piece.Name)
                    {
                        e.End = written;
                        current = e.Parent!;
                        return;
                    }
                }
                Add(new Node { Kind = Kind.Comment, Start = written });
                return;
            }
            var element = new Node { Kind = Kind.Element, Name = piece.Name, Start = written, Attributes = AttributesOf(piece.Attributes) };
            Add(element);
            if (!piece.SelfClosing && !ReaderText.Void.Contains(piece.Name))
            {
                current = element;
            }
        });
        if (place is null)
        {
            Mark();
        }
        return (root, place!);
    }

    private static void Serialize(Node node, Node place, StringBuilder output, ref int at)
    {
        if (node == place)
        {
            at = output.Length;
        }
        output.Append(node.Start);
        foreach (var child in node.Children)
        {
            Serialize(child, place, output, ref at);
        }
        output.Append(node.End);
    }

    /// <summary>
    /// HTML with the marks around <paramref name="index"/> split, so that what a host inserts at the returned index is
    /// in no mark; everything else stays as written. <paramref name="index"/> is in the HTML's text or between its
    /// tags.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the HTML, or inside a tag, a comment or a character reference.</exception>
    public static (string Html, int Index) SplitMarksAt(string html, int index)
    {
        var (root, place) = Parse(html ?? throw new ArgumentNullException(nameof(html)), index);
        SplitMarks(place, root);
        var output = new StringBuilder(html.Length + 256);
        var at = 0;
        Serialize(root, place, output, ref at);
        return (output.ToString(), at);
    }
}
