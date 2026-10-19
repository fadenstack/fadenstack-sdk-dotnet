using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Fadenstack.Models;

namespace Fadenstack.Provenance;

/// <summary>
/// The text a reader sees of HTML and Markdown -- what someone copies out of the editor, the mail or the rendered page
/// -- so a record of markup is found by the words people copy (<see cref="RecordRequest.TextSha256"/>) and an edit is
/// judged by its words, not its tags. Both SDKs extract it the same way (the TypeScript SDK's <c>readerText</c>);
/// <c>reader-text-vectors.json</c> is the test.
/// <list type="bullet">
/// <item>HTML: comments and the contents of <c>script</c>, <c>style</c>, <c>template</c>, <c>head</c> and <c>title</c> go;
/// block elements (<c>p</c>, <c>div</c>, <c>br</c>, <c>li</c>, headings, table rows and cells, ...) become line breaks;
/// every other tag goes without a trace; entities are decoded (HTML 4's named entities and <c>&amp;apos;</c>, decimal and
/// hex references; an unknown one stays as it is).</item>
/// <item>Markdown, as a renderer shows it: front matter goes; fenced code keeps its lines and loses its fences; heading,
/// emphasis (<c>*</c>, <c>_</c>, <c>~</c>, matched as pairs), list, task and quote markers go; inline code keeps its
/// text; a link keeps its text and an image its alt text; reference definitions, thematic breaks and table delimiter
/// rows go, table cells are separated by white space; backslash escapes are resolved; HTML inside is read as HTML is.
/// Lines are read one by one: emphasis across a line break, indented code and HTML blocks are read as plain lines.</item>
/// </list>
/// </summary>
public static class ReaderText
{
    private const char Bom = (char)0xFEFF;

    private static readonly HashSet<string> HtmlTypes = new(StringComparer.Ordinal) { "text/html", "application/xhtml+xml" };

    private static readonly HashSet<string> MarkdownTypes = new(StringComparer.Ordinal) { "text/markdown", "text/x-markdown" };

    /// <summary>Whether content of this media type has a reader's text other than itself: HTML, XHTML and Markdown.</summary>
    public static bool AppliesTo(string mediaType)
    {
        var type = MediaTypes.Bare(mediaType ?? "");
        return HtmlTypes.Contains(type) || MarkdownTypes.Contains(type);
    }

