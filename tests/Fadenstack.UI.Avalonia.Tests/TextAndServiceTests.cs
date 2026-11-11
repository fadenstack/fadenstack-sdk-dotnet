using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Sample;
using Fadenstack.UI.Avalonia.Services;

namespace Fadenstack.UI.Avalonia.Tests;

public class TextAndServiceTests
{
    private static readonly string[] Languages = { "en", "de", "fr", "es", "zh-Hans" };

    [Fact]
    public void Every_text_the_views_name_exists_in_all_five_languages()
    {
        var names = Sources().SelectMany(s => Regex.Matches(s, @"\{v:Text (\w+)\}").Select(m => m.Groups[1].Value)).Distinct().ToList();

        Assert.NotEmpty(names);
        foreach (var language in Languages)
        {
            var strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
            foreach (var name in names)
            {
                Assert.True(strings.Find(name) is not null, $"'{name}' is missing in {language}");
            }
        }
    }

    [Fact]
    public void The_views_have_no_hard_coded_ui_text()
    {
        // A literal text attribute: Text="Word", Content="Word", ToolTip.Tip="Word", PlaceholderText="Word", AutomationProperties.Name="Word".
        var literal = new Regex(@"\b(Text|Content|ToolTip\.Tip|PlaceholderText|AutomationProperties\.Name|Header)=""[A-Za-z]");
        foreach (var source in Sources())
        {
            Assert.DoesNotMatch(literal, source);
        }
    }

    [AvaloniaFact]
    public async Task The_texts_follow_the_language_the_user_picks()
    {
        using var host = await ChatHost.OpenAsync(2);
        var placeholder = host.View.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "Composer");
        Assert.Equal(UiStrings.For(CultureInfo.GetCultureInfo("en")).ComposerPlaceholder, placeholder.PlaceholderText);

        await host.Chat.SetLanguageAsync("de");
        await host.PumpAsync();

        Assert.Equal(UiStrings.For(CultureInfo.GetCultureInfo("de")).ComposerPlaceholder, placeholder.PlaceholderText);
        Assert.Contains(host.View.GetVisualDescendants().OfType<Button>(), b => ToolTip.GetTip(b) as string == UiStrings.For(CultureInfo.GetCultureInfo("de")).NewChatShortcut);
    }

    [AvaloniaFact]
    public async Task The_dispatcher_runs_the_batches_below_everything_else()
    {
        var order = new List<string>();
        new AvaloniaUiDispatcher().Post(() => order.Add("batch"));
        Dispatcher.UIThread.Post(() => order.Add("render-ish"), DispatcherPriority.Render);
        Dispatcher.UIThread.Post(() => order.Add("input"), DispatcherPriority.Input);
        Dispatcher.UIThread.Post(() => order.Add("default"), DispatcherPriority.Default);

        Dispatcher.UIThread.RunJobs();
        await Task.Yield();

        Assert.Equal("batch", order[^1]);
        Assert.True(new AvaloniaUiDispatcher().CheckAccess());
    }

    [AvaloniaFact]
    public async Task A_picture_is_decoded_off_the_ui_thread_at_the_size_it_is_drawn_and_let_go_of_when_cleared()
    {
        var image = new Image { Width = 100, Height = 60 };
        var window = new Window { Content = image };
        window.Show();

        PictureSource.SetWidth(image, 100);
        PictureSource.SetData(image, SyntheticSession.Picture);
        for (var i = 0; i < 400 && image.Source is null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        var bitmap = Assert.IsAssignableFrom<Bitmap>(image.Source);
        Assert.InRange(bitmap.PixelSize.Width, 1, 100 * (int)window.RenderScaling + 1);

        PictureSource.SetData(image, null);

        Assert.Null(image.Source);
        window.Close();
    }

    /// <summary>The views' sources, read from the repository (the texts test needs the XAML as text).</summary>
    private static IEnumerable<string> Sources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fadenstack.slnx")))
        {
            root = root.Parent;
        }
        return Directory.GetFiles(Path.Combine(root!.FullName, "src", "Fadenstack.UI.Avalonia", "Views"), "*.axaml").Select(File.ReadAllText);
    }
}
