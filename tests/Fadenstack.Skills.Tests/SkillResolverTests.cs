using Xunit;

namespace Fadenstack.Skills.Tests;

/// <summary>
/// The pre-selection's rules, ported from core's <c>skills/tests/test_resolve.py</c> (release/0.7.1) and its <c>resolve.py</c>. The
/// tests of owners, visibility and assignments there (a private always-on skill, asking by name for the closest of three, the project and
/// workspace assignments) have no counterpart here: on a device every skill is the person's own. The numbers are core's: a keyword and a file
/// type +30, a tag +20, a named skill 1000, three matches, five named.
/// </summary>
public class SkillResolverTests
{
    /// <summary>A skill as its faden.yaml would make it.</summary>
    internal static SkillCandidate Skill(string name, string activation = "auto", int priority = 50, string[]? keywords = null,
        string[]? fileTypes = null, string[]? tags = null)
    {
        var yaml = "activation: " + activation + "\npriority: " + priority + "\n";
        if (tags is not null)
        {
            yaml += "tags: [" + string.Join(", ", tags) + "]\n";
        }
        if (keywords is not null || fileTypes is not null)
        {
            yaml += "trigger_rules:\n";
            if (keywords is not null)
            {
                yaml += "  keywords: [" + string.Join(", ", keywords.Select(k => "\"" + k + "\"")) + "]\n";
            }
            if (fileTypes is not null)
            {
                yaml += "  file_types: [" + string.Join(", ", fileTypes) + "]\n";
            }
        }
        return new SkillCandidate(name, name + " description", SkillExtension.Parse(yaml));
    }

    private static SkillRequest Ask(string? query = null, string[]? requested = null, string[]? fileTypes = null, string[]? tags = null) => new()
    {
        Query = query,
        Requested = requested ?? Array.Empty<string>(),
        FileTypes = fileTypes ?? Array.Empty<string>(),
        Tags = tags ?? Array.Empty<string>(),
    };

    private static string[] Names(IEnumerable<SkillMatch> matches) => matches.Select(m => m.Skill.Name).ToArray();

    [Fact]
    public void Mentions_are_skills_not_prices()
    {
        var text = "Use $code-review and $SQL-Tuning, not $100, US$5 or $$x; again $code-review.";
        Assert.Equal(new[] { "code-review", "sql-tuning" }, SkillResolver.Mentions(text));
        Assert.Empty(SkillResolver.Mentions(null));
        Assert.Empty(SkillResolver.Mentions(""));
    }

    [Fact]
    public void An_auto_skill_needs_its_trigger_as_a_word()
    {
        var art = Skill("art-critic", keywords: new[] { "art" });
        Assert.Empty(SkillResolver.Choose(new[] { art }, Ask("Let us start the report")));
        Assert.Equal(new[] { "art-critic" }, Names(SkillResolver.Choose(new[] { art }, Ask("What makes art good?"))));
    }

    [Fact]
    public void A_manual_skill_comes_only_when_asked_for()
    {
        var manual = Skill("tone", "manual", keywords: new[] { "tone" });
        Assert.Empty(SkillResolver.Choose(new[] { manual }, Ask("fix the tone")));
        var match = Assert.Single(SkillResolver.Choose(new[] { manual }, Ask("fix the tone with $tone")));
        Assert.Equal("explicit", match.MatchedBy);
    }

    [Fact]
    public void An_always_skill_is_on_in_every_chat()
    {
        var policy = Skill("policy", "always");
        Assert.Equal(new[] { "policy" }, Names(SkillResolver.Choose(new[] { policy }, Ask("hello"))));
    }

    [Fact]
    public void Named_skills_come_first_and_on_top_of_the_limit()
    {
        var always = Enumerable.Range(0, 4).Select(i => Skill("a" + i, "always", 90 - i)).ToList();
        var named = Skill("named", "manual", 1);
        var chosen = SkillResolver.Choose(always.Append(named), Ask(requested: new[] { "named" }), maxResults: 2, maxExplicit: 5);
        Assert.Equal(new[] { "named", "a0", "a1" }, Names(chosen));
    }

    [Fact]
    public void Explicit_requests_are_capped()
    {
        var skills = Enumerable.Range(0, 4).Select(i => Skill("s" + i, "manual")).ToList();
        var chosen = SkillResolver.Choose(skills, Ask(requested: skills.Select(s => s.Name).ToArray()), maxResults: 3, maxExplicit: 2);
        Assert.Equal(new[] { "s0", "s1" }, Names(chosen));
    }

