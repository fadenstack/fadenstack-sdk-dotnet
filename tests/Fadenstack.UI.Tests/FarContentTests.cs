using Fadenstack.UI.Markdown;
using Fadenstack.UI.Parts;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>A skin lets go of an answer's parsed blocks when its turn is far from the viewport.</summary>
public class FarContentTests
{
    private const string Source = "# Title\n\nA paragraph with **bold**.\n\n```csharp\nvar x = 1;\n```\n";

    /// <summary>Blocks hold their inline runs in lists, so two parses of one text are equal in kind and text, not by reference.</summary>
    private static string Describe(MarkdownBlock block) => block is CodeBlock code ? $"code:{code.Code}" : block.GetType().Name;

    [Fact]
    public void Unloading_drops_the_blocks_and_keeps_the_text_and_loading_brings_the_same_blocks_back()
    {
        var document = new MarkdownDocument(Source);
        var before = document.Blocks.Select(Describe).ToList();

        document.Unload();

        Assert.False(document.IsLoaded);
        Assert.Empty(document.Blocks);
        Assert.Equal(Source, document.Text);

        document.Load();

        Assert.True(document.IsLoaded);
        Assert.Equal(before, document.Blocks.Select(Describe));
        Assert.Equal(Source, document.Text);
    }

    [Fact]
    public void Appending_to_an_unloaded_document_loads_it_first()
    {
        var document = new MarkdownDocument("First paragraph.\n\n");
        document.Unload();

        document.Append("Second paragraph.");

        Assert.True(document.IsLoaded);
        Assert.Equal(2, document.Blocks.Count);
        Assert.Equal("First paragraph.\n\nSecond paragraph.", document.Text);
    }

    [Fact]
    public void A_streaming_answer_keeps_its_blocks()
    {
        var part = new MarkdownPart(Source, isStreaming: true);

        part.Unload();

        Assert.True(part.IsLoaded);
        Assert.NotEmpty(part.Blocks);
    }

    [Fact]
    public void A_finished_turn_unloads_and_loads_its_answers_and_a_running_turn_keeps_them()
    {
        var finished = new TurnViewModel(null, "q", DateTimeOffset.UtcNow, 100, TurnState.Completed);
        finished.AddRestoredText(Source);
        var running = new TurnViewModel(null, "q", DateTimeOffset.UtcNow, 100);
        running.AddRestoredText(Source);

        finished.Unload();
        running.Unload();

        Assert.False(((MarkdownPart)finished.Parts.Single()).IsLoaded);
        Assert.True(((MarkdownPart)running.Parts.Single()).IsLoaded);
        Assert.Equal(Source, ((MarkdownPart)finished.Parts.Single()).Text);

        finished.Load();

        Assert.NotEmpty(((MarkdownPart)finished.Parts.Single()).Blocks);
    }
}
