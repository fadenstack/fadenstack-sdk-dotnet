namespace Fadenstack.Http;

/// <summary>
/// <c>X-Faden-Provenance</c>: the generation receipts of a whole answer, comma-separated -- its text's first when it
/// has text, then those of the pictures it carries, in their order.
/// </summary>
public static class ProvenanceHeader
{
    public const string Name = "X-Faden-Provenance";

    /// <summary>The receipt ids in a header's value; none for null or empty.</summary>
    public static IReadOnlyList<string> Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value!.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
}
