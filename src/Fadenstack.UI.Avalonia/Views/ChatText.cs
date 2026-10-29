using Avalonia;
using Avalonia.Markup.Xaml;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// The chat's texts for the skin: <see cref="ChatView"/> sets <c>ChatText.Strings</c> from its view model, and every
/// element inside it (templates and menus too) inherits them. A host that uses the templates in a layout of its own
/// sets it on its root.
/// </summary>
public static class ChatText
{
    public static readonly AttachedProperty<UiStrings?> StringsProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, UiStrings?>("Strings", typeof(ChatText), null, inherits: true);

    /// <summary>For an element outside a panel: the current UI language's texts.</summary>
    internal static UiStrings Fallback { get; } = UiStrings.Current;

    public static UiStrings? GetStrings(AvaloniaObject element) => element.GetValue(StringsProperty);

    public static void SetStrings(AvaloniaObject element, UiStrings? value) => element.SetValue(StringsProperty, value);
}

/// <summary>
/// A chat text by its name in the resources (<c>UiText.resx</c>): <c>Content="{v:Text Allow}"</c>. It follows the
/// panel's <c>ChatText.Strings</c>, so the skin has no text of its own.
/// </summary>
public sealed class TextExtension : MarkupExtension
{
    public TextExtension()
    {
    }

    public TextExtension(string name) => Name = name;

    public string Name { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetObject: AvaloniaObject target })
        {
            return new NamedText(target.GetObservable(ChatText.StringsProperty), Name).ToBinding();
        }
        return ChatText.Fallback[Name];
    }

    /// <summary>The text of <c>name</c> in whichever strings the element inherits, again when they change.</summary>
    private sealed class NamedText : IObservable<string>
    {
        private readonly IObservable<UiStrings?> _strings;
        private readonly string _name;

        public NamedText(IObservable<UiStrings?> strings, string name)
        {
            _strings = strings;
            _name = name;
        }

        public IDisposable Subscribe(IObserver<string> observer) => _strings.Subscribe(new Forward(observer, _name));

        private sealed class Forward : IObserver<UiStrings?>
        {
            private readonly IObserver<string> _to;
            private readonly string _name;

            public Forward(IObserver<string> to, string name)
            {
                _to = to;
                _name = name;
            }

            public void OnNext(UiStrings? value) => _to.OnNext((value ?? ChatText.Fallback)[_name]);

            public void OnError(Exception error) => _to.OnError(error);

            public void OnCompleted() => _to.OnCompleted();
        }
    }
}
