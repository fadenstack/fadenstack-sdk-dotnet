using Avalonia.Data.Converters;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>Counts as booleans: <c>IsVisible="{Binding Items.Count, Converter={x:Static v:Counts.Any}}"</c>.</summary>
public static class Counts
{
    public static IValueConverter Any { get; } = new FuncValueConverter<int, bool>(count => count > 0);

    public static IValueConverter None { get; } = new FuncValueConverter<int, bool>(count => count == 0);
}
