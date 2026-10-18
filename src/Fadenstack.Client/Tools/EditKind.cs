namespace Fadenstack.Tools;

/// <summary>
/// What a tool makes of content a model wrote when it puts it somewhere people read it (contract
/// <c>provenance/v1</c>). New, rewritten and summarised content is marked as AI-generated; translations, corrections,
/// formatting and extraction are standard edits, recorded as exempt by default.
/// </summary>
public enum EditKind
{
    /// <summary>New content.</summary>
    Generated = 0,

    /// <summary>Changes style, structure or meaning.</summary>
    Rewritten,

    /// <summary>A summary of existing content.</summary>
    Summarised,

    /// <summary>A translation.</summary>
    Translated,

    /// <summary>Spelling, grammar, minor stylistic polish.</summary>
    Corrected,

    /// <summary>Layout, styles, format conversion.</summary>
    Formatted,

    /// <summary>Existing content selected or structured, not summarised.</summary>
    Extracted,
}

/// <summary>The wire names of <see cref="EditKind"/>, and which kinds are exempt unless a policy says otherwise.</summary>
public static class EditKinds
{
    /// <summary>The kinds recorded as exempt by default: translated, corrected, formatted, extracted.</summary>
    public static IReadOnlyList<EditKind> ExemptByDefault { get; } =
        new[] { EditKind.Translated, EditKind.Corrected, EditKind.Formatted, EditKind.Extracted };

    public static bool IsExemptByDefault(this EditKind kind) => ExemptByDefault.Contains(kind);

    public static string ToWire(this EditKind kind) => kind switch
    {
        EditKind.Rewritten => "rewritten",
        EditKind.Summarised => "summarised",
        EditKind.Translated => "translated",
        EditKind.Corrected => "corrected",
        EditKind.Formatted => "formatted",
        EditKind.Extracted => "extracted",
        _ => "generated",
    };

    /// <summary>A wire name, or null for anything else.</summary>
    public static EditKind? ParseEditKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "generated" => EditKind.Generated,
        "rewritten" => EditKind.Rewritten,
        "summarised" => EditKind.Summarised,
        "translated" => EditKind.Translated,
        "corrected" => EditKind.Corrected,
        "formatted" => EditKind.Formatted,
        "extracted" => EditKind.Extracted,
        _ => null,
    };
}
