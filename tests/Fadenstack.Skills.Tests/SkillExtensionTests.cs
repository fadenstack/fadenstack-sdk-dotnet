using Xunit;

namespace Fadenstack.Skills.Tests;

/// <summary>What <c>faden.yaml</c> may say, by the rules of core's <c>bundle.py</c> (<c>split_extension</c>, <c>normalize_manifest</c>).</summary>
public class SkillExtensionTests
{
    [Fact]
    public void The_office_agents_slide_design_file_reads()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "slide-design", "faden.yaml"));
        var e = SkillExtension.Parse(text);
        Assert.Equal("Slide design", e.Title);
        Assert.Equal(new[] { "powerpoint", "presentations", "design" }, e.Tags);
        Assert.Equal("auto", e.Activation);
        Assert.Equal(60, e.Priority);
        Assert.Equal(new[] { "slide", "slides", "presentation", "deck", "powerpoint", "pitch" }, e.Keywords);
        Assert.Equal(new[] { "get_design", "add_slide", "check_slide", "render_slide" }, e.PreferredTools);
    }

    [Fact]
    public void An_empty_file_and_a_file_without_the_fields_give_the_defaults()
    {
        foreach (var text in new[] { "", "# nothing\n", "unknown_field: 1\n", "preferred_tools:\ntrigger_rules: {}\ntags: []\n" })
        {
            var e = SkillExtension.Parse(text);
            Assert.Equal(50, e.Priority);
            Assert.Equal("auto", e.Activation);
            Assert.Empty(e.Keywords);
            Assert.Empty(e.PreferredTools);
        }
    }

    [Fact]
    public void A_string_where_a_list_goes_is_split_at_commas_and_white_space_as_core_does()
    {
        var e = SkillExtension.Parse("preferred_tools: get_design, add_slide render_slide\ntrigger_rules:\n  keywords: slide, deck\n");
        Assert.Equal(new[] { "get_design", "add_slide", "render_slide" }, e.PreferredTools);
        Assert.Equal(new[] { "slide", "deck" }, e.Keywords);
    }

    [Theory]
    [InlineData("priority: 101")]
    [InlineData("priority: -1")]
    [InlineData("priority: high")]
    [InlineData("priority: true")]
    [InlineData("priority: 1.5")]
    [InlineData("activation: sometimes")]
    [InlineData("activation: true")]
    [InlineData("preferred_tools: [a, [b]]")]
    [InlineData("preferred_tools: 5")]
    [InlineData("tags: {a: b}")]
    [InlineData("trigger_rules: [slide]")]
    [InlineData("trigger_rules:\n  keywords: [yes]")]
    [InlineData("allowed_tools: [1]")]
    [InlineData("- just\n- a list\n")]
    [InlineData("just text\n")]
    [InlineData("a: &x 1\nb: *x\n")]
    public void A_field_of_the_wrong_type_refuses_the_whole_file(string text) =>
        Assert.Throws<FadenYamlException>(() => SkillExtension.Parse(text));

    [Fact]
    public void Allowed_forbidden_and_knowledge_fields_are_checked_and_never_used()
    {
        var e = SkillExtension.Parse("allowed_tools: [read_page]\nforbidden_tools: [send_mail]\nknowledge_sources: [wiki]\nactivation: manual\n");
        Assert.Equal("manual", e.Activation);
        Assert.Empty(e.PreferredTools); // nothing of the three reaches anything that decides a tool
    }

    [Fact]
    public void Keywords_are_limited_in_number_and_length()
    {
        var many = "trigger_rules:\n  keywords: [" + string.Join(",", Enumerable.Range(0, SkillExtension.MaxKeywords + 1).Select(i => "k" + i)) + "]\n";
        Assert.Throws<FadenYamlException>(() => SkillExtension.Parse(many));
        var long1 = "trigger_rules:\n  keywords: [" + new string('k', SkillExtension.MaxKeywordLength + 1) + "]\n";
        Assert.Throws<FadenYamlException>(() => SkillExtension.Parse(long1));
        Assert.Equal(SkillExtension.MaxKeywords, SkillExtension.Parse(many.Replace(",k" + SkillExtension.MaxKeywords, "")).Keywords.Count);
    }

    [Fact]
    public void The_title_is_cut_at_255_characters()
    {
        Assert.Equal(255, SkillExtension.Parse("title: " + new string('t', 400) + "\n").Title!.Length);
    }
}
