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
    public void Every_button_gets_a_style_of_its_own_type_so_the_cards_load()
    {
        // A style is for one type: a Button's on a ToggleButton throws when the template loads (the cards' time pickers did).
        var chat = Load(Path.Combine("Themes", "Chat.xaml"));
        var types = chat.Descendants(Wpf + "Style").Where(s => s.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is not null)
            .ToDictionary(s => (string)s.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))!, s => (string?)s.Attribute("TargetType"));
        var styled = chat.Descendants().Concat(Load(Path.Combine("Views", "ChatView.xaml")).Descendants())
            .Where(e => e.Name == Wpf + "Button" || e.Name == Wpf + "ToggleButton")
            .Select(e => (Element: e.Name.LocalName, Style: Attr(e, "Style")))
            .Where(e => e.Style is { } s && s.StartsWith("{StaticResource ", StringComparison.Ordinal))
            .Select(e => (e.Element, Target: types.TryGetValue(e.Style!.Substring(16).TrimEnd('}'), out var t) ? t : null))
            .ToList();

        Assert.NotEmpty(styled);
        Assert.DoesNotContain(styled, s => s.Element == "ToggleButton" && s.Target == "Button");
        Assert.DoesNotContain(styled, s => s.Element == "Button" && s.Target == "ToggleButton");
    }

    [Fact]
    public void The_sign_in_panel_has_the_code_and_a_secondary_open_again_button_that_enter_does_not_press()
    {
        var template = BypassTemplate();
        var panel = template.Descendants(Wpf + "StackPanel").Single(p => (string?)p.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "SignIn");
        Assert.Contains("IsSigningIn", Attr(panel, "Visibility"));
        Assert.Contains(panel.Descendants(Wpf + "TextBlock"), t => Attr(t, "Text") == "{Binding SignInCodeText}");
        var open = panel.Descendants(Wpf + "Button").Single();

        Assert.Equal("{Binding OpenAgainCommand}", Attr(open, "Command"));
        Assert.Equal("{StaticResource Fadenstack.SecondaryButton}", Attr(open, "Style")); // a Button's style, not the toggle's
        Assert.Equal("{Binding OpenAgainName}", Attr(open, "AutomationProperties.Name"));
        Assert.Equal("{Binding OpenAgainText}", Attr(open, "Content"));
        Assert.NotEqual("True", Attr(open, "IsDefault"));
        Assert.NotEqual("True", Attr(open, "IsCancel"));
        Assert.Equal("False", Attr(open, "KeyboardNavigation.AcceptsReturn"));
        Assert.Contains("{Binding StateText}", template.Descendants(Wpf + "TextBlock").Select(t => Attr(t, "Text"))); // the first line of it
    }

    [Fact]
    public void Switch_on_again_and_the_chat_lists_bypass_text_are_bound_with_names_for_screen_readers_and_no_hard_coded_text()
    {
        var view = Load(Path.Combine("Views", "ChatView.xaml"));
        var again = view.Descendants(Wpf + "Button").Single(b => Attr(b, "Command") == "{Binding Grants.SwitchOnAgainCommand}");

        Assert.Equal("{Binding Grants.SwitchOnAgainText}", Attr(again, "Content"));
        Assert.Equal("{Binding Grants.SwitchOnAgainName}", Attr(again, "AutomationProperties.Name"));
        Assert.Equal("{StaticResource Fadenstack.SecondaryButton}", Attr(again, "Style"));
        Assert.NotEqual("True", Attr(again, "IsDefault"));
        Assert.NotEqual("True", Attr(again, "IsCancel"));
        var listed = view.Descendants(Wpf + "TextBlock").Single(t => Attr(t, "Text") == "{Binding BypassText}");
        Assert.Equal("{Binding BypassText}", Attr(listed, "AutomationProperties.Name"));
        Assert.Contains("HasBypass", Attr(listed, "Visibility"));
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
