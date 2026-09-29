using System.Collections.ObjectModel;
using System.Text;

namespace Faden.UI.Markdown;

/// <summary>
/// A streamed answer as blocks. <see cref="Append"/> re-parses only the last block -- the one still growing --
/// and replaces it in <see cref="Blocks"/>; every block before it is final. So the cost of an update is the
/// size of the last block, not of the answer, and a skin re-renders one item.
/// </summary>
public sealed class MarkdownDocument
{
    private readonly StringBuilder _text = new();
    private int _liveStart;
    private bool _hasLive;

    public MarkdownDocument(string? text = null)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Append(text!);
        }
    }

    public ObservableCollection<MarkdownBlock> Blocks { get; } = new();

    /// <summary>The Markdown source so far.</summary>
    public string Text => _text.ToString();

    public int Length => _text.Length;

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _text.Append(text);
        var tail = _text.ToString(_liveStart, _text.Length - _liveStart);
        var parsed = MarkdownParser.ParseWithOffsets(tail);
        if (parsed.Count == 0)
        {
            return; // only blank lines so far
        }
        for (var k = 0; k < parsed.Count; k++)
        {
            if (k == 0 && _hasLive)
            {
                if (!Same(Blocks[Blocks.Count - 1], parsed[0].Block))
                {
                    Blocks[Blocks.Count - 1] = parsed[0].Block;
                }
            }
            else
            {
                Blocks.Add(parsed[k].Block);
            }
        }
        _liveStart += parsed[parsed.Count - 1].Offset;
        _hasLive = true;
    }

    /// <summary>Structural equality for the live block: skip the replace when an update changed nothing visible.</summary>
    private static bool Same(MarkdownBlock a, MarkdownBlock b) => a switch
    {
        ParagraphBlock p when b is ParagraphBlock q => p.Inlines.SequenceEqual(q.Inlines),
        HeadingBlock h when b is HeadingBlock g => h.Level == g.Level && h.Inlines.SequenceEqual(g.Inlines),
        CodeBlock c when b is CodeBlock d => c == d,
        _ => false,
    };
}
