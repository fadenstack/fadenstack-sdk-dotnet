using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// AgentAvatar's template is in Themes/Generic.xaml, so it draws wherever it is used.
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

namespace Faden.UI.Wpf.Views;

/// <summary>
/// The Fadenstack agent ("the stack"): three plates, the middle one with eyes. Vector, so it is sharp at any size;
/// set <see cref="Small"/> at 32 px and below. The colours follow the theme (Faden.Avatar.* in Colors.*.xaml);
/// <see cref="ThreadBrush"/> and <see cref="ThreadSideBrush"/> recolour the middle plate for a custom agent.
/// </summary>
public class AgentAvatar : Control
{
    /// <summary>The small cut: bigger eyes, the lid raised, no loose thread end. For 32 px and below.</summary>
    public static readonly DependencyProperty SmallProperty = DependencyProperty.Register(
        nameof(Small), typeof(bool), typeof(AgentAvatar), new PropertyMetadata(false));

    /// <summary>The middle plate's top and the thread end; the theme's rose unless set.</summary>
    public static readonly DependencyProperty ThreadBrushProperty = DependencyProperty.Register(
        nameof(ThreadBrush), typeof(Brush), typeof(AgentAvatar), new PropertyMetadata(null));

    /// <summary>The middle plate's sides; the theme's darker rose unless set.</summary>
    public static readonly DependencyProperty ThreadSideBrushProperty = DependencyProperty.Register(
        nameof(ThreadSideBrush), typeof(Brush), typeof(AgentAvatar), new PropertyMetadata(null));

    static AgentAvatar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(AgentAvatar), new FrameworkPropertyMetadata(typeof(AgentAvatar)));
        FocusableProperty.OverrideMetadata(typeof(AgentAvatar), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(AgentAvatar), new FrameworkPropertyMetadata(false));
    }

    public bool Small
    {
        get => (bool)GetValue(SmallProperty);
        set => SetValue(SmallProperty, value);
    }

    public Brush? ThreadBrush
    {
        get => (Brush?)GetValue(ThreadBrushProperty);
        set => SetValue(ThreadBrushProperty, value);
    }

    public Brush? ThreadSideBrush
    {
        get => (Brush?)GetValue(ThreadSideBrushProperty);
        set => SetValue(ThreadSideBrushProperty, value);
    }
}
