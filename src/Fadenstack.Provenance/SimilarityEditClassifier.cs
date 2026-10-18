using System.Globalization;
using System.Text;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Provenance;

/// <summary>
/// Tells a light correction from a rewrite by comparing the text before and after: <c>corrected</c> (exempt) only when
/// most words stay, the sentences stay the same in number and the word count barely changes; <c>rewritten</c> (marked)
/// otherwise, and whenever in doubt. A tool calls <see cref="Suggest"/> ("improve the writing" returns
/// <c>corrected</c> for a light polish), or the module uses it as its classifier for placements whose kind is
/// <c>rewritten</c> or <c>corrected</c> and that say what they started from (<see cref="MarkableContent.Original"/>).
/// The TypeScript SDK's <c>similarityClassifier</c> judges the same and gives the same reasons.
/// </summary>
public sealed class SimilarityEditClassifier : IEditClassifier
{
    /// <summary>The similarity from which an edit counts as small (0.85: about one word in seven changed).</summary>
    public double MinWordSimilarity { get; init; } = DefaultMinWordSimilarity;

    public const double DefaultMinWordSimilarity = 0.85;

    /// <summary>Above this many word pairs the texts are not compared: <c>rewritten</c>.</summary>
    private const long MaxCells = 4_000_000;

    public EditDecision Classify(EditInput input)
    {
        if (input.Declared is not (EditKind.Rewritten or EditKind.Corrected) || input.Original is null || input.Output is null)
        {
            return new EditDecision(input.Declared);
        }
        var (kind, reason) = Judge(input.Original, input.Output, MinWordSimilarity);
        return new EditDecision(kind, reason);
    }

    /// <summary><c>corrected</c> for a small change of <paramref name="before"/>, else <c>rewritten</c>.</summary>
    public static EditKind Suggest(string before, string after) => Judge(before, after, DefaultMinWordSimilarity).Kind;

    /// <summary>
    /// How alike two texts are, from 0 to 1: one less the word-level edit distance over the longer text's word count. A
    /// substituted word costs its character distance over its length (a typo fixed costs less than a word changed;
    /// words more than three characters apart in length cost 1). 1 for the same words; 0 for texts too long to compare.
    /// </summary>
    public static double WordSimilarity(string before, string after) => Similarity(Words(before), Words(after)) ?? 0;

    internal static (EditKind Kind, string Reason) Judge(string before, string after, double minimum)
    {
        var a = Words(before);
        var b = Words(after);
        if (Similarity(a, b) is not { } similarity)
        {
            return (EditKind.Rewritten, "too long to compare");
        }
        var sentences = (Sentences(before), Sentences(after));
        var ratio = a.Count == 0 ? (b.Count == 0 ? 1 : double.PositiveInfinity) : (double)b.Count / a.Count;
        var kept = Math.Round(similarity * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " %";
        if (similarity >= minimum && sentences.Item1 == sentences.Item2 && ratio is >= 0.8 and <= 1.25)
        {
            return (EditKind.Corrected, $"small change: {kept} of words kept, the same {sentences.Item1} sentence(s)");
        }
        return (EditKind.Rewritten, $"{kept} of words kept, {sentences.Item1} to {sentences.Item2} sentence(s)");
    }

    private static double? Similarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var longest = Math.Max(a.Count, b.Count);
        if (longest == 0)
        {
            return 1;
        }
        if ((long)a.Count * b.Count > MaxCells)
        {
            return null;
        }
        return 1 - Distance(a, b, WordCost) / longest;
    }

    /// <summary>Edit distance between two sequences, two rows; <paramref name="cost"/> prices a substitution (0 to 1).</summary>
    private static double Distance<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, double> cost)
    {
        var previous = new double[b.Count + 1];
        var current = new double[b.Count + 1];
        for (var j = 0; j <= b.Count; j++)
        {
            previous[j] = j;
        }
        for (var i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost(a[i - 1], b[j - 1]));
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Count];
    }

    /// <summary>What replacing one word by another costs: how far apart they are in characters, so a typo fixed costs a fraction.</summary>
    private static double WordCost(string x, string y)
    {
        if (string.Equals(x, y, StringComparison.Ordinal))
        {
            return 0;
        }
        if (Math.Abs(x.Length - y.Length) > 3)
        {
            return 1;
        }
        return Math.Min(1, Distance(x.ToCharArray(), y.ToCharArray(), (p, q) => p == q ? 0 : 1) / Math.Max(x.Length, y.Length));
    }

    /// <summary>
    /// The words of a text: after the normalisation of hashing and in lower case, runs of letters and digits (an
    /// apostrophe or hyphen between two of them stays in the word), each CJK character a word of its own; punctuation
    /// and white space separate.
    /// </summary>
    private static List<string> Words(string text)
    {
        var normalized = ContentHash.Normalize(text).ToLowerInvariant();
        var words = new List<string>();
        var word = new StringBuilder();
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (TokenEstimate.IsCjk(c))
            {
                Flush();
                words.Add(c.ToString());
            }
            else if (char.IsLetterOrDigit(c)
                     || (c is '\'' or '’' or '-' && word.Length > 0 && i + 1 < normalized.Length && char.IsLetterOrDigit(normalized[i + 1])))
            {
                word.Append(c);
            }
            else
            {
                Flush();
            }
        }
        Flush();
        return words;

        void Flush()
        {
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
    }

    private static bool IsSentenceMark(char c) => c is '.' or '!' or '?' or '。' or '！' or '？';

    /// <summary>
    /// How many sentences: runs of sentence marks followed by white space, the end or a CJK character, and one more
    /// when the text does not end with a mark; none for empty text.
    /// </summary>
    private static int Sentences(string text)
    {
        var normalized = ContentHash.Normalize(text);
        if (normalized.Length == 0)
        {
            return 0;
        }
        var count = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (!IsSentenceMark(normalized[i]) || (i + 1 < normalized.Length && IsSentenceMark(normalized[i + 1])))
            {
                continue;
            }
            if (i + 1 == normalized.Length || normalized[i + 1] == ' ' || TokenEstimate.IsCjk(normalized[i + 1]))
            {
                count++;
            }
        }
        return IsSentenceMark(normalized[normalized.Length - 1]) ? count : count + 1;
    }
}
