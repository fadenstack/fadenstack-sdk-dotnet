using System.Globalization;
using System.Text;
using Fadenstack.Models;
using Fadenstack.Tools;

namespace Fadenstack.Provenance;

/// <summary>
/// Tells a light correction from a rewrite by comparing the text before and after: <c>corrected</c> (exempt) only when,
/// in the sentences that changed, most words stay, the sentences stay the same in number and the word count barely
/// changes; <c>rewritten</c> (marked) otherwise, and whenever in doubt. Only what changed is judged, so a rewritten
/// paragraph in an otherwise unchanged document is a rewrite; edits in several places are each judged on their own. A
/// tool calls <see cref="Suggest(string, string, string)"/> ("improve the writing" returns <c>corrected</c> for a light
/// polish), or the module uses it as its classifier for placements whose kind is <c>rewritten</c> or <c>corrected</c>
/// and that say what they started from (<see cref="MarkableContent.Original"/>). HTML and Markdown are compared by the
/// text a reader sees (<see cref="ReaderText"/>), so markup an editor adds or drops is no change of words. The
/// TypeScript SDK's <c>similarityClassifier</c> judges the same and gives the same reasons
/// (<c>edit-kind-vectors.json</c> is the test).
/// </summary>
public sealed class SimilarityEditClassifier : IEditClassifier
{
    /// <summary>The similarity from which an edit counts as small (0.85: about one word in seven changed).</summary>
    public double MinWordSimilarity { get; init; } = DefaultMinWordSimilarity;

    public const double DefaultMinWordSimilarity = 0.85;

    /// <summary>Above this many word pairs (or sentence pairs) the texts are not compared: <c>rewritten</c>.</summary>
    private const long MaxCells = 4_000_000;

    public EditDecision Classify(EditInput input)
    {
        if (input.Declared is not (EditKind.Rewritten or EditKind.Corrected) || input.Original is null || input.Output is null)
        {
            return new EditDecision(input.Declared);
        }
        var type = input.MediaType ?? "text/plain";
        var (kind, reason) = Judge(ReaderText.Of(input.Original, type), ReaderText.Of(input.Output, type), MinWordSimilarity);
        return new EditDecision(kind, reason);
    }

    /// <summary><c>corrected</c> for a small change of <paramref name="before"/>, else <c>rewritten</c>.</summary>
    public static EditKind Suggest(string before, string after) => Judge(before, after, DefaultMinWordSimilarity).Kind;

    /// <summary>
    /// <c>corrected</c> for a small change of <paramref name="before"/>, else <c>rewritten</c>; HTML and Markdown
    /// (<paramref name="mediaType"/>) compared by the text a reader sees.
    /// </summary>
    public static EditKind Suggest(string before, string after, string mediaType) =>
        Judge(ReaderText.Of(before, mediaType), ReaderText.Of(after, mediaType), DefaultMinWordSimilarity).Kind;

    /// <summary>
    /// How alike two texts are, from 0 to 1: one less the word-level edit distance over the longer text's word count. A
    /// substituted word costs its character distance over its length (a typo fixed costs less than a word changed;
    /// words more than three characters apart in length cost 1). 1 for the same words; 0 for texts too long to compare.
    /// </summary>
    public static double WordSimilarity(string before, string after)
    {
        var a = Read(before).Words;
        var b = Read(after).Words;
        return Measure(Texts(a, 0, a.Count), Texts(b, 0, b.Count)) is { } m ? (m.Longest == 0 ? 1 : 1 - m.Distance / m.Longest) : 0;
    }

