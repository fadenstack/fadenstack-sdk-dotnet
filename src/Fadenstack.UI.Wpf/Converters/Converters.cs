using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Fadenstack.UI.Wpf.Converters;

/// <summary>True → Collapsed (and the reverse of BooleanToVisibilityConverter).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Collapsed;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Null, empty text or an empty count → Collapsed.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    /// <summary>Collapsed for null, "" or 0; with <c>ConverterParameter=invert</c>, visible only for those.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is null || (value is string s && s.Length == 0) || (value is int n && n == 0);
        var invert = parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase);
        return empty != invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// A picture's <c>data:</c> URL → a small bitmap, for its chip (<c>ConverterParameter</c> = the pixels to decode it at,
/// default 64: sharp at 16 px on a 300 % screen, and little memory). Nothing for anything else.
/// </summary>
public sealed class DataUrlToImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var size = parameter is string p && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 64;
        return Services.Pictures.FromDataUrl(value as string, size) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A nesting level → a left indent (<c>ConverterParameter</c> = pixels per level, default 18).</summary>
public sealed class LevelToIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var step = parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 18;
        return new Thickness((value is int level ? level : 0) * step, 0, 0, 2);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
