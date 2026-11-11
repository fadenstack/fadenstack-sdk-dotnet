using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.Markdown;

namespace Fadenstack.UI.Avalonia.Tests;

public class MarkdownViewTests
{
    [AvaloniaFact]
    public void A_streaming_paragraph_is_updated_in_place_and_finished_blocks_are_never_touched()
    {
        var document = new MarkdownDocument("First paragraph.\n\nSecond");
        var view = new MarkdownView { Blocks = document.Blocks };
        var first = view.Children[0];
        var live = view.Children[1];

        document.Append(" paragraph grows");
        document.Append(" and grows.\n\nThird");

        Assert.Equal(3, view.Children.Count);
        Assert.Same(first, view.Children[0]);
        Assert.Same(live, view.Children[1]);
        Assert.Contains("grows", string.Concat(((TextBlock)live).Inlines!.OfType<Run>().Select(r => r.Text)));
    }

    [AvaloniaFact]
    public void A_growing_code_block_lays_out_only_its_last_chunk_again()
    {
        var lines = Enumerable.Range(0, MarkdownView.LinesPerChunk * 3).Select(i => $"var line{i} = {i};").ToList();
        var document = new MarkdownDocument("```csharp\n" + string.Join("\n", lines.Take(MarkdownView.LinesPerChunk * 2 + 1)) + "\n");
        var view = new MarkdownView { Blocks = document.Blocks };
        var code = view.Children[0];
        var chunks = ((StackPanel)((ScrollViewer)((DockPanel)((Border)code).Child!).Children[1]).Content!).Children.Cast<TextBlock>().ToList();
        var texts = chunks.Select(c => c.Text).ToList();

        document.Append(string.Join("\n", lines.Skip(MarkdownView.LinesPerChunk * 2 + 1)) + "\n```\n");

        var after = ((StackPanel)((ScrollViewer)((DockPanel)((Border)view.Children[0]).Child!).Children[1]).Content!).Children.Cast<TextBlock>().ToList();
        Assert.Same(code, view.Children[0]);
        Assert.Equal(3, after.Count);
        Assert.Same(chunks[0], after[0]);
        Assert.Same(chunks[1], after[1]);
        Assert.Equal(texts[0], after[0].Text);
        Assert.Equal(texts[1], after[1].Text);
        Assert.Contains("line" + (MarkdownView.LinesPerChunk * 3 - 1), after[2].Text);
    }

    [AvaloniaFact]
    public void Every_block_kind_has_a_control_and_a_changed_kind_is_replaced()
    {
        var text = new StringBuilder("# H\n\nP\n\n- a\n- b\n\n> q\n\n| A |\n|---|\n| 1 |\n\n---\n\n```\nx\n```\n");
        var document = new MarkdownDocument(text.ToString());

        var view = new MarkdownView { Blocks = document.Blocks };

        Assert.Equal(document.Blocks.Count, view.Children.Count);
        Assert.All(view.Children, c => Assert.NotNull(c.Tag));
        document.Unload();
        Assert.Empty(view.Children);
        document.Load();
        Assert.Equal(document.Blocks.Count, view.Children.Count);
    }
}
