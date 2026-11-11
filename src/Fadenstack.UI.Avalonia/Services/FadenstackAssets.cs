using Avalonia;
using Avalonia.Markup.Xaml.Styling;

namespace Fadenstack.UI.Avalonia.Services;

/// <summary>Puts the skin's font-family keys (<c>Typography.axaml</c>) and icons (<c>Icons.axaml</c>) on the application.</summary>
public static class FadenstackAssets
{
    private const string FontKey = "Fadenstack.FontFamily";

    /// <summary>
    /// Merges <c>Typography.axaml</c> and <c>Icons.axaml</c> into the application's resources once, unless a host has
    /// already defined the keys. <c>ChatView</c> calls it; a host that shows <c>FsIcon</c> without a chat view calls it
    /// itself.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (Application.Current is not { } app || app.Resources.TryGetResource(FontKey, null, out _))
        {
            return;
        }
        var baseUri = new Uri("avares://Fadenstack.UI.Avalonia/");
        app.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://Fadenstack.UI.Avalonia/Themes/Typography.axaml") });
        app.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://Fadenstack.UI.Avalonia/Themes/Icons.axaml") });
    }
}
