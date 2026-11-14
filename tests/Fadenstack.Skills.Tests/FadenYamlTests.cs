using System.Text;
using Xunit;

namespace Fadenstack.Skills.Tests;

/// <summary>
/// The reader of <c>faden.yaml</c> (untrusted input) keeps the limits of core's <c>safe_yaml.py</c>: 32 KiB, 8 levels, no anchors, aliases
/// or tags, and reads plain scalars as PyYAML's safe loader does. The tables were checked against PyYAML's <c>safe_load</c>.
/// </summary>
public class FadenYamlTests
{
    private static string Show(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string s => "\"" + s + "\"",
        List<object?> list => "[" + string.Join(",", list.Select(Show)) + "]",
        Dictionary<string, object?> map => "{" + string.Join(",", map.Select(p => p.Key + ":" + Show(p.Value))) + "}",
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    [Theory]
    [InlineData("", "null")]
    [InlineData("# only a comment\n", "null")]
    [InlineData("a: 1", "{a:1}")]
    [InlineData("---\na: 1\n", "{a:1}")]
    [InlineData("title: Slide design\npriority: 60\n", "{title:\"Slide design\",priority:60}")]
    [InlineData("tags: [powerpoint, presentations, design]", "{tags:[\"powerpoint\",\"presentations\",\"design\"]}")]
    [InlineData("tags:\n  - one\n  - two\n", "{tags:[\"one\",\"two\"]}")]
    [InlineData("tags:\n- one\n- two\nnext: 1\n", "{tags:[\"one\",\"two\"],next:1}")]
    [InlineData("a:\n  b:\n    c: x\n  d: y\n", "{a:{b:{c:\"x\"},d:\"y\"}}")]
    [InlineData("list:\n  - name: a\n    n: 1\n  - name: b\n", "{list:[{name:\"a\",n:1},{name:\"b\"}]}")]
    [InlineData("a: 'it''s'\nb: \"tab\\there\"\n", "{a:\"it's\",b:\"tab\there\"}")]
    [InlineData("a: x # a comment\nb: \"has # inside\"\n", "{a:\"x\",b:\"has # inside\"}")]
    [InlineData("a: don't stop\n", "{a:\"don't stop\"}")]
    [InlineData("url: http://x.y/z\n", "{url:\"http://x.y/z\"}")]
    [InlineData("a: {x: 1, y: [2, 3]}\n", "{a:{x:1,y:[2,3]}}")]
    [InlineData("a: [one,\n  two]\n", "{a:[\"one\",\"two\"]}")]
    [InlineData("a: ~\nb: null\nc:\nd: Null\n", "{a:null,b:null,c:null,d:null}")]
    [InlineData("a: true\nb: False\nc: yes\nd: No\ne: on\nf: OFF\n", "{a:true,b:false,c:true,d:false,e:true,f:false}")]
    [InlineData("a: 12\nb: -3\nc: +4\nd: 0x1F\ne: 010\nf: 1_000\ng: 09\nh: 1.5\ni: \"12\"\n",
        "{a:12,b:-3,c:4,d:31,e:8,f:1000,g:\"09\",h:\"1.5\",i:\"12\"}")]
    [InlineData("a: 1\na: 2\n", "{a:2}")]
    [InlineData("\"quoted key\": 1\n", "{quoted key:1}")]
    [InlineData("a: x\r\nb: y\r\n", "{a:\"x\",b:\"y\"}")]
    public void It_reads(string text, string expected) => Assert.Equal(expected, Show(FadenYaml.Parse(text)));

    [Theory]
    [InlineData("a: &x [1]\nb: *x\n", "anchors")]
    [InlineData("a: &x 1\n", "anchors")]
    [InlineData("b: *x\n", "aliases")]
    [InlineData("a: !!python/object:os.system x\n", "tags")]
    [InlineData("a: !tag x\n", "tags")]
    [InlineData("a: |\n  text\n", "block scalars")]
    [InlineData("a: >\n  text\n", "block scalars")]
    [InlineData("a: 1\n---\nb: 2\n", "more than one document")]
    [InlineData("a: 1\n...\n", "more than one document")]
    [InlineData("a:\n\tb: 1\n", "tab")]
    [InlineData("a: [1, 2\n", "bracket")]
    [InlineData("a: \"open\n", "quoted")]
    [InlineData("a: \"x\" y\n", "quoted")]
    [InlineData("a: b: c\n", "map")]
    [InlineData("? complex\n: 1\n", "complex keys")]
    [InlineData("a: 1\n b: 2\n", "laid out")]
    [InlineData("a: 1\nb\n", "laid out")]
    [InlineData("a: x\u200by\n", "invisible")]
    [InlineData("a: x\u202ey\n", "invisible")]
    [InlineData("a: x\u0007y\n", "invisible")]
    [InlineData("a: \"x\\u0007y\"\n", "invisible")]
    [InlineData("a: \"\\q\"\n", "escape")]
    public void It_refuses(string text, string why)
    {
        var e = Assert.Throws<FadenYamlException>(() => FadenYaml.Parse(text));
        Assert.Contains(why, e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_refuses_text_over_32_KiB_and_reads_text_at_the_limit()
    {
        var atLimit = "a: " + new string('x', FadenYaml.MaxBytes - 4) + "\n";
        Assert.Equal(FadenYaml.MaxBytes, Encoding.UTF8.GetByteCount(atLimit));
        Assert.IsType<Dictionary<string, object?>>(FadenYaml.Parse(atLimit));
        var over = "a: " + new string('x', FadenYaml.MaxBytes) + "\n";
        Assert.Contains("32 KiB", Assert.Throws<FadenYamlException>(() => FadenYaml.Parse(over)).Message);
        // bytes, not characters: 3-byte characters
        var wide = "a: " + new string('\u20ac', (FadenYaml.MaxBytes / 3) + 1) + "\n";
        Assert.Throws<FadenYamlException>(() => FadenYaml.Parse(wide));
    }

    [Fact]
    public void Nesting_counts_every_node_as_a_level_and_stops_after_eight()
    {
        // PyYAML's loader counts maps, lists and scalars alike: a map of a map of ... with a scalar at the bottom.
        string Nest(int maps) => string.Concat(Enumerable.Range(0, maps).Select(i => new string(' ', 2 * i) + "k:" + (i == maps - 1 ? " v\n" : "\n")));
        Assert.NotNull(FadenYaml.Parse(Nest(7)));          // 7 maps and the scalar: 8 levels
        var e = Assert.Throws<FadenYamlException>(() => FadenYaml.Parse(Nest(8)));
        Assert.Contains("8 levels", e.Message);
        Assert.NotNull(FadenYaml.Parse("a: " + new string('[', 6) + new string(']', 6) + "\n"));    // map + 6 lists: 7, the empty innermost is a list
        Assert.NotNull(FadenYaml.Parse("a: " + new string('[', 7) + new string(']', 7) + "\n"));    // PyYAML reads this too
        Assert.Throws<FadenYamlException>(() => FadenYaml.Parse("a: " + new string('[', 8) + new string(']', 8) + "\n"));
        Assert.Throws<FadenYamlException>(() => FadenYaml.Parse("a: " + new string('[', 200) + new string(']', 200) + "\n"));
        Assert.Throws<FadenYamlException>(() => FadenYaml.Parse(string.Concat(Enumerable.Range(0, 200).Select(i => new string(' ', 2 * i) + "- \n"))));
    }

    [Fact]
    public void A_wide_document_is_bounded_by_its_size_because_nothing_can_be_referenced()
    {
        // The "billion laughs" shape needs aliases; without them the biggest list a 32 KiB file makes is its own size.
        var many = "a: [" + string.Join(",", Enumerable.Repeat("x", 10000)) + "]\n";
        var list = Assert.IsType<List<object?>>(((Dictionary<string, object?>)FadenYaml.Parse(many)!)["a"]);
        Assert.Equal(10000, list.Count);
    }
}
