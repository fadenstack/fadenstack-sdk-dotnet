using Fadenstack.SkillAuthoring;
using Xunit;

namespace Fadenstack.Skills.Tests;

/// <summary>The editor's Test step: the module's own pre-selection over the edited skill and the person's others.</summary>
public class SkillTestStepTests
{
    private static SkillDocument Keyed(string name = "contract-review", params string[] keywords) => WorkshopRig.Doc(name) with
    {
        Extension = new SkillExtensionFields { Keywords = keywords },
    };

    [Fact]
    public void A_keyword_prompt_lists_matches_and_pre_loads_the_edited_skill()
    {
        using var rig = new WorkshopRig();

        var result = rig.Workshop.Test("Please review this clause.", Keyed(keywords: "clause"));

        Assert.Contains("contract-review", result.Listed);
        Assert.Equal(new[] { new SkillTestMatch("contract-review", "trigger", 80) }, result.Matches);
        Assert.Equal(new[] { "contract-review" }, result.Preloaded);
        Assert.True(result.EditedListed);
        Assert.Contains("contract-review", result.ModelInstructions);
        Assert.DoesNotContain("slide-design", result.Listed);
    }

    [Fact]
    public void A_prompt_that_names_the_skill_picks_it_as_explicit_whatever_its_activation()
    {
        using var rig = new WorkshopRig();
        var manual = Keyed(keywords: "clause") with { Extension = new SkillExtensionFields { Activation = "manual" } };

        var result = rig.Workshop.Test("Use $contract-review here.", manual);

        Assert.Equal("explicit", result.Matches.Single().MatchedBy);
        Assert.Equal(new[] { "contract-review" }, result.Preloaded);
    }

    [Fact]
    public void A_skill_without_faden_yaml_is_listed_for_the_model_but_never_pre_loaded()
    {
        using var rig = new WorkshopRig();

        var result = rig.Workshop.Test("Anything at all.", WorkshopRig.Doc());

        Assert.Equal(new[] { "contract-review" }, result.Listed);
        Assert.Empty(result.Matches);
        Assert.Empty(result.Preloaded);
        Assert.True(result.EditedListed);
    }

    [Fact]
    public void A_skill_the_prompt_does_not_match_is_not_offered_and_the_test_says_so()
    {
        using var rig = new WorkshopRig();

        var result = rig.Workshop.Test("Something about cooking.", Keyed(keywords: "clause"));

        Assert.False(result.EditedListed);
        Assert.DoesNotContain("contract-review", result.Listed);
        Assert.Null(result.ModelInstructions);
    }

    [Fact]
    public async Task The_edited_skill_stands_in_for_the_saved_one_it_came_from_and_for_its_old_name()
    {
        using var rig = new WorkshopRig();
        await rig.Workshop.SaveAsync(WorkshopRig.Doc("contract-review", "The saved description."));

        var edited = rig.Workshop.Test("hello", WorkshopRig.Doc("contract-review", "The edited description."));
        var renamed = rig.Workshop.Test("hello", WorkshopRig.Doc("nda-review"), "contract-review");

        Assert.Contains("The edited description.", edited.ModelInstructions);
        Assert.DoesNotContain("The saved description.", edited.ModelInstructions);
        Assert.Equal(new[] { "nda-review" }, renamed.Listed);
    }

    [Fact]
    public async Task An_off_skill_is_not_offered_but_the_one_being_edited_always_is()
    {
        using var rig = new WorkshopRig();
        await rig.Workshop.SaveAsync(WorkshopRig.Doc("contract-review"));
        await rig.Workshop.SaveAsync(WorkshopRig.Doc("nda-review", "Reviews NDAs."));
        await rig.Workshop.SetEnabledAsync("nda-review", false);
        await rig.Workshop.SetEnabledAsync("slide-design", false);

        var others = rig.Workshop.Test("Make a slide deck.", null);
        var editing = rig.Workshop.Test("Make a slide deck.", WorkshopRig.Doc("nda-review", "Reviews NDAs."));

        Assert.Equal(new[] { "contract-review" }, others.Listed);
        Assert.Empty(others.Matches);
        Assert.Contains("nda-review", editing.Listed);
    }

    [Fact]
    public void A_bundled_skill_is_picked_on_its_keywords_like_the_server_would_pick_it()
    {
        using var rig = new WorkshopRig();

        var result = rig.Workshop.Test("Build me a slide deck.", null);

        Assert.Equal("slide-design", result.Matches.Single().Name);
        Assert.Equal(new[] { "slide-design" }, result.Preloaded);
    }

    [Fact]
    public void The_test_never_writes_anything()
    {
        using var rig = new WorkshopRig();

        rig.Workshop.Test("a clause", Keyed(keywords: "clause"));

        Assert.False(Directory.Exists(rig.Folder));
        Assert.False(Directory.Exists(rig.Staging));
    }
}
