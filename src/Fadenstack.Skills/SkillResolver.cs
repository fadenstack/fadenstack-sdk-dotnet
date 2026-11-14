using System.Text.RegularExpressions;

namespace Fadenstack.Skills;

/// <summary>A skill the person can use on this device, with what its <c>faden.yaml</c> says (<see cref="SkillExtension.Empty"/> without one).</summary>
internal sealed class SkillCandidate
{
    internal SkillCandidate(string name, string description, SkillExtension? extension)
    {
        Name = name;
        Description = description;
        Extension = extension ?? SkillExtension.Empty;
        HasFile = extension is not null;
    }

    internal string Name { get; }

    internal string Description { get; }

    internal SkillExtension Extension { get; }

    /// <summary>Whether a <c>faden.yaml</c> was read for the skill.</summary>
    internal bool HasFile { get; }

    internal int Priority => Extension.Priority;

    /// <summary>
    /// A skill the pre-selection does not filter: <c>auto</c> with no keywords and no file types, which is every skill without a
    /// <c>faden.yaml</c>. Core would never offer one (nothing matches it); a person's own skills stay listed as before.
    /// </summary>
    internal bool Unfiltered => Extension.Activation == "auto" && Extension.Keywords.Count == 0 && Extension.FileTypes.Count == 0;
}

/// <summary>What a request says about itself (core's <c>RequestContext</c>, minus the server's users, assistants and projects).</summary>
internal sealed class SkillRequest
{
    internal string? Query { get; set; }

    /// <summary>Skills asked for by name (a picker); a <c>$slug</c> in <see cref="Query"/> asks for one too.</summary>
    internal IReadOnlyList<string> Requested { get; set; } = Array.Empty<string>();

    internal IReadOnlyList<string> FileTypes { get; set; } = Array.Empty<string>();

    internal IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
}

/// <summary>A skill a request gets, and why: <c>explicit</c>, <c>always</c> or <c>trigger</c> (core's <c>Match</c>).</summary>
internal sealed class SkillMatch
{
    internal SkillMatch(SkillCandidate skill, int score, string matchedBy, int triggerHits)
    {
        Skill = skill;
        Score = score;
        MatchedBy = matchedBy;
        TriggerHits = triggerHits;
    }

    internal SkillCandidate Skill { get; }

    internal int Score { get; }

    internal string MatchedBy { get; }

    /// <summary>How many keywords and file types of the skill the request matched (tags do not count: they are a weak sign).</summary>
    internal int TriggerHits { get; }
}

/// <summary>What one turn offers: the skills listed for the model, and the ones whose instructions are loaded for it.</summary>
internal sealed class SkillSelection
{
    internal SkillSelection(IReadOnlyList<SkillCandidate> listed, IReadOnlyList<SkillMatch> matches, IReadOnlyList<SkillMatch> preloaded)
    {
        Listed = listed;
        Matches = matches;
        Preloaded = preloaded;
    }

    internal IReadOnlyList<SkillCandidate> Listed { get; }

    internal IReadOnlyList<SkillMatch> Matches { get; }

    internal IReadOnlyList<SkillMatch> Preloaded { get; }
}

/// <summary>
/// Which skills a turn gets (sdk/51 F04, D16: the model picks; <c>faden.yaml</c> filters, ranks and may pre-load), ported from core's
/// <c>skills/faden_skills/services/resolve.py</c> with its numbers: named skills first (up to 5, whatever their activation), then the
/// <c>always</c> and the matched <c>auto</c> skills by score, cut at 3; <c>manual</c> skills only by name. A keyword counts as a whole word
/// or phrase (+30 each), a file type +30, a tag +20, on top of the skill's priority; ties go to the higher priority, then the name.
/// Core has no assignments, owners or visibility here: every skill is the person's own.
/// <para>
/// Core gives every skill it resolves to the model, with its instructions. This module lists them and loads the instructions of the
/// first <see cref="Select"/>'s <c>maxPreloaded</c> (default 1) that were asked for by name, are <c>always</c> on, or matched a keyword or
/// file type: the "strong" match. A local model has a short context; the others are listed, and <c>load_skill</c> loads any of them.
/// </para>
/// </summary>
internal static class SkillResolver
{
    internal const string Explicit = "explicit";
    internal const string Always = "always";
    internal const string Trigger = "trigger";

    internal const int MaxMatched = 3;
    internal const int MaxNamed = 5;
    internal const int MaxPreloaded = 1;

    private const int TriggerBoost = 30;
    private const int TagBoost = 20;

