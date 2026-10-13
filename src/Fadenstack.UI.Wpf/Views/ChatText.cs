using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>
/// The chat's texts for the skin: <see cref="ChatView"/> sets <c>ChatText.Strings</c> from its view model, and every
/// element inside it (templates and menus too) inherits them. A host that uses the templates in a layout of its own
/// sets it on its root.
/// </summary>
public static class ChatText
{
    public static readonly DependencyProperty StringsProperty = DependencyProperty.RegisterAttached(
        "Strings", typeof(UiStrings), typeof(ChatText), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>For an element outside a panel: the current UI language's texts.</summary>
    internal static UiStrings Fallback { get; } = UiStrings.Current;

    public static UiStrings? GetStrings(DependencyObject element) => (UiStrings?)element.GetValue(StringsProperty);

    public static void SetStrings(DependencyObject element, UiStrings? value) => element.SetValue(StringsProperty, value);
}

/// <summary>
/// A chat text by its name in the resources (<c>UiText.resx</c>): <c>Content="{v:Text Allow}"</c>. It follows the
/// panel's <c>ChatText.Strings</c>, so the skin has no text of its own.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension : MarkupExtension
{
    public TextExtension()
    {
    }

    public TextExtension(string name) => Name = name;

    [ConstructorArgument("name")]
    public string Name { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding
        {
            Path = new PropertyPath($"(0)[{Name}]", ChatText.StringsProperty),
            RelativeSource = RelativeSource.Self,
            Mode = BindingMode.OneWay,
            FallbackValue = ChatText.Fallback[Name],
        };
        return binding.ProvideValue(serviceProvider);
    }
}