    // Ranking (core's test_rank_skills): the old resolver cut to the top three before it dropped the skills that did not apply.
    private static readonly SkillCandidate Plan = Skill("test-plan", priority: 40, keywords: new[] { "test plan" });
    private static readonly SkillCandidate Cases = Skill("test-cases", priority: 40, keywords: new[] { "pytest" });
    private static readonly SkillCandidate Bugs = Skill("bug-triage", priority: 40, keywords: new[] { "bug" });
    private static readonly SkillCandidate Style = Skill("qa-style", "always", 10);

    [Fact]
    public void An_always_on_skill_is_not_crowded_out_by_ones_that_do_not_apply()
    {
        var chosen = SkillResolver.Choose(new[] { Plan, Cases, Bugs, Style }, Ask("hello there"));
        Assert.Equal(new[] { "qa-style" }, Names(chosen));
    }

    [Fact]
    public void Matched_skills_rank_by_score_and_are_cut_at_the_limit()
    {
        var chosen = SkillResolver.Choose(new[] { Style, Plan, Cases, Bugs }, Ask("a test plan for the pytest bug"));
        Assert.Equal(new[] { "bug-triage", "test-cases", "test-plan" }, Names(chosen));
        Assert.Equal(new[] { 70, 70, 70 }, chosen.Select(m => m.Score).ToArray());
    }

    [Theory]
    [InlineData("slide", 1)]
    [InlineData("a slide and a slide", 1)]
    [InlineData("slide and deck", 2)]
    [InlineData("Slide, DECK, and pitch.", 3)]
    [InlineData("slides", 0)]
    [InlineData("deck-building", 1)]
    [InlineData("slide_one", 0)]
    [InlineData("nodeck", 0)]
    public void Each_keyword_adds_thirty_once_as_a_whole_word_in_any_case(string query, int keywords)
    {
        var skill = Skill("s", priority: 20, keywords: new[] { "slide", "deck", "pitch" });
        var chosen = SkillResolver.Choose(new[] { skill }, Ask(query));
        if (keywords == 0)
        {
            Assert.Empty(chosen);
            return;
        }
        var match = Assert.Single(chosen);
        Assert.Equal(20 + (30 * keywords), match.Score);
        Assert.Equal(keywords, match.TriggerHits);
        Assert.Equal("trigger", match.MatchedBy);
    }

    [Fact]
    public void A_keyword_may_be_a_phrase_and_holds_no_pattern()
    {
        var skill = Skill("s", keywords: new[] { "test plan", "c++", ".*" });
        Assert.Empty(SkillResolver.Choose(new[] { skill }, Ask("a plan for the test")));
        Assert.Single(SkillResolver.Choose(new[] { skill }, Ask("Write a TEST PLAN")));
        Assert.Single(SkillResolver.Choose(new[] { skill }, Ask("port it to c++ today")));
        Assert.Empty(SkillResolver.Choose(new[] { skill }, Ask("anything at all")));
    }

    [Fact]
    public void File_types_and_tags_add_to_the_score_and_a_tag_alone_is_a_weak_match()
    {
        var skill = Skill("sheet", priority: 10, fileTypes: new[] { ".XLSX", "csv" }, tags: new[] { "finance" });
        var byType = Assert.Single(SkillResolver.Choose(new[] { skill }, Ask(fileTypes: new[] { "xlsx", ".csv" })));
        Assert.Equal(70, byType.Score);
        Assert.Equal(2, byType.TriggerHits);
        var byTag = Assert.Single(SkillResolver.Choose(new[] { skill }, Ask(tags: new[] { "finance" })));
        Assert.Equal(30, byTag.Score);
        Assert.Equal(0, byTag.TriggerHits);
        Assert.Empty(SkillResolver.Choose(new[] { skill }, Ask(fileTypes: new[] { "pdf" }, tags: new[] { "legal" })));
    }

    [Fact]
    public void Equal_scores_go_to_the_higher_priority_then_the_name()
    {
        var low = Skill("alpha", priority: 40, keywords: new[] { "x" });
        var high = Skill("zulu", priority: 41, keywords: new[] { "x", "y" });
        var tie1 = Skill("bravo", priority: 40, keywords: new[] { "x" });
        var chosen = SkillResolver.Choose(new[] { low, tie1, high }, Ask("x"), maxResults: 5);
        Assert.Equal(new[] { "zulu", "alpha", "bravo" }, Names(chosen));
        Assert.Equal(new[] { 71, 70, 70 }, chosen.Select(m => m.Score).ToArray());
    }

