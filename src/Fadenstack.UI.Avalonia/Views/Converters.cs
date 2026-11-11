using System.Globalization;
using Avalonia.Data.Converters;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>Counts as booleans: <c>IsVisible="{Binding Items.Count, Converter={x:Static v:Counts.Any}}"</c>.</summary>
public static class Counts
{
    public static IValueConverter Any { get; } = new FuncValueConverter<int, bool>(count => count > 0);

    public static IValueConverter None { get; } = new FuncValueConverter<int, bool>(count => count == 0);
}

/// <summary>A tool's class as a badge's tone: <see cref="IsRisky"/> for one that cannot be undone or is not classified.</summary>
public static class ToolClasses
{
    public static IValueConverter IsRisky { get; } = new FuncValueConverter<ToolClass, bool>(c => c is ToolClass.Destructive or ToolClass.Unknown);
}

/// <summary>A notice's severity as the error banner: blocked and error both are.</summary>
public static class Notices
{
    public static IValueConverter IsError { get; } = new FuncValueConverter<NoticeSeverity, bool>(s => s is NoticeSeverity.Blocked or NoticeSeverity.Error);
}

/// <summary>True when every value is the same object: <c>&lt;MultiBinding Converter="{x:Static v:Same.Instance}"&gt;</c> (an item and the selected one).</summary>
public sealed class Same : IMultiValueConverter
{
    public static Same Instance { get; } = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count > 0 && values.All(v => ReferenceEquals(v, values[0]));
}