    /// <summary>
    /// The kind and why. Only what changed is judged, in whole sentences: a rewritten paragraph inside an unchanged
    /// document is a rewrite, however much of the document stayed. Edits in several places, apart from each other by
    /// sentences that stayed word for word, are each judged on their own, and the revision is <c>corrected</c> only when
    /// every one is a small change: judged together, the unchanged sentences between them would count as kept words and
    /// let a rewrite at one end pass as a correction. The same words throughout (or one change that spans both texts)
    /// are judged as a whole, as before.
    /// </summary>
    internal static (EditKind Kind, string Reason) Judge(string before, string after, double minimum)
    {
        var a = Read(before);
        var b = Read(after);
        if (ChangedRegion(a, b) is not { } region)
        {
            return JudgeWhole(a, b, minimum);
        }
        var total = a.Words.Count;
        if (PlacesIn(a, b, region) is not { } places)
        {
            return (EditKind.Rewritten, $"too long to compare in the changed sentences ({region.A1 - region.A0} of {total} words)");
        }
        if (places.Count == 1 && places[0] is { A0: 0, B0: 0 } only && only.A1 == total && only.B1 == b.Words.Count)
        {
            return JudgeWhole(a, b, minimum);
        }
        string Scope(int words, int? place) =>
            $" in the changed sentences ({(place is null ? "" : $"place {place} of {places.Count}: ")}{words} of {total} words)";
        var all = new Comparison(0, 0, 1, (0, 0), true);
        var changed = 0;
        for (var k = 0; k < places.Count; k++)
        {
            var p = places[k];
            int? place = places.Count > 1 ? k + 1 : null;
            if (Compare(Texts(a.Words, p.A0, p.A1), Texts(b.Words, p.B0, p.B1), TextOf(a, p.A0, p.A1), TextOf(b, p.B0, p.B1), minimum) is not { } c)
            {
                return (EditKind.Rewritten, "too long to compare" + Scope(p.A1 - p.A0, place));
            }
            if (!c.Small)
            {
                return Verdict(c, Scope(p.A1 - p.A0, place));
            }
            all = all with
            {
                Distance = all.Distance + c.Distance, Longest = all.Longest + c.Longest,
                Sentences = (all.Sentences.Before + c.Sentences.Before, all.Sentences.After + c.Sentences.After),
            };
            changed += p.A1 - p.A0;
        }
        all = all with { Kept = all.Longest == 0 ? 1 : 1 - all.Distance / all.Longest };
        return Verdict(all, Scope(changed, null), places.Count);
    }

    /// <summary>The whole of both texts, as the classifier has always compared them.</summary>
    private static (EditKind Kind, string Reason) JudgeWhole(Prose a, Prose b, double minimum) =>
        Compare(Texts(a.Words, 0, a.Words.Count), Texts(b.Words, 0, b.Words.Count), a.Normal, b.Normal, minimum) is { } c
            ? Verdict(c, "")
            : (EditKind.Rewritten, "too long to compare");

