using System.Globalization;
using System.Text;

namespace Fadenstack.Skills;

/// <summary>The YAML of a skill's <c>faden.yaml</c> is not accepted: it is too large, too deep, or uses something a skill may not use.</summary>
internal sealed class FadenYamlException : Exception
{
    public FadenYamlException(string message) : base(message)
    {
    }
}

/// <summary>
/// A small reader for <c>faden.yaml</c>, which comes from whoever wrote the skill (untrusted input). It holds the limits of core's
/// <c>skills/faden_skills/services/safe_yaml.py</c>: at most 32 KiB, nesting at most 8 levels (scalars count as a level, as the
/// parser's nodes do there), and no anchors (<c>&amp;a</c>), aliases (<c>*a</c>) or tags (<c>!t</c>) -- without references a document
/// cannot grow past its own size. Beyond core it refuses what a skill never needs and a hand-written reader would get wrong: block
/// scalars (<c>|</c>, <c>&gt;</c>), a second document, tabs in the indentation, multi-line plain or quoted scalars, complex keys, and
/// control or invisible characters. It reads block maps and lists, flow lists and maps, plain, single- and double-quoted scalars and
/// comments. Plain scalars resolve like PyYAML's safe loader does (YAML 1.1: <c>yes</c>/<c>no</c>/<c>on</c>/<c>off</c> are booleans,
/// integers, <c>null</c>/<c>~</c>), so a file reads to the same types here and in core; everything else is a string. Values are
/// <c>null</c>, <see cref="bool"/>, <see cref="long"/>, <see cref="string"/>, <see cref="List{T}"/> of object and
/// <see cref="Dictionary{TKey, TValue}"/> of string to object (a repeated key: the last wins, as in PyYAML).
/// </summary>
internal static class FadenYaml
{
    internal const int MaxBytes = 32 * 1024;
    internal const int MaxDepth = 8;

    private static readonly string[] True = { "true", "True", "TRUE", "yes", "Yes", "YES", "on", "On", "ON" };
    private static readonly string[] False = { "false", "False", "FALSE", "no", "No", "NO", "off", "Off", "OFF" };