    // ``$code-review`` in a message: not after a word character or another $ ("US$5", "$$"), and the slug holds a letter ("$100" is a price).
    private static readonly Regex MentionRegex = new(@"(?<![\w$])\$([a-z0-9]+(?:-[a-z0-9]+)*)(?![\w-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>The skills a message asks for with <c>$slug</c>, in order, once each (core's <c>mentions</c>).</summary>
    internal static IReadOnlyList<string> Mentions(string? text)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return found;
        }
        try
        {
            foreach (Match match in MentionRegex.Matches(text))
            {
                var slug = match.Groups[1].Value.ToLowerInvariant();
                if (slug.Any(c => c >= 'a' && c <= 'z') && !found.Contains(slug))
                {
                    found.Add(slug);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // a message built to be slow: no skill is asked for by name
        }
        return found;
    }

    /// <summary>The skills <paramref name="request"/> gets: named ones first, then by score (core's <c>choose</c>).</summary>
    internal static IReadOnlyList<SkillMatch> Choose(IEnumerable<SkillCandidate> candidates, SkillRequest request, int maxResults = MaxMatched,
        int maxExplicit = MaxNamed)
    {
        var all = candidates.ToList();
        var asked = request.Requested.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().ToLowerInvariant()).ToList();
        asked.AddRange(Mentions(request.Query).Where(m => !asked.Contains(m)));

        var explicitMatches = new List<SkillMatch>();
        foreach (var name in asked)
        {
            var pick = all.FirstOrDefault(c => c.Name == name);
            if (pick is not null && explicitMatches.All(m => m.Skill != pick))
            {
                explicitMatches.Add(new SkillMatch(pick, 1000 + pick.Priority, Explicit, 0));
            }
        }
        if (explicitMatches.Count > Math.Max(maxExplicit, 0))
        {
            explicitMatches.RemoveRange(Math.Max(maxExplicit, 0), explicitMatches.Count - Math.Max(maxExplicit, 0));
        }

        var scored = new List<SkillMatch>();
        foreach (var candidate in all)
        {
            if (explicitMatches.Any(m => m.Skill == candidate))
            {
                continue;
            }
            if (Implicit(candidate, request) is { } match)
            {
                scored.Add(match);
            }
        }
        var ranked = scored.OrderByDescending(m => m.Score).ThenByDescending(m => m.Skill.Priority)
            .ThenBy(m => m.Skill.Name, StringComparer.Ordinal).Take(Math.Max(maxResults, 0));
        return explicitMatches.Concat(ranked).ToList();
    }

    private static SkillMatch? Implicit(SkillCandidate candidate, SkillRequest request)
    {
        if (candidate.Extension.Activation == "manual")
        {
            return null;
        }
        var matched = Score(candidate, request);
        if (candidate.Extension.Activation == "always")
        {
            return new SkillMatch(candidate, matched?.Score ?? candidate.Priority, Always, matched?.Hits ?? 0);
        }
        return matched is null ? null : new SkillMatch(candidate, matched.Value.Score, Trigger, matched.Value.Hits);
    }

    private static (int Score, int Hits)? Score(SkillCandidate candidate, SkillRequest request)
    {
        var score = candidate.Priority;
        var hits = 0;
        var matched = false;
        var query = request.Query ?? "";
        var extension = candidate.Extension;
        foreach (var keyword in extension.KeywordMatchers)
        {
            if (query.Length > 0 && IsMatch(keyword, query))
            {
                matched = true;
                hits++;
                score += TriggerBoost;
            }
        }
        var wanted = new HashSet<string>(request.FileTypes.Select(f => f.ToLowerInvariant().TrimStart('.')), StringComparer.Ordinal);
        foreach (var fileType in extension.FileTypes)
        {
            if (wanted.Contains(fileType.ToLowerInvariant().TrimStart('.')))
            {
                matched = true;
                hits++;
                score += TriggerBoost;
            }
        }
        foreach (var tag in request.Tags)
        {
            if (extension.Tags.Contains(tag))
            {
                matched = true;
                score += TagBoost;
            }
        }
        return matched ? (score, hits) : null;
    }

    private static bool IsMatch(Regex keyword, string query)
    {
        try
        {
            return keyword.IsMatch(query);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// What one turn offers: the matches (<see cref="Choose"/>), then the skills that are not filtered, listed for the model; and the
    /// matches whose instructions are loaded (see the class remarks), at most <paramref name="maxPreloaded"/>.
    /// </summary>
    internal static SkillSelection Select(IReadOnlyList<SkillCandidate> candidates, SkillRequest request, int maxResults = MaxMatched,
        int maxExplicit = MaxNamed, int maxPreloaded = MaxPreloaded)
    {
        var matches = Choose(candidates, request, maxResults, maxExplicit);
        var listed = matches.Select(m => m.Skill).ToList();
        listed.AddRange(candidates.Where(c => c.Unfiltered && !listed.Contains(c))
            .OrderByDescending(c => c.Priority).ThenBy(c => c.Name, StringComparer.Ordinal));
        // A skill the person named ($slug) is loaded, faden.yaml or not: they asked for it. Otherwise only a strong match of a skill
        // with faden.yaml (always, or a keyword or file type hit).
        var preloaded = matches.Where(m => m.MatchedBy == Explicit || (m.Skill.HasFile && (m.MatchedBy != Trigger || m.TriggerHits > 0)))
            .Take(Math.Max(maxPreloaded, 0)).ToList();
        return new SkillSelection(listed, matches, preloaded);
    }
}