    /// <summary>The text a reader sees of <paramref name="text"/>: of HTML and Markdown as <see cref="FromHtml"/> and <see cref="FromMarkdown"/> read it, anything else as it is.</summary>
    public static string Of(string text, string mediaType)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }
        var type = MediaTypes.Bare(mediaType ?? "");
        return HtmlTypes.Contains(type) ? FromHtml(text) : MarkdownTypes.Contains(type) ? FromMarkdown(text) : text;
    }

    /// <summary>
    /// For HTML and Markdown: SHA-256 of the text a reader sees, normalised as content is hashed
    /// (<see cref="ContentHash.TextSha256"/>), hex -- the record request's <c>text_sha256</c>. Null for any other media type,
    /// and for markup with no text to read.
    /// </summary>
    public static string? Sha256(string text, string mediaType)
    {
        if (!AppliesTo(mediaType))
        {
            return null;
        }
        var plain = ContentHash.Normalize(Of(text, mediaType));
        return plain.Length == 0 ? null : ContentHash.TextSha256(plain);
    }

    // -- HTML ---------------------------------------------------------------------------------------

    /// <summary>Elements whose content no reader sees.</summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal) { "script", "style", "template", "head", "title" };

    /// <summary>Elements that start a line of their own.</summary>
    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "body", "br", "caption", "center", "dd", "details", "dialog", "dir", "div", "dl",
        "dt", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr",
        "html", "legend", "li", "main", "menu", "nav", "ol", "option", "p", "pre", "section", "summary", "table", "tbody", "td",
        "tfoot", "th", "thead", "tr", "ul",
    };

    /// <summary>The plain text of HTML: what a reader sees, with a line break for each block element.</summary>
    public static string FromHtml(string html)
    {
        if (html is null)
        {
            throw new ArgumentNullException(nameof(html));
        }
        return TextOfHtml(html.Length > 0 && html[0] == Bom ? html.Substring(1) : html);
    }

    private static string TextOfHtml(string html)
    {
        var n = html.Length;
        var output = new StringBuilder(n);
        var text = 0; // where the text not yet written starts
        void Flush(int end)
        {
            if (end > text)
            {
                output.Append(DecodeEntities(html.Substring(text, end - text)));
            }
        }
        var i = 0;
        while (i < n && (i = html.IndexOf('<', i)) >= 0)
        {
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                Flush(i);
                var comment = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = text = comment < 0 ? n : comment + 3;
                continue;
            }
            var next = i + 1 < n ? html[i + 1] : '\0';
            if (next is '!' or '?')
            {
                // <!DOCTYPE ...>, <![CDATA[...]]>, <?xml ...?>: to the next ">"
                var gt = html.IndexOf('>', i + 2);
                if (gt < 0)
                {
                    i++;
                    continue;
                }
                Flush(i);
                i = text = gt + 1;
                continue;
            }
            var end = next == '/';
            var nameAt = end ? i + 2 : i + 1;
            if (nameAt >= n || !IsAsciiLetter(html[nameAt]))
            {
                i++; // a "<" in the text
                continue;
            }
            var j = nameAt;
            while (j < n && !EndsTagName(html[j]))
            {
                j++;
            }
            var close = TagEnd(html, j);
            if (close < 0)
            {
                i++; // never closed: text
                continue;
            }
            Flush(i);
            var name = AsciiLower(html.Substring(nameAt, j - nameAt));
            i = text = close;
            if (!end && Dropped.Contains(name) && html[close - 2] != '/')
            {
                i = text = AfterEndTag(html, name, close);
            }
            else if (Blocks.Contains(name))
            {
                output.Append('\n');
            }
        }
        Flush(n);
        return output.ToString();
    }

    /// <summary>A tag name in lower case: ASCII letters only, so both SDKs agree on every name.</summary>
    private static string AsciiLower(string name)
    {
        var chars = name.ToCharArray();
        for (var k = 0; k < chars.Length; k++)
        {
            if (chars[k] is >= 'A' and <= 'Z')
            {
                chars[k] = (char)(chars[k] + 32);
            }
        }
        return new string(chars);
    }

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    /// <summary>HTML's white space: tab, line feed, form feed, carriage return, space.</summary>
    private static bool IsHtmlSpace(char c) => c is '\t' or '\n' or '\f' or '\r' or ' ';

    private static bool EndsTagName(char c) => IsHtmlSpace(c) || c is '/' or '>';

    /// <summary>Just after the <c>&gt;</c> of a tag whose attributes start at <paramref name="from"/> (a <c>&gt;</c> in a quoted value does not end it); -1 for none.</summary>
    private static int TagEnd(string html, int from)
    {
        var quote = '\0';
        var equals = false;
        for (var k = from; k < html.Length; k++)
        {
            var c = html[k];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c == '>')
            {
                return k + 1;
            }
            else if (c == '=')
            {
                equals = true;
            }
            else if (equals && c is '"' or '\'')
            {
                quote = c;
                equals = false;
            }
            else if (!IsHtmlSpace(c))
            {
                equals = false;
            }
        }
        return -1;
    }

    /// <summary>Just after the end tag <c>&lt;/name ...&gt;</c> from <paramref name="from"/> on, ignoring ASCII case; the text's end when there is none.</summary>
    private static int AfterEndTag(string html, string name, int from)
    {
        for (var k = html.IndexOf("</", from, StringComparison.Ordinal); k >= 0; k = html.IndexOf("</", k + 2, StringComparison.Ordinal))
        {
            var at = k + 2 + name.Length;
            if (at <= html.Length && AsciiLower(html.Substring(k + 2, name.Length)) == name && (at >= html.Length || EndsTagName(html[at])))
            {
                var gt = html.IndexOf('>', at);
                return gt < 0 ? html.Length : gt + 1;
            }
        }
        return html.Length;
    }

    /// <summary>HTML 4's named entities and <c>apos</c>, by name and code point; the TypeScript SDK has the same table.</summary>
    internal const string EntityTable =
        "quot 34 amp 38 apos 39 lt 60 gt 62 nbsp 160 iexcl 161 cent 162 pound 163 curren 164 yen 165 brvbar 166 sect 167 uml 168 "
        + "copy 169 ordf 170 laquo 171 not 172 shy 173 reg 174 macr 175 deg 176 plusmn 177 sup2 178 sup3 179 acute 180 micro 181 "
        + "para 182 middot 183 cedil 184 sup1 185 ordm 186 raquo 187 frac14 188 frac12 189 frac34 190 iquest 191 Agrave 192 "
        + "Aacute 193 Acirc 194 Atilde 195 Auml 196 Aring 197 AElig 198 Ccedil 199 Egrave 200 Eacute 201 Ecirc 202 Euml 203 "
        + "Igrave 204 Iacute 205 Icirc 206 Iuml 207 ETH 208 Ntilde 209 Ograve 210 Oacute 211 Ocirc 212 Otilde 213 Ouml 214 "
        + "times 215 Oslash 216 Ugrave 217 Uacute 218 Ucirc 219 Uuml 220 Yacute 221 THORN 222 szlig 223 agrave 224 aacute 225 "
        + "acirc 226 atilde 227 auml 228 aring 229 aelig 230 ccedil 231 egrave 232 eacute 233 ecirc 234 euml 235 igrave 236 "
        + "iacute 237 icirc 238 iuml 239 eth 240 ntilde 241 ograve 242 oacute 243 ocirc 244 otilde 245 ouml 246 divide 247 "
        + "oslash 248 ugrave 249 uacute 250 ucirc 251 uuml 252 yacute 253 thorn 254 yuml 255 OElig 338 oelig 339 Scaron 352 "
        + "scaron 353 Yuml 376 fnof 402 circ 710 tilde 732 Alpha 913 Beta 914 Gamma 915 Delta 916 Epsilon 917 Zeta 918 Eta 919 "
        + "Theta 920 Iota 921 Kappa 922 Lambda 923 Mu 924 Nu 925 Xi 926 Omicron 927 Pi 928 Rho 929 Sigma 931 Tau 932 Upsilon 933 "
        + "Phi 934 Chi 935 Psi 936 Omega 937 alpha 945 beta 946 gamma 947 delta 948 epsilon 949 zeta 950 eta 951 theta 952 "
        + "iota 953 kappa 954 lambda 955 mu 956 nu 957 xi 958 omicron 959 pi 960 rho 961 sigmaf 962 sigma 963 tau 964 "
        + "upsilon 965 phi 966 chi 967 psi 968 omega 969 thetasym 977 upsih 978 piv 982 ensp 8194 emsp 8195 thinsp 8201 "
        + "zwnj 8204 zwj 8205 lrm 8206 rlm 8207 ndash 8211 mdash 8212 lsquo 8216 rsquo 8217 sbquo 8218 ldquo 8220 rdquo 8221 "
        + "bdquo 8222 dagger 8224 Dagger 8225 bull 8226 hellip 8230 permil 8240 prime 8242 Prime 8243 lsaquo 8249 rsaquo 8250 "
        + "oline 8254 frasl 8260 euro 8364 image 8465 weierp 8472 real 8476 trade 8482 alefsym 8501 larr 8592 uarr 8593 "
        + "rarr 8594 darr 8595 harr 8596 crarr 8629 lArr 8656 uArr 8657 rArr 8658 dArr 8659 hArr 8660 forall 8704 part 8706 "
        + "exist 8707 empty 8709 nabla 8711 isin 8712 notin 8713 ni 8715 prod 8719 sum 8721 minus 8722 lowast 8727 radic 8730 "
        + "prop 8733 infin 8734 ang 8736 and 8743 or 8744 cap 8745 cup 8746 int 8747 there4 8756 sim 8764 cong 8773 asymp 8776 "
        + "ne 8800 equiv 8801 le 8804 ge 8805 sub 8834 sup 8835 nsub 8836 sube 8838 supe 8839 oplus 8853 otimes 8855 perp 8869 "
        + "sdot 8901 lceil 8968 rceil 8969 lfloor 8970 rfloor 8971 lang 9001 rang 9002 loz 9674 spades 9824 clubs 9827 "
        + "hearts 9829 diams 9830";

    private static readonly Lazy<Dictionary<string, string>> NamedEntities = new(() =>
    {
        var parts = EntityTable.Split(' ');
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var k = 0; k < parts.Length; k += 2)
        {
            map[parts[k]] = char.ConvertFromUtf32(int.Parse(parts[k + 1], CultureInfo.InvariantCulture));
        }
        return map;
    });

    private static readonly Regex Entity = new("&(?:#([0-9]{1,7})|#[xX]([0-9a-fA-F]{1,6})|([A-Za-z][A-Za-z0-9]{0,31}));", RegexOptions.CultureInvariant);

    /// <summary>
    /// Text with its character references decoded: the named ones of HTML 4 (and <c>&amp;apos;</c>, case as written), decimal
    /// and hex ones up to U+10FFFF but surrogates and U+0000. Anything else stays as it is.
    /// </summary>
    public static string DecodeEntities(string text)
    {
        if (text is null || text.IndexOf('&') < 0)
        {
            return text ?? "";
        }
        return Entity.Replace(text, m =>
        {
            if (m.Groups[3].Success)
            {
                return NamedEntities.Value.TryGetValue(m.Groups[3].Value, out var named) ? named : m.Value;
            }
            var code = m.Groups[1].Success
                ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
                : int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return code is > 0 and <= 0x10FFFF and (< 0xD800 or > 0xDFFF) ? char.ConvertFromUtf32(code) : m.Value;
        });
    }

    // -- Markdown -----------------------------------------------------------------------------------

    private static readonly Regex LineBreak = new("\r\n|\r|\n", RegexOptions.CultureInvariant);
    private static readonly Regex Blank = new(@"^[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Quote = new(@"^ {0,3}> ?", RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^[ \t]*(`{3,}|~{3,})([\s\S]*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Definition = new(@"^ {0,3}\[([^\]^][^\]]*)\]:[ \t]*[^ \t]+(?:[ \t]+(?:""[^""]*""|'[^']*'|\([^)]*\)))?[ \t]*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex DelimiterRow = new(@"^[ \t]*\|?[ \t]*:?-+:?[ \t]*(?:\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex ThematicBreak = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SetextUnderline = new(@"^ {0,3}=+[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(@"^ {0,3}#{1,6}(?:[ \t]+|$)", RegexOptions.CultureInvariant);
    private static readonly Regex HeadingClose = new(@"(?:^|[ \t]+)#+[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Bullet = new(@"^[ \t]*[-*+](?:[ \t]+|$)", RegexOptions.CultureInvariant);
    private static readonly Regex Ordered = new(@"^[ \t]*([0-9]{1,9})[.)](?:[ \t]+|$)", RegexOptions.CultureInvariant);
    private static readonly Regex TaskBox = new(@"^\[[ xX]\](?:[ \t]+|$)", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new("[ \t]+", RegexOptions.CultureInvariant);
    private static readonly Regex Autolink = new(@"\G<([A-Za-z][A-Za-z0-9+.-]{1,31}:[^\x00-\x20<>]*)>", RegexOptions.CultureInvariant);
    private static readonly Regex EmailAutolink = new(
        @"\G<([A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*)>",
        RegexOptions.CultureInvariant);

    private const string AsciiPunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    private enum LineKind
    {
        Text,
        Code,
        Drop,
        Row,
    }

    /// <summary>What the line before was, for the rules that depend on it (an underline, an ordered list after a paragraph).</summary>
    private enum LineState
    {
        Start,
        Blank,
        Paragraph,
        List,
        Other,
    }

    /// <summary>A piece of the text: markup is read as HTML afterwards, a literal (code, an escaped character) stays as it is.</summary>
    private readonly record struct Segment(bool Literal, string Text);

    private static readonly Segment NewLine = new(false, "\n");
    private static readonly Segment Cell = new(false, "\t");

    /// <summary>The plain text of Markdown, as a renderer shows it.</summary>
    public static string FromMarkdown(string markdown)
    {
        if (markdown is null)
        {
            throw new ArgumentNullException(nameof(markdown));
        }
        var lines = LineBreak.Split(markdown.Length > 0 && markdown[0] == Bom ? markdown.Substring(1) : markdown).ToList();
        if (lines[0] == "---")
        {
            var close = -1;
            for (var k = 1; k < lines.Count && close < 0; k++)
            {
                if (lines[k] is "---" or "...")
                {
                    close = k;
                }
            }
            if (close > 0)
            {
                lines = lines.Skip(close + 1).ToList();
            }
        }
        var kinds = lines.Select(_ => LineKind.Text).ToArray();
        var bodies = new List<string>(lines.Count);
        var definitions = new HashSet<string>(StringComparer.Ordinal);

        // Quotes, fenced code and reference definitions.
        (char Char, int Length, int Depth)? fence = null;
        for (var k = 0; k < lines.Count; k++)
        {
            var line = lines[k];
            if (fence is { } open)
            {
                var d = 0;
                for (var q = Quote.Match(line); d < open.Depth && q.Success; d++, q = Quote.Match(line))
                {
                    line = line.Substring(q.Length);
                }
                var closing = Fence.Match(line);
                if (closing.Success && closing.Groups[1].Value[0] == open.Char && closing.Groups[1].Length >= open.Length && Blank.IsMatch(closing.Groups[2].Value))
                {
                    kinds[k] = LineKind.Drop;
                    fence = null;
                }
                else
                {
                    kinds[k] = LineKind.Code;
                }
                bodies.Add(line);
                continue;
            }
            var depth = 0;
            for (var q = Quote.Match(line); q.Success; q = Quote.Match(line))
            {
                line = line.Substring(q.Length);
                depth++;
            }
            bodies.Add(line);
            var opening = Fence.Match(line);
            if (opening.Success && !(opening.Groups[1].Value[0] == '`' && opening.Groups[2].Value.IndexOf('`') >= 0))
            {
                kinds[k] = LineKind.Drop;
                fence = (opening.Groups[1].Value[0], opening.Groups[1].Length, depth);
                continue;
            }
            var definition = Definition.Match(line);
            if (definition.Success)
            {
                kinds[k] = LineKind.Drop;
                definitions.Add(LabelOf(definition.Groups[1].Value));
            }
        }

        // Tables: a header row, a delimiter row, and the rows after it.
        for (var k = 1; k < lines.Count; k++)
        {
            if (kinds[k] != LineKind.Text || kinds[k - 1] != LineKind.Text)
            {
                continue;
            }
            if (!DelimiterRow.IsMatch(bodies[k]) || bodies[k].IndexOf('|') < 0 || !HasPipe(bodies[k - 1]))
            {
                continue;
            }
            kinds[k - 1] = LineKind.Row;
            kinds[k] = LineKind.Drop;
            for (var r = k + 1; r < lines.Count && kinds[r] == LineKind.Text && !Blank.IsMatch(bodies[r]) && HasPipe(bodies[r]); r++)
            {
                kinds[r] = LineKind.Row;
            }
        }

        var segments = new List<Segment>();
        var previous = LineState.Start;
        for (var k = 0; k < lines.Count; k++)
        {
            var body = bodies[k];
            switch (kinds[k])
            {
                case LineKind.Code:
                    segments.Add(new Segment(true, body));
                    segments.Add(NewLine);
                    previous = LineState.Other;
                    continue;
                case LineKind.Drop:
                    previous = LineState.Other;
                    continue;
                case LineKind.Row:
                    foreach (var cell in TableCells(body))
                    {
                        Inline(cell, definitions, segments);
                        segments.Add(Cell);
                    }
                    segments.Add(NewLine);
                    previous = LineState.Other;
                    continue;
            }
            if (Blank.IsMatch(body))
            {
                segments.Add(NewLine);
                previous = LineState.Blank;
                continue;
            }
            if (ThematicBreak.IsMatch(body) || (SetextUnderline.IsMatch(body) && previous == LineState.Paragraph))
            {
                previous = LineState.Other;
                continue;
            }
            var now = previous == LineState.List && body.Length > 0 && body[0] is ' ' or '\t' ? LineState.List : LineState.Paragraph;
            var heading = Heading.Match(body);
            if (heading.Success)
            {
                body = HeadingClose.Replace(body.Substring(heading.Length), "");
                now = LineState.Other;
            }
            else
            {
                var bullet = Bullet.Match(body);
                var ordered = bullet.Success ? null : Ordered.Match(body);
                var marker = bullet.Success ? bullet
                    : ordered is { Success: true } && (int.Parse(ordered.Groups[1].Value, CultureInfo.InvariantCulture) == 1 || previous != LineState.Paragraph)
                        ? ordered : null;
                if (marker is not null)
                {
                    body = body.Substring(marker.Length);
                    var task = TaskBox.Match(body);
                    if (task.Success)
                    {
                        body = body.Substring(task.Length);
                    }
                    now = LineState.List;
                }
            }
            Inline(body, definitions, segments);
            segments.Add(NewLine);
            previous = now;
        }

        var output = new StringBuilder();
        var markup = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment.Literal)
            {
                output.Append(TextOfHtml(markup.ToString())).Append(segment.Text);
                markup.Clear();
            }
            else
            {
                markup.Append(segment.Text);
            }
        }
        return output.Append(TextOfHtml(markup.ToString())).ToString();
    }

    /// <summary>A reference label as definitions and references are matched: white space runs one space, trimmed, in lower case.</summary>
    private static string LabelOf(string label) => Spaces.Replace(label, " ").Trim(' ').ToLowerInvariant();

    /// <summary>Whether a line has a <c>|</c> that is not escaped.</summary>
    private static bool HasPipe(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '|' && (i == 0 || line[i - 1] != '\\'))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A table row's cells, as GFM splits them: the outer pipes go, every other <c>|</c> that is not escaped separates two
    /// cells (inside code too), and <c>\|</c> is a <c>|</c> in its cell.
    /// </summary>
    private static List<string> TableCells(string row)
    {
        var s = row.Trim(' ', '\t');
        if (s.StartsWith("|", StringComparison.Ordinal))
        {
            s = s.Substring(1);
        }
        if (s.EndsWith("|", StringComparison.Ordinal) && !s.EndsWith("\\|", StringComparison.Ordinal))
        {
            s = s.Substring(0, s.Length - 1);
        }
        var cells = new List<string>();
        var cell = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (s[i] == '|')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(s[i]);
            }
        }
        cells.Add(cell.ToString());
        return cells;
    }

    private enum TokenKind
    {
        Text,
        Literal,
        Delimiter,
    }

    private sealed class Token
    {
        public TokenKind Kind { get; init; }

        public string Text { get; init; } = "";

        public char Char { get; init; }

        public int Length { get; init; }

        public bool Open { get; init; }

        public bool Close { get; init; }

        public bool Matched { get; set; }
    }

    private static bool IsPunctuation(char c) => AsciiPunctuation.IndexOf(c) >= 0 || char.IsPunctuation(c);

    /// <summary>White space as hashing counts it (<see cref="ContentHash.Normalize"/>).</summary>
    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= (char)0x1C and <= (char)0x1F;

    /// <summary>One line's inline Markdown, as segments: emphasis that pairs goes, code and escapes stay as they are.</summary>
    private static void Inline(string line, HashSet<string> definitions, List<Segment> segments)
    {
        var tokens = Tokenize(line, definitions);
        // Emphasis: a closing run takes the nearest opening run of its character; runs that pair go, the others stay text.
        var openers = new List<int>();
        for (var k = 0; k < tokens.Count; k++)
        {
            var t = tokens[k];
            if (t.Kind != TokenKind.Delimiter)
            {
                continue;
            }
            if (t.Close)
            {
                var m = openers.Count - 1;
                while (m >= 0 && tokens[openers[m]].Char != t.Char)
                {
                    m--;
                }
                if (m >= 0)
                {
                    tokens[openers[m]].Matched = true;
                    t.Matched = true;
                    openers.RemoveRange(m, openers.Count - m);
                    continue;
                }
            }
            if (t.Open)
            {
                openers.Add(k);
            }
        }
        foreach (var t in tokens)
        {
            switch (t.Kind)
            {
                case TokenKind.Literal:
                    segments.Add(new Segment(true, t.Text));
                    break;
                case TokenKind.Text:
                    segments.Add(new Segment(false, t.Text));
                    break;
                default:
                    if (!t.Matched)
                    {
                        segments.Add(new Segment(false, new string(t.Char, t.Length)));
                    }
                    break;
            }
        }
    }

    private static List<Token> Tokenize(string s, HashSet<string> definitions)
    {
        var tokens = new List<Token>();
        var text = new StringBuilder();
        void Push()
        {
            if (text.Length > 0)
            {
                tokens.Add(new Token { Kind = TokenKind.Text, Text = text.ToString() });
            }
            text.Clear();
        }
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && AsciiPunctuation.IndexOf(s[i + 1]) >= 0)
            {
                Push();
                tokens.Add(new Token { Kind = TokenKind.Literal, Text = s[i + 1].ToString() });
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var run = RunOf(s, i, '`');
                var close = Backticks(s, i + run, run);
                if (close < 0)
                {
                    text.Append(s, i, run);
                    i += run;
                    continue;
                }
                var code = s.Substring(i + run, close - i - run);
                if (code.Length >= 2 && code[0] == ' ' && code[code.Length - 1] == ' ' && code.Any(ch => ch != ' '))
                {
                    code = code.Substring(1, code.Length - 2);
                }
                Push();
                tokens.Add(new Token { Kind = TokenKind.Literal, Text = code });
                i = close + run;
                continue;
            }
            if (c == '<')
            {
                var link = Autolink.Match(s, i);
                if (!link.Success)
                {
                    link = EmailAutolink.Match(s, i);
                }
                if (link.Success)
                {
                    Push();
                    tokens.Add(new Token { Kind = TokenKind.Literal, Text = link.Groups[1].Value });
                    i += link.Length;
                    continue;
                }
            }
            var image = c == '!' && i + 1 < s.Length && s[i + 1] == '[';
            if (c == '[' || image)
            {
                if (LinkAt(s, image ? i + 1 : i, definitions) is { } link)
                {
                    Push();
                    tokens.AddRange(Tokenize(link.Text, definitions));
                    i = link.End;
                    continue;
                }
            }
            if (c is '*' or '_' or '~')
            {
                var run = RunOf(s, i, c);
                var before = i > 0 ? s[i - 1] : ' ';
                var after = i + run < s.Length ? s[i + run] : ' ';
                var left = !IsSpace(after) && (!IsPunctuation(after) || IsSpace(before) || IsPunctuation(before));
                var right = !IsSpace(before) && (!IsPunctuation(before) || IsSpace(after) || IsPunctuation(after));
                Push();
                tokens.Add(new Token
                {
                    Kind = TokenKind.Delimiter, Char = c, Length = run,
                    Open = c == '_' ? left && (!right || IsPunctuation(before)) : left,
                    Close = c == '_' ? right && (!left || IsPunctuation(after)) : right,
                });
                i += run;
                continue;
            }
            text.Append(c);
            i++;
        }
        Push();
        return tokens;
    }

    /// <summary>How many <paramref name="c"/> in a row from <paramref name="at"/>.</summary>
    private static int RunOf(string s, int at, char c)
    {
        var k = at;
        while (k < s.Length && s[k] == c)
        {
            k++;
        }
        return k - at;
    }

    /// <summary>Where the next run of exactly <paramref name="length"/> backticks from <paramref name="from"/> starts, or -1.</summary>
    private static int Backticks(string s, int from, int length)
    {
        for (var k = from < s.Length ? s.IndexOf('`', from) : -1; k >= 0; k = k < s.Length ? s.IndexOf('`', k) : -1)
        {
            var run = RunOf(s, k, '`');
            if (run == length)
            {
                return k;
            }
            k += run;
        }
        return -1;
    }

    /// <summary>A link at <paramref name="open"/> (its <c>[</c>): inline <c>[text](...)</c>, a reference <c>[text][ref]</c> or <c>[text][]</c>, or <c>[text]</c> that is defined.</summary>
    private static (string Text, int End)? LinkAt(string s, int open, HashSet<string> definitions)
    {
        var close = Matching(s, open, '[', ']');
        if (close < 0)
        {
            return null;
        }
        var text = s.Substring(open + 1, close - open - 1);
        var next = close + 1 < s.Length ? s[close + 1] : '\0';
        if (next == '(')
        {
            var end = Matching(s, close + 1, '(', ')');
            return end < 0 ? null : (text, end + 1);
        }
        if (next == '[')
        {
            var end = Matching(s, close + 1, '[', ']');
            if (end < 0)
            {
                return null;
            }
            var reference = s.Substring(close + 2, end - close - 2);
            return definitions.Contains(LabelOf(reference.Length == 0 ? text : reference)) ? (text, end + 1) : null;
        }
        return definitions.Contains(LabelOf(text)) ? (text, close + 1) : null;
    }

    /// <summary>The <paramref name="closer"/> that closes the <paramref name="opener"/> at <paramref name="at"/>, nested pairs and escaped characters skipped; -1 for none.</summary>
    private static int Matching(string s, int at, char opener, char closer)
    {
        var depth = 0;
        for (var k = at; k < s.Length; k++)
        {
            var c = s[k];
            if (c == '\\')
            {
                k++;
            }
            else if (c == opener)
            {
                depth++;
            }
            else if (c == closer && --depth == 0)
            {
                return k;
            }
        }
        return -1;
    }
}