    /// <summary>The value of the YAML in <paramref name="text"/>; throws <see cref="FadenYamlException"/> when it is not accepted.</summary>
    internal static object? Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            throw new FadenYamlException("The YAML is larger than 32 KiB.");
        }
        CheckCharacters(text);
        return new Reader(Lines(text)).Document();
    }

    private static FadenYamlException Deep() => new("The YAML nests more than " + MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels deep.");

    private static void CheckCharacters(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var invisible = (c < ' ' && c != '\t' && c != '\n' && c != '\r') || c == '\u007f' || (c >= '\u0080' && c <= '\u009f')
                || (c >= (char)0x200b && c <= (char)0x200f) || (c >= (char)0x202a && c <= (char)0x202e) || (c >= (char)0x2060 && c <= (char)0x2064)
                || (c >= (char)0x2066 && c <= (char)0x2069) || c == (char)0x00ad || (c == (char)0xfeff && i > 0);
            if (invisible)
            {
                throw new FadenYamlException("The YAML holds a control or invisible character.");
            }
        }
    }

    private readonly struct Line
    {
        public Line(int indent, string text)
        {
            Indent = indent;
            Text = text;
        }

        public int Indent { get; }

        public string Text { get; }
    }

    private static List<Line> Lines(string text)
    {
        var lines = new List<Line>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var indent = 0;
            while (indent < raw.Length && raw[indent] == ' ')
            {
                indent++;
            }
            if (indent < raw.Length && raw[indent] == '\t')
            {
                throw new FadenYamlException("The YAML indents with a tab.");
            }
            var content = StripComment(raw.Substring(indent)).TrimEnd();
            if (content.Length > 0)
            {
                lines.Add(new Line(indent, content));
            }
        }
        return lines;
    }

    /// <summary>The text before a comment: a <c>#</c> at the start or after a space, outside quotes.</summary>
    private static string StripComment(string text)
    {
        var quote = '\0';
        var previous = '\0'; // the last character that is not a space
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (quote == '"' && c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if ((c == '"' || c == '\'') && (previous == '\0' || previous == ':' || previous == '-' || previous == '[' || previous == ',' || previous == '{'))
            {
                quote = c;
            }
            else if (c == '#' && (i == 0 || text[i - 1] == ' ' || text[i - 1] == '\t'))
            {
                return text.Substring(0, i);
            }
            if (c != ' ' && c != '\t')
            {
                previous = c;
            }
        }
        return text;
    }

    private sealed class Reader
    {
        private readonly List<Line> _lines;
        private int _pos;

        public Reader(List<Line> lines) => _lines = lines;

        public object? Document()
        {
            if (_lines.Count > 0 && _lines[0].Text == "---")
            {
                _pos = 1;
            }
            if (_pos >= _lines.Count)
            {
                return null;
            }
            for (var i = _pos; i < _lines.Count; i++)
            {
                var line = _lines[i];
                if (line.Indent == 0 && (line.Text == "---" || line.Text == "..." || line.Text.StartsWith("--- ", StringComparison.Ordinal)))
                {
                    throw new FadenYamlException("The YAML holds more than one document.");
                }
            }
            var value = Block(_lines[_pos].Indent, 1);
            if (_pos < _lines.Count)
            {
                throw new FadenYamlException("The YAML is not laid out as a map or a list.");
            }
            return value;
        }

        private object? Block(int indent, int depth)
        {
            if (depth > MaxDepth)
            {
                throw Deep();
            }
            var line = _lines[_pos];
            if (IsItem(line.Text))
            {
                return List(indent, depth);
            }
            if (KeyOf(line.Text) is not null)
            {
                return Map(indent, depth);
            }
            // A lone scalar document.
            _pos++;
            var scalar = Inline(line.Text, depth);
            if (_pos < _lines.Count)
            {
                throw new FadenYamlException("The YAML holds a scalar that goes over several lines.");
            }
            return scalar;
        }


        private static bool IsItem(string text) => text == "-" || text.StartsWith("- ", StringComparison.Ordinal);

        private Dictionary<string, object?> Map(int indent, int depth)
        {
            if (depth + 1 > MaxDepth)
            {
                throw Deep(); // the keys are nodes too
            }
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (_pos < _lines.Count && _lines[_pos].Indent == indent && KeyOf(_lines[_pos].Text) is { } entry)
            {
                _pos++;
                map[entry.Name] = entry.Tail.Length == 0 ? Nested(indent, depth) : Inline(entry.Tail, depth + 1);
            }
            Finish(indent, true);
            return map;
        }

        /// <summary>The value on the lines after <c>key:</c>: a block indented further (or a list at the key's own indent), else null.</summary>
        private object? Nested(int indent, int depth)
        {
            if (_pos < _lines.Count)
            {
                var next = _lines[_pos];
                if (next.Indent > indent || (next.Indent == indent && IsItem(next.Text)))
                {
                    return Block(next.Indent, depth + 1);
                }
            }
            return null;
        }

        private List<object?> List(int indent, int depth)
        {
            var list = new List<object?>();
            while (_pos < _lines.Count && _lines[_pos].Indent == indent && IsItem(_lines[_pos].Text))
            {
                var text = _lines[_pos].Text;
                var rest = text.Substring(1).TrimStart();
                var inner = indent + (text.Length - rest.Length);
                if (rest.Length == 0)
                {
                    _pos++;
                    list.Add(Nested(indent, depth));
                }
                else if (IsItem(rest) || KeyOf(rest) is not null)
                {
                    // "- key: value" or "- - x": the rest is the first line of a block that starts further in.
                    _lines[_pos] = new Line(inner, rest);
                    list.Add(Block(inner, depth + 1));
                }
                else
                {
                    _pos++;
                    list.Add(Inline(rest, depth + 1));
                }
            }
            Finish(indent, false);
            return list;
        }

        /// <summary>A block ends at a line that is less indented; a map also ends at a line of its own indent that is a key, and a list at one that is not an item (the map around it goes on).</summary>
        private void Finish(int indent, bool map)
        {
            if (_pos < _lines.Count && (_lines[_pos].Indent > indent || (map && _lines[_pos].Indent == indent)))
            {
                throw new FadenYamlException("The YAML is not laid out as a map or a list.");
            }
        }

        /// <summary>The key and the rest of a <c>key: rest</c> line; null when the line is not one (a flow value, a list item, a scalar).</summary>
        private static (string Name, string Tail)? KeyOf(string text)
        {
            if (text[0] == '[' || text[0] == '{' || IsItem(text))
            {
                return null;
            }
            string key;
            int after;
            if (text[0] == '"' || text[0] == '\'')
            {
                var end = QuoteEnd(text, 0);
                if (end < 0)
                {
                    return null;
                }
                key = Quoted(text.Substring(0, end + 1));
                after = end + 1;
                while (after < text.Length && text[after] == ' ')
                {
                    after++;
                }
                if (after >= text.Length || text[after] != ':' || (after + 1 < text.Length && text[after + 1] != ' '))
                {
                    return null;
                }
            }
            else
            {
                after = -1;
                for (var i = 0; i < text.Length; i++)
                {
                    if (text[i] == ':' && (i + 1 == text.Length || text[i + 1] == ' '))
                    {
                        after = i;
                        break;
                    }
                }
                if (after <= 0)
                {
                    return null;
                }
                var plain = text.Substring(0, after).TrimEnd();
                Indicator(plain);
                key = plain;
            }
            return (key, text.Substring(after + 1).Trim());
        }

        /// <summary>The value written on a line after its key or list dash.</summary>
        private object? Inline(string text, int depth)
        {
            if (depth > MaxDepth)
            {
                throw Deep();
            }
            if (text[0] == '[' || text[0] == '{')
            {
                var source = text;
                while (true)
                {
                    try
                    {
                        var flow = new Flow(source);
                        var value = flow.Value(depth);
                        flow.End();
                        return value;
                    }
                    catch (EndOfFlow) when (_pos < _lines.Count)
                    {
                        source += " " + _lines[_pos].Text;
                        _pos++;
                    }
                    catch (EndOfFlow)
                    {
                        throw new FadenYamlException("A bracket in the YAML is not closed.");
                    }
                }
            }
            if (text[0] == '"' || text[0] == '\'')
            {
                var end = QuoteEnd(text, 0);
                if (end < 0)
                {
                    throw new FadenYamlException("A quoted value in the YAML is not closed on its line.");
                }
                if (text.Substring(end + 1).Trim().Length > 0)
                {
                    throw new FadenYamlException("A quoted value in the YAML is followed by more text.");
                }
                return Quoted(text.Substring(0, end + 1));
            }
            Indicator(text);
            if (text.IndexOf(": ", StringComparison.Ordinal) >= 0 || text.EndsWith(":", StringComparison.Ordinal))
            {
                throw new FadenYamlException("The YAML holds a map where a value is expected.");
            }
            return Plain(text);
        }
    }

    /// <summary>The characters a plain scalar may not start with, each with the reason.</summary>
    private static void Indicator(string text)
    {
        switch (text[0])
        {
            case '&':
                throw new FadenYamlException("YAML anchors (&name) are not allowed.");
            case '*':
                throw new FadenYamlException("YAML aliases (*name) are not allowed.");
            case '!':
                throw new FadenYamlException("YAML tags (!tag) are not allowed.");
            case '|':
            case '>':
                throw new FadenYamlException("YAML block scalars (| and >) are not read; write the value on one line.");
            case '?':
                if (text.Length == 1 || text[1] == ' ')
                {
                    throw new FadenYamlException("YAML complex keys are not allowed.");
                }
                break;
            case '%':
            case '@':
            case '`':
                throw new FadenYamlException("The YAML holds a character that may not start a value.");
        }
    }

    /// <summary>The index of the quote that closes the one at <paramref name="start"/>; -1 when there is none.</summary>
    private static int QuoteEnd(string text, int start)
    {
        var quote = text[start];
        for (var i = start + 1; i < text.Length; i++)
        {
            if (quote == '"' && text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == quote)
            {
                if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++; // '' is a quote in a single-quoted scalar
                    continue;
                }
                return i;
            }
        }
        return -1;
    }

    private static string Quoted(string token)
    {
        var inner = token.Substring(1, token.Length - 2);
        if (token[0] == '\'')
        {
            return inner.Replace("''", "'");
        }
        var sb = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            i++;
            if (i >= inner.Length)
            {
                throw new FadenYamlException("A quoted value in the YAML ends in a backslash.");
            }
            switch (inner[i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '0': sb.Append('\0'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case ' ': sb.Append(' '); break;
                case 'x' or 'u' when HexEscape(inner, ref i, inner[i] == 'x' ? 2 : 4) is { } decoded: sb.Append(decoded); break;
                default:
                    throw new FadenYamlException("A quoted value in the YAML holds an escape that is not read.");
            }
        }
        var result = sb.ToString();
        CheckCharacters(result);
        return result;
    }

    private static string? HexEscape(string text, ref int index, int digits)
    {
        if (index + digits >= text.Length)
        {
            return null;
        }
        var hex = text.Substring(index + 1, digits);
        if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
        {
            return null;
        }
        index += digits;
        return ((char)code).ToString();
    }

    /// <summary>A plain scalar as PyYAML's safe loader resolves it (YAML 1.1): null, a boolean, an integer, else the text.</summary>
    private static object? Plain(string text)
    {
        if (text == "~" || text == "null" || text == "Null" || text == "NULL")
        {
            return null;
        }
        if (Array.IndexOf(True, text) >= 0)
        {
            return true;
        }
        if (Array.IndexOf(False, text) >= 0)
        {
            return false;
        }
        if (Integer(text) is { } number)
        {
            return number;
        }
        return text;
    }

    private static long? Integer(string text)
    {
        var s = text.Replace("_", "");
        if (s.Length == 0 || text[0] == '_' || text[text.Length - 1] == '_')
        {
            return null;
        }
        var negative = s[0] == '-';
        var digits = s[0] == '-' || s[0] == '+' ? s.Substring(1) : s;
        if (digits.Length == 0)
        {
            return null;
        }
        long value;
        if (digits.Length > 2 && digits[0] == '0' && (digits[1] == 'x' || digits[1] == 'X'))
        {
            if (!long.TryParse(digits.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
            {
                return null;
            }
        }
        else if (digits.Length > 1 && digits[0] == '0')
        {
            // Octal, as YAML 1.1 reads a leading zero; 09 is not a number there.
            value = 0;
            foreach (var c in digits)
            {
                if (c < '0' || c > '7' || value > long.MaxValue / 8 - 1)
                {
                    return null;
                }
                value = (value * 8) + (c - '0');
            }
        }
        else if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return null;
        }
        return negative ? -value : value;
    }

    private sealed class EndOfFlow : Exception
    {
    }

    /// <summary>A flow collection (<c>[a, b]</c>, <c>{k: v}</c>) in one string.</summary>
    private sealed class Flow
    {
        private readonly string _text;
        private int _at;

        public Flow(string text) => _text = text;

        public void End()
        {
            Space();
            if (_at < _text.Length)
            {
                throw new FadenYamlException("A flow value in the YAML is followed by more text.");
            }
        }

        private void Space()
        {
            while (_at < _text.Length && (_text[_at] == ' ' || _text[_at] == '\t'))
            {
                _at++;
            }
        }

        public object? Value(int depth)
        {
            if (depth > MaxDepth)
            {
                throw Deep();
            }
            Space();
            if (_at >= _text.Length)
            {
                throw new EndOfFlow();
            }
            var c = _text[_at];
            if (c == '[')
            {
                _at++;
                var list = new List<object?>();
                while (true)
                {
                    Space();
                    if (_at >= _text.Length)
                    {
                        throw new EndOfFlow();
                    }
                    if (_text[_at] == ']')
                    {
                        _at++;
                        return list;
                    }
                    list.Add(Value(depth + 1));
                    Separator(']');
                }
            }
            if (c == '{')
            {
                _at++;
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                while (true)
                {
                    Space();
                    if (_at >= _text.Length)
                    {
                        throw new EndOfFlow();
                    }
                    if (_text[_at] == '}')
                    {
                        _at++;
                        return map;
                    }
                    var key = Value(depth + 1) as string ?? throw new FadenYamlException("A key in a flow map of the YAML is not text.");
                    Space();
                    if (_at < _text.Length && _text[_at] == ':')
                    {
                        _at++;
                        map[key] = Value(depth + 1);
                    }
                    else
                    {
                        map[key] = null;
                    }
                    Separator('}');
                }
            }
            if (c == '"' || c == '\'')
            {
                var end = QuoteEnd(_text, _at);
                if (end < 0)
                {
                    throw new EndOfFlow();
                }
                var token = _text.Substring(_at, end + 1 - _at);
                _at = end + 1;
                return Quoted(token);
            }
            var start = _at;
            while (_at < _text.Length && _text[_at] != ',' && _text[_at] != ']' && _text[_at] != '}'
                && !(_text[_at] == ':' && (_at + 1 == _text.Length || _text[_at + 1] == ' ' || _text[_at + 1] == ',' || _text[_at + 1] == ']' || _text[_at + 1] == '}')))
            {
                _at++;
            }
            var plain = _text.Substring(start, _at - start).Trim();
            if (plain.Length == 0)
            {
                return null;
            }
            Indicator(plain);
            return Plain(plain);
        }

        private void Separator(char close)
        {
            Space();
            if (_at >= _text.Length)
            {
                throw new EndOfFlow();
            }
            if (_text[_at] == ',')
            {
                _at++;
            }
            else if (_text[_at] != close)
            {
                throw new FadenYamlException("A flow value in the YAML is not laid out as a list or a map.");
            }
        }
    }
}
