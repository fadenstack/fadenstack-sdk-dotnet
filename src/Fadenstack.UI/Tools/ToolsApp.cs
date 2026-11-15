using System.Text.RegularExpressions;
using Fadenstack.LocalTools;
using Fadenstack.Tools;

namespace Fadenstack.UI.Tools;

/// <summary>
/// A tool the app itself gives the assistant, as "In this app" lists it: its name, a chip with its class and its description as plain
/// text (white space collapsed, cut to <see cref="MaxDescription"/> characters). Read only.
/// </summary>
public sealed class AppToolViewModel
{
    /// <summary>The longest description a row shows, with the ellipsis a cut one ends in.</summary>
    public const int MaxDescription = 300;

    private static readonly Regex Space = new Regex(@"\s+", RegexOptions.Compiled);

    internal AppToolViewModel(UiStrings strings, AppToolInfo tool)
    {
        Name = tool.Name;
        Description = Plain(tool.Description);
        ClassLabel = strings.ApprovalClassLabel(tool.Class);
        IsRead = tool.Class == ToolClass.Read;
        IsWrite = tool.Class == ToolClass.Write;
        IsDestructive = tool.Class == ToolClass.Destructive;
        IsUnmarked = !IsRead && !IsWrite && !IsDestructive;
    }

    public string Name { get; }

    /// <summary>The description as one plain line of text; empty when the tool has none.</summary>
    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    /// <summary>The class chip's text ("Read", "Write", ...).</summary>
    public string ClassLabel { get; }

    /// <summary>Reads only (a success chip).</summary>
    public bool IsRead { get; }

    /// <summary>Changes something that can be changed back (an outlined chip).</summary>
    public bool IsWrite { get; }

    /// <summary>Deletes, sends, pays or cannot be undone (a warning chip).</summary>
    public bool IsDestructive { get; }

    /// <summary>Not marked: every call asks (a muted chip).</summary>
    public bool IsUnmarked { get; }

    /// <summary>True when <paramref name="filter"/> (any case) is in the tool's name.</summary>
    internal bool Matches(string filter) => ToolsViewModel.Contains(Name, filter);

    /// <summary>Collapses every run of white space to one space and cuts to <see cref="MaxDescription"/> characters, ending in an ellipsis.</summary>
    internal static string Plain(string? text)
    {
        var plain = Space.Replace(text ?? "", " ").Trim();
        if (plain.Length <= MaxDescription)
        {
            return plain;
        }
        var cut = MaxDescription - 1;
        if (char.IsHighSurrogate(plain[cut - 1]))
        {
            cut--; // never half of a surrogate pair
        }
        return plain.Substring(0, cut).TrimEnd() + "…";
    }
}
