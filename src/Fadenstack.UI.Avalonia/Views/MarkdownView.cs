using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Markdown;
using IconPath = Avalonia.Controls.Shapes.Path;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// An answer's Markdown blocks, one light control per block (a paragraph is one selectable text block, a code block a
/// monospace one per few lines, a list a row per item). It follows the document's block collection, and while an answer
/// streams only its last block is replaced: when the new block is of the same kind the control is updated in place, and a
/// code block re-lays out only the chunk that grew, so the cost of an update is the size of the last paragraph or chunk,
/// not of the answer and not of the control tree. <c>&lt;v:MarkdownView Blocks="{Binding Blocks}" CopyCommand="{Binding CopyCodeCommand}" /&gt;</c>
/// </summary>
public class MarkdownView : StackPanel
{
    /// <summary>\n    /// How many lines of a code block share one text block; the chunk that grows is the only one laid out again, and shaping\n    /// text costs about 2.5 microseconds a character, so a chunk is kept to some 800 characters. Code is plain text (the copy\n    /// button takes all of it): selectable text blocks lay out about twice as slowly.\n    /// </summary>
    internal const int LinesPerChunk = 8;

    public static readonly StyledProperty<IReadOnlyList<MarkdownBlock>?> BlocksProperty =
        AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MarkdownBlock>?>(nameof(Blocks));

