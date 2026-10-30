using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Controls;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Themes;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// An audit, not a test: draws every control style of the skin (Themes/Controls.axaml, design 22 section 5) in every state,
/// light, dark and high contrast and with each named accent, at 2x, into the folder named by FADEN_GALLERY_OUT. States are
/// forced pseudo-classes (hover, pressed, open), real properties (checked, selected, disabled) and real keyboard focus
/// (the focus ring is an adorner, so one control per row window is focused with Tab). One PNG per theme and accent.
/// </summary>
public class ControlGalleryRenders
{
    private static readonly string? Out = Environment.GetEnvironmentVariable("FADEN_GALLERY_OUT");

    private const double Scale = 2;

    /// <summary>One cell: a state's name and a control in that state.</summary>
    private sealed record Cell(string State, Func<Control> Make, Action<Control>? After = null, bool Focus = false);

    private sealed record Row(string Name, IReadOnlyList<Cell> Cells);

    private static void Pseudo(Control c, string pseudo) => ((IPseudoClasses)c.Classes).Set(pseudo, true);

    private static Action<Control> Part<T>(string name, string pseudo) where T : Control => c =>
    {
        foreach (var part in c.GetVisualDescendants().OfType<T>().Where(p => p.Name == name))
        {
            Pseudo(part, pseudo);
        }
    };

    private static FsIcon Icon(string name) => new() { Data = (Geometry)Application.Current!.FindResource("Icon." + name)! };

    private static T With<T>(T control, params string[] classes) where T : Control
    {
        foreach (var c in classes)
        {
            control.Classes.Add(c);
        }
        return control;
    }

    private static T State<T>(T control, params string[] pseudo) where T : Control
    {
        foreach (var p in pseudo)
        {
            Pseudo(control, p);
        }
        return control;
    }

    private static Button IconButton(string icon, params string[] classes) => With(new Button { Content = Icon(icon) }, classes.Prepend("icon").ToArray());

    private static IEnumerable<Cell> ButtonStates(Func<Control> make, bool toggle = false)
    {
        yield return new Cell("normal", make);
        yield return new Cell("hover", () => State(make(), ":pointerover"));
        yield return new Cell("pressed", () => State(make(), ":pointerover", ":pressed"));
        yield return new Cell("focus", make, Focus: true);
        if (toggle)
        {
            yield return new Cell("checked", () => Check(make()));
            yield return new Cell("checked hover", () => State(Check(make()), ":pointerover"));
        }
        yield return new Cell("disabled", () => Disable(make()));
        if (toggle)
        {
            yield return new Cell("checked disabled", () => Disable(Check(make())));
        }
    }

    private static Control Check(Control c)
    {
        if (c is ToggleButton t)
        {
            t.IsChecked = true;
        }
        return c;
    }

    private static Control Disable(Control c)
    {
        c.IsEnabled = false;
        return c;
    }

    private static MenuFlyoutPresenter Menu(bool hoverFirst = false)
    {
        var presenter = new MenuFlyoutPresenter();
        MenuItem Item(string header, string? icon = null, string? gesture = null) => new()
        {
            Header = header,
            Icon = icon is null ? null : Icon(icon),
            InputGesture = gesture is null ? null : KeyGesture.Parse(gesture),
        };
        var open = Item("Open", "OpenInFull");
        presenter.Items.Add(open);
        presenter.Items.Add(Item("New chat", "Add", "Ctrl+N"));
        presenter.Items.Add(Item("Hide to the tray", "Remove"));
        presenter.Items.Add(new Separator());
        presenter.Items.Add(new MenuItem { Header = "Calm", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true });
        presenter.Items.Add(Item("Settings...", "SettingsOutlined", "Ctrl+,"));
        var more = Item("More", "MoreVert");
        more.Items.Add(new MenuItem { Header = "Sub item" });
        presenter.Items.Add(more);
        presenter.Items.Add(new MenuItem { Header = "Not now", IsEnabled = false });
        presenter.Items.Add(new Separator());
        presenter.Items.Add(Item("Quit"));
        if (hoverFirst)
        {
            open.IsSelected = true;
        }
        return presenter;
    }

