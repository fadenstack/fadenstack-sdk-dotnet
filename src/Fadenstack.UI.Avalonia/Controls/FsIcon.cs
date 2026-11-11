using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Fadenstack.UI.Avalonia.Controls;

/// <summary>
/// Draws one icon of <c>Themes/Icons.axaml</c> (design 22, section 4). It scales the 24x24 design box of the icon set, not
/// the path's bounds, to <see cref="Size"/>, centres that box in its own bounds and fills the geometry with
/// <see cref="Foreground"/>, so every icon sits at the same place whatever its ink. Use it as
/// <c>&lt;c:FsIcon Data="{DynamicResource Icon.Send}" /&gt;</c>.
/// </summary>
public class FsIcon : Control
{
    /// <summary>The edge of the icon set's design box, in the set's own units.</summary>
    public const double DesignSize = 24;

    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<FsIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<FsIcon, double>(nameof(Size), 18.0);

    /// <summary>Inherited like <c>TextElement.Foreground</c>, so an icon in a button follows the button's foreground.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<FsIcon>();

    static FsIcon()
    {
        AffectsMeasure<FsIcon>(SizeProperty);
        AffectsRender<FsIcon>(DataProperty, SizeProperty, ForegroundProperty);
    }

    /// <summary>The icon, a geometry drawn in the 24x24 design box (<c>Icon.*</c> resources).</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>The edge of the square the design box is scaled to, in device-independent pixels (16, 18, 20 or 22).</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = EffectiveSize;
        return new Size(size, size);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Data is not { } data || Foreground is not { } brush)
        {
            return;
        }

        var size = EffectiveSize;
        var origin = BoxOrigin(size);
        var scale = size / DesignSize;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin.X, origin.Y)))
        {
            context.DrawGeometry(brush, null, data);
        }
    }

    private double EffectiveSize => double.IsNaN(Size) || Size < 0 ? 0 : Size;

    /// <summary>
    /// The top left of the design box: centred in the bounds, then moved to a whole device pixel (measured from the
    /// window, so a fractional position of the parent does not blur the edges).
    /// </summary>
    private Point BoxOrigin(double size)
    {
        var centred = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        var root = TopLevel.GetTopLevel(this);
        var scaling = root?.RenderScaling ?? 1.0;
        if (root is not null && this.TranslatePoint(centred, root) is { } inRoot)
        {
            var snapped = new Point(Math.Round(inRoot.X * scaling) / scaling, Math.Round(inRoot.Y * scaling) / scaling);
            return new Point(centred.X + (snapped.X - inRoot.X), centred.Y + (snapped.Y - inRoot.Y));
        }
        return centred;
    }
}
