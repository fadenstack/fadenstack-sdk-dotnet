using System.ComponentModel;

namespace Fadenstack.UI.Tools;

/// <summary>
/// A Tools view model that tells its texts (as the skill editor's do): a skin reads every text from <see cref="Strings"/> and says
/// them again when it changes (the app's language changed, <c>Retext</c>).
/// </summary>
public interface IToolsTexts : INotifyPropertyChanged
{
    /// <summary>The texts the view model was given; the current UI language's by default.</summary>
    UiStrings Strings { get; }
}

/// <summary>
/// What the Tools view needs from the app that a view model cannot do itself: choose a folder or a file. The skin implements it with
/// the system's dialogs; a host without any answers null (the sheet then leaves the field as it was).
/// </summary>
public interface IToolsFileHost
{
    /// <summary>The folder the person chose; null when they cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? start, CancellationToken cancellationToken);

    /// <summary>The file the person chose; null when they cancelled.</summary>
    Task<string?> PickFileAsync(string title, string? start, CancellationToken cancellationToken);
}
