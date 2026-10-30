using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Fadenstack.UI.Avalonia.Controls;

/// <summary>
/// The keyboard focus ring (design 22, section 5.4): a 2 px line in <c>Fadenstack.Focus</c> at 2 px outside the focused
/// control, its corners following the control's (a round button gets a round ring, a 12 px button a 16 px ring). The skin's
/// <c>Controls.axaml</c> makes it every control's focus adorner, so it shows for keyboard focus only (<c>:focus-visible</c>).
/// </summary>
public class FocusRing : Border
{
    /// <summary>The gap between the control's edge and the ring.</summary>
    public static readonly StyledProperty<double> OffsetProperty = AvaloniaProperty.Register<FocusRing, double>(nameof(Offset), 2.0);

    static FocusRing()
    {
        AdornerLayer.AdornedElementProperty.Changed.AddClassHandler<FocusRing>((ring, _) => ring.Follow());
        OffsetProperty.Changed.AddClassHandler<FocusRing>((ring, _) => ring.Follow());
        BorderThicknessProperty.Changed.AddClassHandler<FocusRing>((ring, _) => ring.Follow());
    }

    public FocusRing()
    {
        IsHitTestVisible = false;
        Focusable = false;
        // The adorner layer clips an adorner to the focused control's bounds; the ring lies outside them by design.
        AdornerLayer.SetIsClipEnabled(this, false);
    }

    public double Offset
    {
        get => GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Follow();
    }

    /// <summary>The corner radius of the control the ring surrounds: a templated control's or a border's, else none.</summary>
    internal static CornerRadius RadiusOf(Visual? target) => target switch
    {
        TemplatedControl t => t.CornerRadius,
        Border b => b.CornerRadius,
        _ => default,
    };

    private void Follow()
    {
        var grow = Offset + BorderThickness.Left;
        Margin = new Thickness(-grow);
        var r = RadiusOf(AdornerLayer.GetAdornedElement(this));
        CornerRadius = new CornerRadius(r.TopLeft + grow, r.TopRight + grow, r.BottomRight + grow, r.BottomLeft + grow);
    }
}