    [Fact]
    public void A_named_skill_is_scored_by_its_priority_over_a_thousand()
    {
        var match = Assert.Single(SkillResolver.Choose(new[] { Skill("review", priority: 7) }, Ask("use $Review")));
        Assert.Equal(1007, match.Score);
        Assert.Equal("explicit", match.MatchedBy);
    }

    // The part core does not have: what is listed and what is loaded.

    [Fact]
    public void The_list_holds_the_matches_then_the_skills_that_are_not_filtered_and_a_skill_without_faden_yaml_is_one_of_them()
    {
        var plain = new SkillCandidate("plain", "No faden.yaml.", null);
        var open = Skill("open", priority: 80); // a faden.yaml with no triggers
        var slide = Skill("slide-design", keywords: new[] { "slide" });
        var manual = Skill("manual-one", "manual", keywords: new[] { "slide" });
        var all = new[] { plain, slide, manual, open };

        var quiet = SkillResolver.Select(all, Ask("hello"));
        Assert.Equal(new[] { "open", "plain" }, quiet.Listed.Select(s => s.Name).ToArray());
        Assert.Empty(quiet.Matches);

        var slides = SkillResolver.Select(all, Ask("a slide please"));
        Assert.Equal(new[] { "slide-design", "open", "plain" }, slides.Listed.Select(s => s.Name).ToArray());

        var named = SkillResolver.Select(all, Ask("use $manual-one"));
        Assert.Equal(new[] { "manual-one", "open", "plain" }, named.Listed.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void A_skill_without_faden_yaml_is_pre_loaded_only_when_the_person_names_it()
    {
        var plain = new SkillCandidate("plain", "No faden.yaml.", null);
        var unnamed = SkillResolver.Select(new[] { plain }, Ask("plain plain plain"));
        Assert.Empty(unnamed.Matches);
        Assert.Empty(unnamed.Preloaded);
        // Named by the person ($plain): they asked for it, so it is first in the list and its instructions come with the turn.
        var selection = SkillResolver.Select(new[] { plain }, Ask("use $plain"));
        Assert.Single(selection.Matches);
        Assert.Equal("plain", Assert.Single(selection.Preloaded).Skill.Name);
        Assert.Equal(new[] { "plain" }, selection.Listed.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void A_keyword_a_named_skill_and_an_always_skill_are_pre_loaded_and_a_tag_is_not()
    {
        var keyword = Skill("kw", keywords: new[] { "slide" });
        var tagged = Skill("tg", tags: new[] { "design" });
        var always = Skill("al", "always", 5);
        var manual = Skill("mn", "manual");

        Assert.Equal(new[] { "kw" }, Pre(new[] { keyword }, Ask("a slide")));
        Assert.Equal(new[] { "mn" }, Pre(new[] { manual }, Ask("$mn")));
        Assert.Equal(new[] { "al" }, Pre(new[] { always }, Ask("hello")));
        var weak = SkillResolver.Select(new[] { tagged }, Ask(tags: new[] { "design" }));
        Assert.Single(weak.Matches);
        Assert.Empty(weak.Preloaded);
    }

    [Fact]
    public void Only_the_first_strong_matches_up_to_the_limit_are_pre_loaded_in_rank_order()
    {
        var one = Skill("one", priority: 60, keywords: new[] { "x" });
        var two = Skill("two", priority: 50, keywords: new[] { "x" });
        var named = Skill("named", "manual");
        var all = new[] { one, two, named };

        Assert.Equal(new[] { "named" }, Pre(all, Ask("x $named")));
        Assert.Equal(new[] { "named", "one" }, Pre(all, Ask("x $named"), maxPreloaded: 2));
        Assert.Equal(new[] { "one" }, Pre(all, Ask("x")));
        Assert.Empty(Pre(all, Ask("x"), maxPreloaded: 0));
        Assert.Equal(new[] { "one", "two" }, Pre(all, Ask("x"), maxPreloaded: 5));
    }

    private static string[] Pre(IReadOnlyList<SkillCandidate> skills, SkillRequest request, int maxPreloaded = 1) =>
        SkillResolver.Select(skills, request, maxPreloaded: maxPreloaded).Preloaded.Select(m => m.Skill.Name).ToArray();
}
