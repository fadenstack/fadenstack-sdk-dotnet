using System.Globalization;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Provenance;

/// <summary>
/// Tells a light correction from a rewrite by comparing the text before and after: <c>corrected</c> (exempt) only when
/// most words stay, the sentences stay the same in number and the length barely changes; <c>rewritten</c> (marked)
/// otherwise, and whenever in doubt. A tool calls <see cref="Suggest"/> ("improve the writing" returns
/// <c>corrected</c> for a light polish), or the module uses it as its classifier for placements whose kind is
/// <c>rewritten</c> or <c>corrected</c> and that say what they started from (<see cref="MarkableContent.Original"/>).
/// </summary>
public sealed class SimilarityEditClassifier : IEditClassifier
{
    /// <summary>The share of words that must stay for <c>corrected</c> (0.85).</summary>
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

    /// <summary>The share of words of the longer text that an edit kept (1 - word edit distance / length), from 0 to 1.</summary>
    public static double WordSimilarity(string before, string after)
    {
        var a = Words(before);
        var b = Words(after);
        return Similarity(a, b) ?? 0;
    }

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
        var kept = similarity.ToString("P0", CultureInfo.InvariantCulture);
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
        // Levenshtein over words, two rows.
        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (var j = 0; j <= b.Count; j++)
        {
            previous[j] = j;
        }
        for (var i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                var cost = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal) ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return 1 - (double)previous[b.Count] / longest;
    }

    /// <summary>The words of a text as hashing normalises it: split on white space, each CJK character a word.</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        foreach (var c in ContentHash.Normalize(text))
        {
            if (c == ' ' || TokenEstimate.IsCjk(c))
            {
                if (word.Length > 0)
                {
                    words.Add(word.ToString());
                    word.Clear();
                }
                if (c != ' ')
                {
                    words.Add(c.ToString());
                }
                continue;
            }
            word.Append(c);
        }
        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }
        return words;
    }

    /// <summary>How many sentences: runs of sentence marks that end one, at least one for any text.</summary>
    private static int Sentences(string text)
    {
        var normalized = ContentHash.Normalize(text);
        var count = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] is '.' or '!' or '?' or '。' or '！' or '？'
                && (i + 1 == normalized.Length || normalized[i + 1] == ' ' || TokenEstimate.IsCjk(normalized[i + 1])))
            {
                count++;
            }
        }
        return Math.Max(count, normalized.Length == 0 ? 0 : 1);
    }
}
