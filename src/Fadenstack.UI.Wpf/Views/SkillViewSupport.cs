using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using Fadenstack.UI.Wpf.Behaviors;

namespace Fadenstack.UI.Wpf.Views;

/// <summary>What the three skill views share: the palette of the theme, the texts' language and direction, and the first focus.</summary>
internal static class SkillViewSupport
{
    /// <summary>Puts the theme's colours first among the view's merged resources.</summary>
    public static void ApplyTheme(UserControl view, ChatTheme theme)
    {
        var colors = new ResourceDictionary { Source = ChatView.PaletteUri(theme) };
        var merged = view.Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = colors;
        }
        else
        {
            merged.Insert(0, colors);
        }
    }

    /// <summary>The texts for everything in the view (<see cref="ChatText"/>), with their language and direction.</summary>
    public static void ApplyStrings(UserControl view, UiStrings? strings)
    {
        ChatText.SetStrings(view, strings);
        if (strings is null)
        {
            view.ClearValue(FrameworkElement.LanguageProperty);
            view.ClearValue(FrameworkElement.FlowDirectionProperty);
        }
        else
        {
            view.Language = XmlLanguage.GetLanguage(strings.Language);
            view.FlowDirection = strings.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
    }

    /// <summary>When the view is first shown, the focus goes to its first field (or button).</summary>
    public static void FocusFirstWhenLoaded(UserControl view)
    {
        view.Loaded += (_, _) => view.Dispatcher.BeginInvoke(new Action(() => WizardFocus.FocusFirst(view)), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Calls <paramref name="shown"/> when the view is loaded while visible, and each time it becomes visible again: a control that
    /// reads something that changes meanwhile (the "Your data" control's record of tool calls) asks its view model to read it again.
    /// View-only glue, so the code-behind gets no member of its own. TS: <c>whenShown</c> (ui-web).
    /// </summary>
    public static void WhenShown(FrameworkElement view, Action shown)
    {
        view.Loaded += (_, _) =>
        {
            if (view.IsVisible)
            {
                shown();
            }
        };
        view.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && view.IsLoaded)
            {
                shown();
            }
        };
    }
}