    private static Control Segmented(Action<RadioButton[]>? change = null)
    {
        var items = new[] { "System", "Light", "Dark" }.Select(t => With(new RadioButton { Content = t, GroupName = Guid.NewGuid().ToString() }, "segment")).ToArray();
        var group = Guid.NewGuid().ToString();
        foreach (var i in items)
        {
            i.GroupName = group;
        }
        items[1].IsChecked = true;
        change?.Invoke(items);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var i in items)
        {
            panel.Children.Add(i);
        }
        return With(new Border { Child = panel }, "segmented");
    }

    private static Control Chips()
    {
        var removable = With(new Border
        {
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    Icon("Description"),
                    new TextBlock { Text = "Report.docx", VerticalAlignment = VerticalAlignment.Center },
                    IconButton("Close", "small"),
                },
            },
        }, "chip", "removable");
        var plain = With(new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon("AutoAwesome"), new TextBlock { Text = "Summarise", VerticalAlignment = VerticalAlignment.Center } } },
        }, "chip");
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { plain, removable } };
    }

    private static Control Badges() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            With(new Border { Child = new TextBlock { Text = "Read" } }, "badge", "read"),
            With(new Border { Child = new TextBlock { Text = "Write" } }, "badge", "write"),
            With(new Border { Child = new TextBlock { Text = "Destructive" } }, "badge", "danger"),
            With(new Border { Child = new TextBlock { Text = "3" } }, "count"),
        },
    };

    private static Control Banner(string kind, string icon, string title) => With(new Border
    {
        Width = 360,
        Child = new DockPanel
        {
            Children =
            {
                Dock(With(Icon(icon), "status"), global::Avalonia.Controls.Dock.Left, new Thickness(0, 0, 12, 0)),
                Dock(With(new Button { Content = "Try again" }, "quiet"), global::Avalonia.Controls.Dock.Right, new Thickness(8, -8, -8, -8)),
                new StackPanel
                {
                    Children =
                    {
                        With(new TextBlock { Text = title }, "strong"),
                        new TextBlock { Text = "The server did not answer in time.", TextWrapping = TextWrapping.Wrap },
                    },
                },
            },
        },
    }, "banner", kind);

    private static T Dock<T>(T c, global::Avalonia.Controls.Dock dock, Thickness margin) where T : Control
    {
        DockPanel.SetDock(c, dock);
        c.Margin = margin;
        return c;
    }

    private static Control Card() => With(new Border
    {
        Width = 300,
        Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                With(new TextBlock { Text = "Run add_comment?" }, "subtitle"),
                new TextBlock { Text = "The agent wants to add a comment to page 2.", TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { With(new Button { Content = "Deny" }, "secondary"), With(new Button { Content = "Allow" }, "primary") },
                },
            },
        },
    }, "card");

    private static Control Sheet() => With(new Border
    {
        Width = 320,
        Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                With(new TextBlock { Text = "Install the plugin?" }, "subtitle"),
                With(new TextBlock { Text = "Publisher: Example Ltd" }, "muted"),
                With(new Button { Content = "Install", HorizontalAlignment = HorizontalAlignment.Right }, "primary"),
            },
        },
    }, "sheet");

    private static Control MenuBorder() => With(new Border
    {
        Width = 220,
        Child = new StackPanel
        {
            Children =
            {
                With(new Button { Content = "Ask before changes" }, "menu-item", "selected"),
                State(With(new Button { Content = "Act without asking" }, "menu-item"), ":pointerover"),
                With(new Button { Content = "No tools" }, "menu-item"),
            },
        },
    }, "menu");

    private static Control Scroller(Action<ScrollBar>? bar = null)
    {
        var lines = new StackPanel();
        for (var i = 0; i < 20; i++)
        {
            lines.Children.Add(new TextBlock { Text = $"Line {i + 1} of a list that scrolls", Margin = new Thickness(8, 2) });
        }
        var viewer = new ScrollViewer { Width = 200, Height = 96, Content = lines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        viewer.Offset = new Vector(0, 120);
        return new Border { Child = viewer, Tag = bar };
    }

    private static Control List(Action<ListBox>? change = null)
    {
        var list = new ListBox { Width = 220 };
        foreach (var t in new[] { "Inbox", "Drafts", "Archive", "Not available" })
        {
            list.Items.Add(new ListBoxItem { Content = t });
        }
        ((ListBoxItem)list.Items[3]!).IsEnabled = false;
        list.SelectedIndex = 1;
        change?.Invoke(list);
        return list;
    }

    private static Control TypeRamp() => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            With(new TextBlock { Text = "Display 24/700" }, "display"),
            With(new TextBlock { Text = "Title 20/600" }, "title"),
            With(new TextBlock { Text = "Subtitle 16/600" }, "subtitle"),
            With(new TextBlock { Text = "Body 14/400 - the quick brown fox" }, "body"),
            With(new TextBlock { Text = "Caption 12 muted" }, "caption", "muted"),
            With(new TextBlock { Text = "MICRO 11/600" }, "micro"),
            With(new TextBlock { Text = "mono: var x = 1;" }, "mono"),
        },
    };

    private static IReadOnlyList<Row> Rows() => new List<Row>
    {
        new("IconButton (normal 32)", ButtonStates(() => IconButton("Add")).ToList()),
        new("IconButton.large (36)", ButtonStates(() => IconButton("History", "large")).ToList()),
        new("IconButton.small (28)", ButtonStates(() => IconButton("ContentCopy", "small")).ToList()),
        new("IconButton.toggle", ButtonStates(() => With(new ToggleButton { Content = Icon("PushPinOutlined") }, "icon"), toggle: true).ToList()),
        new("IconButton.primary (large)", ButtonStates(() => IconButton("Send", "large", "primary")).ToList()),
        new("Button.primary", ButtonStates(() => With(new Button { Content = "Allow" }, "primary")).ToList()),
        new("Button.secondary", ButtonStates(() => With(new Button { Content = "Deny" }, "secondary")).ToList()),
        new("Button.quiet", ButtonStates(() => With(new Button { Content = "Show details" }, "quiet")).ToList()),
        new("SplitButton.primary", new List<Cell>
        {
            new("normal", () => SplitButton()),
            new("hover", () => SplitButton(), Part<Button>("PART_PrimaryButton", ":pointerover")),
            new("hover arrow", () => SplitButton(), Part<Button>("PART_SecondaryButton", ":pointerover")),
            new("pressed", () => SplitButton(), c => { Part<Button>("PART_PrimaryButton", ":pointerover")(c); Part<Button>("PART_PrimaryButton", ":pressed")(c); }),
            new("focus", () => SplitButton(), Focus: true),
            // The pseudo-class after the template: the split button sets it from its flyout when it applies one.
            new("open", () => new StackPanel { Spacing = 0, Children = { SplitButton(), new Border { Margin = new Thickness(-12, -2, 0, 0), Child = Menu(hoverFirst: true) } } },
                c => Pseudo(c.GetVisualDescendants().OfType<SplitButton>().First(), ":flyout-open")),
            new("disabled", () => Disable(SplitButton())),
        }),
        new("Pill (toggle)", ButtonStates(() => Pill(), toggle: true).ToList()),
        new("TextBox", new List<Cell>
        {
            new("placeholder", () => Box()),
            new("hover", () => State(Box(), ":pointerover")),
            new("focus", () => Box(), Focus: true),
            new("filled", () => Box("Quarterly report")),
            new("error", () => Error(Box("not an e-mail"))),
            new("disabled", () => Disable(Box("Set by your IT"))),
        }),
        new("ComboBox", new List<Cell>
        {
            new("normal", () => Combo()),
            new("hover", () => State(Combo(), ":pointerover")),
            new("pressed", () => State(Combo(), ":pointerover", ":pressed")),
            new("focus", () => Combo(), Focus: true),
            new("open", () => new StackPanel { Children = { State(Combo(), ":dropdownopen"), ComboList() } }),
            new("disabled", () => Disable(Combo())),
        }),
        new("ToggleSwitch", new List<Cell>
        {
            new("off", () => Switch(false)),
            new("off hover", () => State(Switch(false), ":pointerover")),
            new("on", () => Switch(true)),
            new("on hover", () => State(Switch(true), ":pointerover")),
            new("focus", () => Switch(true), Focus: true),
            new("disabled off", () => Disable(Switch(false))),
            new("disabled on", () => Disable(Switch(true))),
            new("with label", () => new ToggleSwitch { Content = "Calm", IsChecked = true }),
        }),
        new("Segmented", new List<Cell>
        {
            new("normal", () => Segmented()),
            new("hover", () => Segmented(i => Pseudo(i[2], ":pointerover"))),
            new("pressed", () => Segmented(i => { Pseudo(i[2], ":pointerover"); Pseudo(i[2], ":pressed"); })),
            new("focus", () => Segmented(), c => { }, Focus: true),
            new("disabled", () => Disable(Segmented())),
        }),
        new("Slider", new List<Cell>
        {
            new("normal", () => Slide()),
            new("hover", () => State(Slide(), ":pointerover")),
            new("pressed", () => State(Slide(), ":pointerover", ":pressed")),
            new("focus", () => Slide(), Focus: true),
            new("disabled", () => Disable(Slide())),
        }),
        new("CheckBox", ToggleStates(() => new CheckBox { Content = "Remember me" }, threeState: true).ToList()),
        new("RadioButton", ToggleStates(() => new RadioButton { Content = "Right side", GroupName = Guid.NewGuid().ToString() }, threeState: false).ToList()),
        new("Menu", new List<Cell>
        {
            new("open", () => Menu()),
            new("item hover", () => Menu(hoverFirst: true)),
            new("menu drawn by a view", MenuBorder),
        }),
        new("ToolTip", new List<Cell>
        {
            new("open", () => State(new ToolTip { Content = "New chat (Ctrl+N)" }, ":open")),
            new("long", () => State(new ToolTip { Content = "Answers are generated by AI and can be wrong. Check what matters." }, ":open")),
        }),
        new("ScrollViewer", new List<Cell>
        {
            new("idle", () => Scroller()),
            new("scrolling", () => Scroller(), c => ForBars(c, b => Pseudo(b, ":scrolling"))),
            new("hover (wide)", () => Scroller(), c => ForBars(c, b => typeof(ScrollBar).GetProperty("IsExpanded")!.SetValue(b, true))),
        }),
        new("ListBox", new List<Cell>
        {
            new("selected", () => List()),
            new("hover", () => List(l => Pseudo((ListBoxItem)l.Items[0]!, ":pointerover"))),
            new("selected hover", () => List(l => Pseudo((ListBoxItem)l.Items[1]!, ":pointerover"))),
            new("focus", () => List(), c => { }, Focus: true),
        }),
        new("Card, Sheet", new List<Cell> { new("card", Card), new("sheet", Sheet) }),
        new("Banner", new List<Cell>
        {
            new("info", () => Banner("info", "InfoOutlined", "A new version is ready")),
            new("warning", () => Banner("warning", "WarningAmber", "Plugins are off")),
            new("error", () => Banner("error", "ErrorOutline", "Something went wrong")),
            new("success", () => Banner("success", "Check", "Signed in")),
        }),
        new("Chip, Badge", new List<Cell> { new("chips", Chips), new("chip remove hover", Chips, c => { foreach (var b in c.GetVisualDescendants().OfType<Button>()) Pseudo(b, ":pointerover"); }), new("badges", Badges) }),
        new("Type", new List<Cell> { new("ramp", TypeRamp) }),
    };

    private static void ForBars(Control c, Action<ScrollBar> act)
    {
        foreach (var bar in c.GetVisualDescendants().OfType<ScrollBar>().Where(b => b.IsVisible))
        {
            act(bar);
        }
    }

    private static IEnumerable<Cell> ToggleStates(Func<ToggleButton> make, bool threeState)
    {
        yield return new Cell("unchecked", make);
        yield return new Cell("hover", () => State(make(), ":pointerover"));
        yield return new Cell("pressed", () => State(make(), ":pointerover", ":pressed"));
        yield return new Cell("checked", () => Check(make()));
        yield return new Cell("checked hover", () => State(Check(make()), ":pointerover"));
        if (threeState)
        {
            yield return new Cell("indeterminate", () => { var c = make(); c.IsThreeState = true; c.IsChecked = null; return c; });
        }
        yield return new Cell("focus", () => Check(make()), Focus: true);
        yield return new Cell("disabled", () => Disable(make()));
        yield return new Cell("checked disabled", () => Disable(Check(make())));
    }

    private static SplitButton SplitButton()
    {
        var split = With(new SplitButton { Content = "Allow" }, "primary");
        split.Flyout = new MenuFlyout();
        return split;
    }

    private static ToggleButton Pill() => With(new ToggleButton
    {
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { Icon("Shield"), new TextBlock { Text = "Ask", VerticalAlignment = VerticalAlignment.Center }, Icon("KeyboardArrowDown") },
        },
    }, "pill");

    private static TextBox Box(string? text = null) => new() { Width = 220, PlaceholderText = "Search or run a command", Text = text };

    private static Control Error(TextBox box)
    {
        DataValidationErrors.SetErrors(box, new object[] { "Enter an e-mail address." });
        return box;
    }

    private static ComboBox Combo()
    {
        var combo = new ComboBox { Width = 200, PlaceholderText = "Choose a language" };
        foreach (var l in new[] { "English", "Deutsch", "Español", "Français", "中文" })
        {
            combo.Items.Add(new ComboBoxItem { Content = l });
        }
        combo.SelectedIndex = 1;
        return combo;
    }

    private static Control ComboList()
    {
        var items = new StackPanel();
        var names = new[] { "English", "Deutsch", "Español" };
        for (var i = 0; i < names.Length; i++)
        {
            var item = new ComboBoxItem { Content = names[i], IsSelected = i == 1 };
            if (i == 2)
            {
                Pseudo(item, ":pointerover");
            }
            items.Children.Add(item);
        }
        return With(new Border { Width = 200, Margin = new Thickness(0, 4, 0, 0), Child = items }, "menu");
    }

    private static ToggleSwitch Switch(bool on) => new() { IsChecked = on };

    private static Slider Slide() => new() { Width = 200, Minimum = 90, Maximum = 140, Value = 110 };

    /// <summary>One row in a window of its own: the row's name, then each state's name over its control.</summary>
    private static async Task<WriteableBitmap> RenderRow(Row row)
    {
        var grid = new Grid { Margin = new Thickness(16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(170)));
        var title = With(new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 18, 0, 0) }, "caption", "strong");
        grid.Children.Add(title);
        var made = new List<(Control Control, Cell Cell)>();
        for (var i = 0; i < row.Cells.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var control = row.Cells[i].Make();
            made.Add((control, row.Cells[i]));
            var cell = new StackPanel
            {
                Margin = new Thickness(0, 0, 20, 0),
                Spacing = 8,
                Children =
                {
                    With(new TextBlock { Text = row.Cells[i].State }, "caption", "muted"),
                    new Border { Padding = new Thickness(6), Child = control, HorizontalAlignment = HorizontalAlignment.Left },
                },
            };
            Grid.SetColumn(cell, i + 1);
            grid.Children.Add(cell);
        }
        var root = new Border { Child = grid };
        root.Bind(Border.BackgroundProperty, root.GetResourceObservable(FadenstackKeys.Background));
        root.Bind(global::Avalonia.Controls.Documents.TextElement.ForegroundProperty, root.GetResourceObservable(FadenstackKeys.Foreground));
        var window = new Window { Content = root, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        window.SetRenderScaling(Scale);
        await Pump(4);
        foreach (var (control, cell) in made)
        {
            cell.After?.Invoke(control);
            if (control is Border { Tag: Action<ScrollBar> bar })
            {
                ForBars(control, bar);
            }
        }
        var focus = made.FirstOrDefault(m => m.Cell.Focus).Control;
        if (focus is not null)
        {
            var target = focus is Border { Classes: var cls } b && cls.Contains("segmented")
                ? b.GetVisualDescendants().OfType<RadioButton>().First(r => r.IsChecked == true)
                : focus is ListBox list ? (Control)list.ContainerFromIndex(1)! : focus;
            target.Focus(NavigationMethod.Tab);
        }
        await Pump(10);
        var frame = window.CaptureRenderedFrame()!;
        window.Close();
        return frame;
    }

    private static async Task Pump(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(4);
        }
    }

    /// <summary>The rows stacked into one picture, on the theme's background.</summary>
    private static void SaveSheet(IReadOnlyList<WriteableBitmap> rows, string path)
    {
        var width = rows.Max(r => r.PixelSize.Width);
        var height = rows.Sum(r => r.PixelSize.Height);
        using var first = rows[0].Lock();
        var sheet = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * Scale, 96 * Scale), first.Format, AlphaFormat.Premul);
        using (var target = sheet.Lock())
        {
            var fill = new byte[target.RowBytes];
            var top = 0;
            foreach (var row in rows)
            {
                using var source = row == rows[0] ? null : row.Lock();
                var src = source ?? first;
                var data = new byte[src.RowBytes];
                // The background colour: the row's top-left pixel, to fill the rest of the line.
                Marshal.Copy(src.Address, data, 0, 4);
                for (var x = 0; x < width; x++)
                {
                    Buffer.BlockCopy(data, 0, fill, x * 4, 4);
                }
                for (var y = 0; y < src.Size.Height; y++)
                {
                    Marshal.Copy(src.Address + y * src.RowBytes, data, 0, src.RowBytes);
                    Marshal.Copy(fill, 0, target.Address + (top + y) * target.RowBytes, width * 4);
                    Marshal.Copy(data, 0, target.Address + (top + y) * target.RowBytes, src.Size.Width * 4);
                }
                top += src.Size.Height;
            }
        }
        sheet.Save(path, new PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public async Task Render_every_control_in_every_state()
    {
        if (string.IsNullOrEmpty(Out))
        {
            return;
        }
        Directory.CreateDirectory(Out);
        FadenstackControls.EnsureLoaded();
        var app = Application.Current!;
        try
        {
            var sheets = new List<(string Name, ThemeVariant Variant, ThemeChoices Choices)>();
            foreach (var accent in Enum.GetValues<ThemeAccent>())
            {
                sheets.Add(($"{accent.ToString().ToLowerInvariant()}-light", ThemeVariant.Light, new ThemeChoices { AccentName = accent }));
                sheets.Add(($"{accent.ToString().ToLowerInvariant()}-dark", ThemeVariant.Dark, new ThemeChoices { AccentName = accent }));
            }
            sheets.Add(("highcontrast", ThemeVariant.Dark, new ThemeChoices { HighContrast = true }));
            foreach (var (name, variant, choices) in sheets)
            {
                app.RequestedThemeVariant = variant;
                FadenstackTheme.Apply(app, choices);
                var rows = new List<WriteableBitmap>();
                foreach (var row in Rows())
                {
                    rows.Add(await RenderRow(row));
                }
                SaveSheet(rows, Path.Combine(Out, $"controls-{name}.png"));
                // The first sheet's rows also one by one, for a close look.
                if (name == "frost-light" || name == "frost-dark")
                {
                    var i = 0;
                    foreach (var row in rows)
                    {
                        row.Save(Path.Combine(Out, $"row-{name}-{i++:00}.png"), new PngBitmapEncoderOptions());
                    }
                }
            }
        }
        finally
        {
            FadenstackTheme.Apply(app, new ThemeChoices());
            app.RequestedThemeVariant = ThemeVariant.Light;
        }
    }
}
