using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace Fadenstack.UI.Avalonia.Behaviors;

/// <summary>
/// Shows an overlay scroll bar only while its content scrolls (design 22, section 5.3: hidden when not scrolling, never a
/// permanent bar). The skin's <c>Controls.axaml</c> switches it on for every <see cref="ScrollBar"/>; it sets the
/// <c>:scrolling</c> pseudo-class while the value changes and for <see cref="Hold"/> after, and the bar's style fades the
/// thumb in and out on it.
/// </summary>
public static class ScrollActivity
{
    /// <summary>How long the bar stays after the last scroll.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.2);

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollBar, bool>("IsEnabled", typeof(ScrollActivity));

    private static readonly AttachedProperty<DispatcherTimer?> TimerProperty =
        AvaloniaProperty.RegisterAttached<ScrollBar, DispatcherTimer?>("Timer", typeof(ScrollActivity));

    static ScrollActivity()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollBar>((bar, e) =>
        {
            bar.PropertyChanged -= OnBarChanged;
            if (e.NewValue is true)
            {
                bar.PropertyChanged += OnBarChanged;
            }
            else
            {
                Settle(bar);
            }
        });
    }

    public static bool GetIsEnabled(ScrollBar bar) => bar.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollBar bar, bool value) => bar.SetValue(IsEnabledProperty, value);

    /// <summary>Marks the bar as scrolling now; it settles <see cref="Hold"/> after the last call.</summary>
    public static void Touch(ScrollBar bar)
    {
        ((IPseudoClasses)bar.Classes).Set(":scrolling", true);
        var timer = bar.GetValue(TimerProperty);
        if (timer is null)
        {
            timer = new DispatcherTimer { Interval = Hold };
            timer.Tick += (_, _) => Settle(bar);
            bar.SetValue(TimerProperty, timer);
        }
        timer.Stop();
        timer.Start();
    }

    private static void Settle(ScrollBar bar)
    {
        bar.GetValue(TimerProperty)?.Stop();
        ((IPseudoClasses)bar.Classes).Set(":scrolling", false);
    }

    private static void OnBarChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RangeBase.ValueProperty && sender is ScrollBar { IsEffectivelyVisible: true } bar)
        {
            Touch(bar);
        }
    }
}
