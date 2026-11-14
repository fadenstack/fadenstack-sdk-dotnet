using System.ComponentModel;
using Fadenstack.UI.Skills;

namespace Fadenstack.UI.Avalonia.Views;

/// <summary>
/// What the three skill views share about their texts: they take them from their view model (<see cref="ISkillTexts.Strings"/>, as the wizard
/// does) and say them again when the view model says they changed; without a view model the texts are the current language's.
/// </summary>
internal sealed class SkillTextsFollower
{
    private readonly Action<UiStrings?> _apply;
    private ISkillTexts? _texts;

    public SkillTextsFollower(Action<UiStrings?> apply) => _apply = apply;

    /// <summary>The view's DataContext changed.</summary>
    public void Follow(object? dataContext)
    {
        if (_texts is not null)
        {
            _texts.PropertyChanged -= OnChanged;
        }
        _texts = dataContext as ISkillTexts;
        if (_texts is not null)
        {
            _texts.PropertyChanged += OnChanged;
        }
        _apply(_texts?.Strings);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ISkillTexts.Strings) or null or "")
        {
            _apply(_texts?.Strings);
        }
    }
}