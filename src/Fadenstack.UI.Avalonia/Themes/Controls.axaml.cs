using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace Fadenstack.UI.Avalonia.Themes;

/// <summary>
/// The skin's control styles (design 22, section 5): icon buttons, text buttons, the split button, inputs, choices,
/// menus, tooltips, scroll bars, lists and the container classes, in every state. They sit on top of <c>FluentTheme</c>
/// and replace its looks; colours, radii and type come from the theme keys (<c>Colors.axaml</c>, <c>Typography.axaml</c>).
/// Add it after the Fluent theme, in the application's styles, so menus, tooltips and the focus ring follow it too:
/// <code>
/// &lt;Application.Styles&gt;
///   &lt;FluentTheme /&gt;
///   &lt;fs:FadenstackControls /&gt;
/// &lt;/Application.Styles&gt;
/// </code>
/// A <c>ChatView</c> adds it itself when the application has not (<see cref="EnsureLoaded"/>).
/// </summary>
public class FadenstackControls : Styles
{
    public FadenstackControls(IServiceProvider? serviceProvider = null)
    {
        AvaloniaXamlLoader.Load(serviceProvider, this);
    }

    /// <summary>
    /// Adds the control styles to the application's styles, after what is there (so after the Fluent theme), unless they
    /// are there already (as this class or a <c>StyleInclude</c> of <c>Controls.axaml</c>). Also puts the theme keys,
    /// fonts and icons on the application when it has none.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (Application.Current is not { } app)
        {
            return;
        }
        Services.FadenstackTheme.EnsureDefaults();
        Services.FadenstackAssets.EnsureLoaded();
        if (!Contains(app.Styles))
        {
            app.Styles.Add(new FadenstackControls());
        }
    }

    private static bool Contains(IEnumerable<IStyle> styles) => styles.Any(s => s switch
    {
        FadenstackControls => true,
        StyleInclude { Source: { } source } => source.AbsolutePath.EndsWith("/Themes/Controls.axaml", StringComparison.OrdinalIgnoreCase),
        Styles nested => Contains(nested),
        _ => false,
    });
}