    /// <summary>The reason as both SDKs record it; <paramref name="scope"/> says what was compared when it was not the whole text.</summary>
    private static (EditKind Kind, string Reason) Verdict(Comparison c, string scope, int places = 1)
    {
        var kept = Math.Round(c.Kept * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " %";
        return c.Small
            ? (EditKind.Corrected, $"small change{(places > 1 ? $" in {places} places" : "")}: {kept} of words kept{scope}, the same {c.Sentences.Before} sentence(s)")
            : (EditKind.Rewritten, $"{kept} of words kept{scope}, {c.Sentences.Before} to {c.Sentences.After} sentence(s)");
    }

    /// <summary>A comparison of two runs of words: how many were kept, the sentences on each side, and whether that is a small change.</summary>
    private sealed record Comparison(double Distance, double Longest, double Kept, (int Before, int After) Sentences, bool Small);

    private static Comparison? Compare(IReadOnlyList<string> a, IReadOnlyList<string> b, string textA, string textB, double minimum)
    {
        if (Measure(a, b) is not { } m)
        {
            return null;
        }
        var kept = m.Longest == 0 ? 1 : 1 - m.Distance / m.Longest;
        var sentences = (SentenceCount(textA), SentenceCount(textB));
        var ratio = a.Count == 0 ? (b.Count == 0 ? 1 : double.PositiveInfinity) : (double)b.Count / a.Count;
        return new Comparison(m.Distance, m.Longest, kept, sentences,
            kept >= minimum && sentences.Item1 == sentences.Item2 && ratio is >= 0.8 and <= 1.25);
    }

    /// <summary>The word-level edit distance and the longer side's word count; null for texts too long to compare.</summary>
    private static (double Distance, double Longest)? Measure(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var longest = Math.Max(a.Count, b.Count);
        if (longest == 0)
        {
            return (0, 0);
        }
        if ((long)a.Count * b.Count > MaxCells)
        {
            return null;
        }
        return (Distance(a, b, WordCost), longest);
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

    /// <summary>A word of a text: in lower case, where it starts in the text, and which of its sentences it is in (from 0).</summary>
    private readonly record struct Word(string Text, int Start, int Sentence);

    /// <summary>A text as the classifier reads it: normalised as it is hashed and in lower case, and its words.</summary>
    private sealed record Prose(string Normal, IReadOnlyList<Word> Words);

    /// <summary>Words [A0, A1) of the original and [B0, B1) of the revision.</summary>
    private readonly record struct Span(int A0, int A1, int B0, int B1);

    /// <summary>
    /// A text's words: after the normalisation of hashing and in lower case, runs of letters and digits (an apostrophe
    /// or hyphen between two of them stays in the word), each CJK character a word of its own; punctuation and white
    /// space separate. Each word knows its sentence (see <see cref="SentenceEnds"/>).
    /// </summary>
    private static Prose Read(string text)
    {
        var normalized = ContentHash.Normalize(text).ToLowerInvariant();
        var ends = SentenceEnds(normalized);
        var words = new List<Word>();
        var word = new StringBuilder();
        var start = 0;
        var sentence = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (TokenEstimate.IsCjk(c))
            {
                Flush();
                word.Append(c);
                start = i;
                Flush();
            }
            else if (char.IsLetterOrDigit(c)
                     || (c is '\'' or '’' or '-' && word.Length > 0 && i + 1 < normalized.Length && char.IsLetterOrDigit(normalized[i + 1])))
            {
                if (word.Length == 0)
                {
                    start = i;
                }
                word.Append(c);
            }
            else
            {
                Flush();
            }
        }
        Flush();
        return new Prose(normalized, words);

        void Flush()
        {
            if (word.Length > 0)
            {
                while (sentence < ends.Count && ends[sentence] < start)
                {
                    sentence++;
                }
                words.Add(new Word(word.ToString(), start, sentence));
                word.Clear();
            }
        }
    }

    private static List<string> Texts(IReadOnlyList<Word> words, int from, int to)
    {
        var texts = new List<string>(to - from);
        for (var i = from; i < to; i++)
        {
            texts.Add(words[i].Text);
        }
        return texts;
    }

    private static bool IsSentenceMark(char c) => c is '.' or '!' or '?' or '。' or '！' or '？';

    /// <summary>Where sentences end in a normalised text: the last mark of each run of sentence marks followed by a space, the end or a CJK character.</summary>
    private static List<int> SentenceEnds(string normalized)
    {
        var ends = new List<int>();
        for (var i = 0; i < normalized.Length; i++)
        {
            if (!IsSentenceMark(normalized[i]) || (i + 1 < normalized.Length && IsSentenceMark(normalized[i + 1])))
            {
                continue;
            }
            if (i + 1 == normalized.Length || normalized[i + 1] == ' ' || TokenEstimate.IsCjk(normalized[i + 1]))
            {
                ends.Add(i);
            }
        }
        return ends;
    }

    /// <summary>How many sentences a normalised text has: its sentence ends, and one more when it does not end with a mark; none when empty.</summary>
    private static int SentenceCount(string normalized)
    {
        var text = normalized.Trim(' ');
        if (text.Length == 0)
        {
            return 0;
        }
        return SentenceEnds(text).Count + (IsSentenceMark(text[text.Length - 1]) ? 0 : 1);
    }

    /// <summary>Whether a sentence starts (or ends) before word <paramref name="i"/>: at either end, or between two words of different sentences.</summary>
    private static bool Boundary(Prose x, int i) => i == 0 || i == x.Words.Count || x.Words[i - 1].Sentence != x.Words[i].Sentence;

    /// <summary>
    /// What changed, in whole sentences: the words between the common leading and trailing words of both texts,
    /// widened on both sides to where a sentence starts and ends in both (so a word added or fixed is judged with its
    /// sentence, not alone). Null when the words are the same.
    /// </summary>
    private static Span? ChangedRegion(Prose a, Prose b)
    {
        var na = a.Words.Count;
        var nb = b.Words.Count;
        var lead = 0;
        while (lead < na && lead < nb && a.Words[lead].Text == b.Words[lead].Text)
        {
            lead++;
        }
        if (lead == na && lead == nb)
        {
            return null;
        }
        var trail = 0;
        while (trail < na - lead && trail < nb - lead && a.Words[na - 1 - trail].Text == b.Words[nb - 1 - trail].Text)
        {
            trail++;
        }
        // The words before the change are the same in both texts, and so are those after: one index for the start, one
        // count of shared words after the change for the end. Each text's sentences may end elsewhere (a full stop
        // added), so the region grows until it starts and ends at a sentence's edge in both.
        var start = lead;
        var after = trail;
        for (var grown = true; grown;)
        {
            grown = false;
            foreach (var x in new[] { a, b })
            {
                if (!Boundary(x, start))
                {
                    while (!Boundary(x, start))
                    {
                        start--;
                    }
                    grown = true;
                }
                var end = x.Words.Count - after;
                if (!Boundary(x, end))
                {
                    while (!Boundary(x, end))
                    {
                        end++;
                    }
                    after = x.Words.Count - end;
                    grown = true;
                }
            }
        }
        return new Span(start, na - after, start, nb - after);
    }

    /// <summary>The sentences among words [from, to) of a text, which start and end at sentence edges: their words, and their words as one key.</summary>
    private static List<(int From, int To, string Key)> SentencesIn(Prose x, int from, int to)
    {
        var sentences = new List<(int From, int To, string Key)>();
        for (var i = from; i < to;)
        {
            var j = i + 1;
            while (j < to && !Boundary(x, j))
            {
                j++;
            }
            sentences.Add((i, j, string.Join(" ", Texts(x.Words, i, j))));
            i = j;
        }
        return sentences;
    }

    /// <summary>
    /// The places that changed within the region: runs of sentences between sentences both texts have word for word
    /// (their longest common run of sentences). Null when there are too many sentences to line up.
    /// </summary>
    private static List<Span>? PlacesIn(Prose a, Prose b, Span region)
    {
        var sa = SentencesIn(a, region.A0, region.A1);
        var sb = SentencesIn(b, region.B0, region.B1);
        if ((long)sa.Count * sb.Count > MaxCells)
        {
            return null;
        }
        var width = sb.Count + 1;
        // common[i * width + j]: the most sentences sa[i..] and sb[j..] have in common, in order.
        var common = new int[(sa.Count + 1) * width];
        for (var i = sa.Count - 1; i >= 0; i--)
        {
            for (var j = sb.Count - 1; j >= 0; j--)
            {
                common[i * width + j] = sa[i].Key == sb[j].Key
                    ? common[(i + 1) * width + j + 1] + 1
                    : Math.Max(common[(i + 1) * width + j], common[i * width + j + 1]);
            }
        }
        var places = new List<Span>();
        int At(List<(int From, int To, string Key)> s, int k, int end) => k < s.Count ? s[k].From : end;
        var gi = 0;
        var gj = 0;
        void Close(int i, int j)
        {
            if (i > gi || j > gj)
            {
                places.Add(new Span(At(sa, gi, region.A1), At(sa, i, region.A1), At(sb, gj, region.B1), At(sb, j, region.B1)));
            }
        }
        for (int i = 0, j = 0; i < sa.Count && j < sb.Count;)
        {
            if (sa[i].Key == sb[j].Key)
            {
                Close(i, j);
                gi = ++i;
                gj = ++j;
            }
            else if (common[(i + 1) * width + j] >= common[i * width + j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }
        Close(sa.Count, sb.Count);
        return places;
    }

    /// <summary>The text of words [from, to): from the first word's start to the next word's (or the end); "" for none.</summary>
    private static string TextOf(Prose x, int from, int to)
    {
        if (from >= to)
        {
            return "";
        }
        var end = to < x.Words.Count ? x.Words[to].Start : x.Normal.Length;
        return x.Normal.Substring(x.Words[from].Start, end - x.Words[from].Start);
    }
}
