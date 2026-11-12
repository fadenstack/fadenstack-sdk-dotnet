namespace Fadenstack.UI.ViewModels;

/// <summary>How a grant's place is shown in a short line.</summary>
internal static class PlaceNames
{
    /// <summary>
    /// A file's or folder's name, else the identifier as given. A grant's places are strings made on any device, so both
    /// <c>/</c> and <c>\</c> separate, whatever this computer's own separator is (<see cref="System.IO.Path.GetFileName(string)"/>
    /// on Linux and macOS would keep <c>C:\Docs\report.docx</c> whole).
    /// </summary>
    public static string Short(string identifier)
    {
        var trimmed = identifier.TrimEnd('/', '\\');
        var name = trimmed.Substring(trimmed.LastIndexOfAny(['/', '\\']) + 1);
        return string.IsNullOrEmpty(name) ? identifier : name;
    }
}
