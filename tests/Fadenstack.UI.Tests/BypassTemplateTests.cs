using System.Xml.Linq;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// The WPF skin's Bypass confirmation is deliberate (sdk/09 L-58; sdk/40 A3.3): its switch is never the default button,
/// Enter on it does nothing and Esc never answers it, "Not now" comes first, and no key of the panel reaches it. Read as
/// XML from Themes/Chat.xaml and Views/ChatView.xaml (copied next to the tests), as these tests do not load WPF.
/// </summary>
public class BypassTemplateTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument Load(string path) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, path));

    private static XElement BypassTemplate() => Load(Path.Combine("Themes", "Chat.xaml")).Descendants(Wpf + "DataTemplate")
        .Single(t => (string?)t.Attribute("DataType") == "{x:Type parts:BypassPart}");

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

    [Fact]
    public void The_switch_is_never_the_default_button_nor_the_cancel_one_and_enter_on_it_does_nothing()
    {
        var buttons = BypassTemplate().Descendants(Wpf + "Button").ToList();
        var allow = buttons.Single(b => Attr(b, "Command") == "{Binding AllowCommand}");

        Assert.NotEqual("True", Attr(allow, "IsDefault"));
        Assert.NotEqual("True", Attr(allow, "IsCancel"));
        Assert.Equal("False", Attr(allow, "KeyboardNavigation.AcceptsReturn"));
        Assert.DoesNotContain("PrimaryButton", Attr(allow, "Style") ?? "");
        Assert.DoesNotContain(buttons, b => Attr(b, "IsDefault") == "True" || Attr(b, "IsCancel") == "True");
    }

    [Fact]
    public void Not_now_comes_first_and_the_reason_box_takes_no_enter()
    {
        var template = BypassTemplate();
        var row = template.Descendants().Single(e => e.Name.LocalName == "ButtonRow");
        var first = row.Elements(Wpf + "Button").First();
        Assert.Equal("{Binding NotNowCommand}", Attr(first, "Command"));
        var reason = template.Descendants(Wpf + "TextBox").Single();
        Assert.Equal("False", Attr(reason, "AcceptsReturn"));
    }

    [Fact]
    public void No_key_of_the_panel_answers_the_confirmation()
    {
        var bindings = Load(Path.Combine("Views", "ChatView.xaml")).Descendants(Wpf + "KeyBinding").ToList();

        Assert.NotEmpty(bindings);
        Assert.DoesNotContain(bindings, k => (Attr(k, "Command") ?? "").Contains("Bypass") || (Attr(k, "Command") ?? "").Contains("Allow"));
        // Esc stops the turn (or closes About); Enter sends from the composer.
        Assert.All(bindings.Where(k => Attr(k, "Key") is "Escape" or "Enter"),
            k => Assert.Contains(Attr(k, "Command"), new[] { "{Binding StopCommand}", "{Binding CloseAboutCommand}", "{Binding SendCommand}" }));
    }
}
