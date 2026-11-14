using System.Globalization;
using Avalonia.Data.Converters;
using Fadenstack.UI.Skills;

namespace Fadenstack.UI.Avalonia.Behaviors;

/// <summary>
/// The findings of the editor's checks that are about one field, for the line under that field:
/// <c>ItemsSource="{Binding Issues, Converter={x:Static b:SkillIssuesFor.Name}}"</c>. The list of all findings stays whole under the checks.
/// </summary>
public sealed class SkillIssuesFor : IValueConverter
{
    private readonly string _subject;

    private SkillIssuesFor(string subject) => _subject = subject;

    public static SkillIssuesFor Name { get; } = new("name");

    public static SkillIssuesFor Description { get; } = new("description");

    public static SkillIssuesFor Instructions { get; } = new("instructions");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as IEnumerable<SkillIssueViewModel> ?? Array.Empty<SkillIssueViewModel>())
            .Where(i => string.Equals(i.Subject, _subject, StringComparison.Ordinal)).ToList();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