    /// <summary>Copies a code block (its button); the parameter is the block.</summary>
    public static readonly StyledProperty<ICommand?> CopyCommandProperty =
        AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(CopyCommand));

    private INotifyCollectionChanged? _watching;

    public IReadOnlyList<MarkdownBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    public ICommand? CopyCommand
    {
        get => GetValue(CopyCommandProperty);
        set => SetValue(CopyCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlocksProperty)
        {
            if (_watching is not null)
            {
                _watching.CollectionChanged -= OnBlocksChanged;
            }
            _watching = change.NewValue as INotifyCollectionChanged;
            if (_watching is not null)
            {
                _watching.CollectionChanged += OnBlocksChanged;
            }
            Rebuild();
        }
        else if (change.Property == CopyCommandProperty)
        {
            foreach (var code in Children.OfType<CodeBlockView>())
            {
                code.Copy.Command = CopyCommand;
            }
        }
    }

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var blocks = Blocks;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex == Children.Count && blocks is not null:
                for (var i = 0; i < e.NewItems!.Count; i++)
                {
                    Children.Add(Create((MarkdownBlock)e.NewItems[i]!));
                }
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems!.Count == 1 && e.NewStartingIndex < Children.Count:
                var block = (MarkdownBlock)e.NewItems[0]!;
                if (!Update(Children[e.NewStartingIndex], block))
                {
                    Children[e.NewStartingIndex] = Create(block);
                }
                break;
            default:
                Rebuild();
                break;
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        if (Blocks is { } blocks)
        {
            foreach (var block in blocks)
            {
                Children.Add(Create(block));
            }
        }
    }

    private Control Create(MarkdownBlock block)
    {
        Control control = block switch
        {
            ParagraphBlock p => Text(p.Inlines, "md-paragraph", selectable: true),
            HeadingBlock h => Heading(h),
            CodeBlock c => CreateCode(c),
            ListBlock l => CreateList(l),
            QuoteBlock q => CreateQuote(q),
            TableBlock t => CreateTable(t),
            _ => new Border { Classes = { "rule" } },
        };
        control.Tag = block;
        return control;
    }

    /// <summary>Updates <paramref name="control"/> to <paramref name="block"/> when it is of the same kind; false when it must be replaced.</summary>
    private bool Update(Control control, MarkdownBlock block)
    {
        switch (block)
        {
            case ParagraphBlock p when control is TextBlock text && control.Tag is ParagraphBlock:
                MarkdownText.SetInlines(text, p.Inlines);
                break;
            case HeadingBlock h when control is TextBlock text && control.Tag is HeadingBlock old && old.Level == h.Level:
                MarkdownText.SetInlines(text, h.Inlines);
                break;
            case CodeBlock c when control is CodeBlockView code:
                code.Show(c);
                break;
            case ListBlock l when control is StackPanel rows && control.Tag is ListBlock:
                UpdateList(rows, l);
                break;
            default:
                return false;
        }
        control.Tag = block;
        return true;
    }

    private static TextBlock Text(IReadOnlyList<InlineRun> inlines, string @class, bool selectable)
    {
        TextBlock text = selectable ? new SelectableTextBlock() : new TextBlock();
        text.Classes.Add(@class);
        MarkdownText.SetInlines(text, inlines);
        return text;
    }

    private static TextBlock Heading(HeadingBlock heading)
    {
        var text = Text(heading.Inlines, "md-heading", selectable: false);
        text.FontSize = heading.Level switch { 1 => 20, 2 => 17, 3 => 15, _ => 14 };
        return text;
    }

    // -- code ----------------------------------------------------------------------------------------

    private Border CreateCode(CodeBlock block)
    {
        var view = new CodeBlockView(CopyCommand);
        view.Show(block);
        return view;
    }

    /// <summary>A code block: a header (the language, a copy button) over monospace text blocks of a few lines each.</summary>
    private sealed class CodeBlockView : Border
    {
        private readonly StackPanel _chunks = new();
        private readonly TextBlock _language = new() { Classes = { "muted" } };

        public CodeBlockView(ICommand? copy)
        {
            Copy = new Button { Classes = { "ghost" }, Command = copy, Content = new IconPath { Classes = { "icon", "muted", "copy" } } };
            DockPanel.SetDock(Copy, Dock.Right);
            var header = new DockPanel { Margin = new Thickness(12, 5, 5, 0), Children = { Copy, _language } };
            DockPanel.SetDock(header, Dock.Top);
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(12, 4, 12, 10),
                Content = _chunks,
            };
            Classes.Add("code");
            Margin = new Thickness(0, 2, 0, 12);
            Child = new DockPanel { Children = { header, scroll } };
        }

        public Button Copy { get; }

        public void Show(CodeBlock block)
        {
            Copy.CommandParameter = block;
            _language.Text = block.Language ?? "";
            var lines = block.Code.Split('\n');
            var chunks = (lines.Length + LinesPerChunk - 1) / LinesPerChunk;
            for (var i = 0; i < chunks; i++)
            {
                var text = string.Join("\n", lines.Skip(i * LinesPerChunk).Take(LinesPerChunk));
                if (i < _chunks.Children.Count)
                {
                    var existing = (TextBlock)_chunks.Children[i];
                    if (existing.Text != text)
                    {
                        existing.Text = text;
                    }
                }
                else
                {
                    _chunks.Children.Add(new TextBlock { Classes = { "mono" }, TextWrapping = TextWrapping.NoWrap, Text = text });
                }
            }
            while (_chunks.Children.Count > chunks)
            {
                _chunks.Children.RemoveAt(_chunks.Children.Count - 1);
            }
        }
    }
    // -- lists, quotes, tables -------------------------------------------------------------------------

    private static StackPanel CreateList(ListBlock list)
    {
        var rows = new StackPanel { Margin = new Thickness(4, 0, 0, 10) };
        UpdateList(rows, list);
        return rows;
    }

    private static void UpdateList(StackPanel rows, ListBlock list)
    {
        for (var i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            if (i < rows.Children.Count)
            {
                var row = (DockPanel)rows.Children[i];
                row.Margin = new Thickness(18 * item.Level, 0, 0, 0);
                ((TextBlock)row.Children[0]).Text = item.Marker;
                MarkdownText.SetInlines((TextBlock)row.Children[1], item.Inlines);
            }
            else
            {
                var marker = new TextBlock { Classes = { "muted" }, Text = item.Marker, MinWidth = 18, VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(marker, Dock.Left);
                rows.Children.Add(new DockPanel { Margin = new Thickness(18 * item.Level, 0, 0, 0), Children = { marker, Text(item.Inlines, "md-item", selectable: true) } });
            }
        }
        while (rows.Children.Count > list.Items.Count)
        {
            rows.Children.RemoveAt(rows.Children.Count - 1);
        }
    }

    private static Border CreateQuote(QuoteBlock quote) =>
        new() { Classes = { "quote" }, Child = Text(quote.Inlines, "md-quote", selectable: true) };

    private static Border CreateTable(TableBlock table)
    {
        var host = new ContentControl { Focusable = false };
        MarkdownTable.SetTable(host, table);
        return new Border { Classes = { "table" }, Child = host };
    }
}
