using System.Globalization;
using System.Resources;

namespace Fadenstack.Provenance;

/// <summary>
/// The words a content mark shows people, in one language: the visible label ("AI-generated") and the title on marked
/// HTML (the label, a space, the record's id). The module ships them in English, German, French, Spanish and Simplified
/// Chinese (<c>Resources/ProvenanceText*.resx</c>); a host gives its own words with the constructor, through
/// <see cref="ProvenanceOptions.UseTexts"/>. The module owns its texts: it does not need the UI layer for them.
/// </summary>
public sealed class ProvenanceTexts
{
    private static readonly ResourceManager Package =
        new("Fadenstack.Provenance.Resources.ProvenanceText", typeof(ProvenanceTexts).Assembly);

    /// <summary>A host's own words: the label people read on content made with AI.</summary>
    public ProvenanceTexts(string aiGenerated)
    {
        AiGenerated = string.IsNullOrWhiteSpace(aiGenerated)
            ? throw new ArgumentException("The label must not be empty.", nameof(aiGenerated))
            : aiGenerated;
    }

    /// <summary>"AI-generated": the visible label on content made with AI, where the organisation asks for one.</summary>
    public string AiGenerated { get; }

    /// <summary>
    /// The tooltip (<c>title</c>) on marked HTML: <see cref="AiGenerated"/>, a space, the record's id -- in every language,
    /// so readers find the id (the TypeScript SDK and the host agents write the same).
    /// </summary>
    public string AiGeneratedTitle(string recordId) => AiGenerated + " " + recordId;

    /// <summary>The languages the module ships.</summary>
    public static IReadOnlyList<string> BuiltInLanguages { get; } = new[] { "en", "de", "fr", "es", "zh-Hans" };

    /// <summary>The words in <paramref name="culture"/>'s language: de-AT finds German, a language the module lacks finds English.</summary>
    public static ProvenanceTexts For(CultureInfo culture) =>
        new(Package.GetString(nameof(AiGenerated), culture ?? throw new ArgumentNullException(nameof(culture)))!);

    /// <summary>
    /// The words in a language named by its tag (the user's language, e.g. <c>de</c>); the host's
    /// (<see cref="CultureInfo.CurrentUICulture"/>) when there is none or the system does not know it.
    /// </summary>
    public static ProvenanceTexts For(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return Current;
        }
        try
        {
            return For(CultureInfo.GetCultureInfo(language));
        }
        catch (CultureNotFoundException)
        {
            return Current;
        }
    }

    /// <summary>The words in the host's language (<see cref="CultureInfo.CurrentUICulture"/>).</summary>
    public static ProvenanceTexts Current => For(CultureInfo.CurrentUICulture);
}
