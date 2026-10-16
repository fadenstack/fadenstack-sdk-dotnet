using System.Globalization;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.Models;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>Where the composer asks for a skill: <c>$name</c> at the end, or <c>/name</c> as the whole text.</summary>
public class SkillTokenTests
{
    private static readonly SkillOption[] Options =
    {
        new() { Id = "1", Slug = "reply-in-house-style", Name = "Reply in our style", Description = "Answers a customer mail the way we write", Activation = "manual" },
        new() { Id = "2", Slug = "contract-check", Name = "Vertragsprüfung", Description = "Checks a clause against the legal checklist", Activation = "manual" },
        new() { Id = "3", Slug = "summary", Name = "Short summary", Description = "Five bullet points", Activation = "manual" },
    };

    [Fact]
    public void It_finds_a_dollar_name_at_the_end_after_a_space_or_at_the_start_and_a_slash_name_as_the_whole_message()
    {
        Assert.Equal(new SkillToken("", 0), SkillToken.Find("$"));
        Assert.Equal(new SkillToken("contr", 13), SkillToken.Find("Please check $contr"));
        Assert.Equal(new SkillToken("sum", 0), SkillToken.Find("/sum"));
        Assert.Equal(new SkillToken("vertrags-p", 6), SkillToken.Find("Prüfe $vertrags-p"));
    }

    [Fact]
    public void Prices_paths_and_words_with_a_dollar_inside_stay_text()
    {
        Assert.Null(SkillToken.Find("It costs $5 now"));
        Assert.Null(SkillToken.Find("US$"));
        Assert.Null(SkillToken.Find("see /docs and more"));
        Assert.Null(SkillToken.Find("$summary done"));
        Assert.Null(SkillToken.Find("Please check $contr\n")); // a $ before the last line break is not at the end
    }

    [Fact]
    public void Names_and_slugs_match_first_then_descriptions_without_regard_to_case_or_accents()
    {
        Assert.Equal(new[] { "contract-check" }, SkillPickerViewModel.Match(Options, "vertragspruf").Select(s => s.Slug));
        Assert.Equal(new[] { "reply-in-house-style" }, SkillPickerViewModel.Match(Options, "MAIL").Select(s => s.Slug));
        Assert.Equal(new[] { "reply-in-house-style", "contract-check" }, SkillPickerViewModel.Match(Options, "s").Select(s => s.Slug).Take(2));
        Assert.Equal(3, SkillPickerViewModel.Match(Options, " ").Count);
    }
}

/// <summary>The skill picker, picked skills, and pictures, in the chat against a scripted gateway.</summary>
public class SkillsPicturesTests
{
    private const string PictureUrl = "data:image/jpeg;base64,/9j/4AAQSkZJRg==";

    private static readonly FakeSkill[] Offered =
    {
        new("reply-in-house-style", "Reply in our style", "Answers a customer mail the way we write"),
        new("contract-check", "Vertragsprüfung", "Checks a clause against the legal checklist"),
        new("summary", "Short summary", "Five bullet points"),
    };

    private static async Task<AgentChatViewModel> ChatAsync(Gateway gateway, ISessionStore? store = null)
    {
        var broker = new ApprovalBroker();
        var agent = gateway.Builder(store).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo("en-GB"),
        });
        await chat.InitializeAsync();
        return chat;
    }

    private static async Task Until(Func<bool> check)
    {
        for (var i = 0; i < 400 && !check(); i++)
        {
            await Task.Delay(5);
        }
        Assert.True(check(), "timed out waiting");
    }

    private static string[]? SkillsOf(Recorded request) =>
        request.Json.TryGetProperty("skills", out var skills) ? skills.EnumerateArray().Select(s => s.GetString()!).ToArray() : null;

    [Fact]
    public async Task It_opens_on_a_dollar_narrows_as_the_user_types_and_the_pick_goes_with_the_next_message_only()
    {
        var gateway = new Gateway(skills: Offered).Says("Ok.").Says("Ok again.");
        var chat = await ChatAsync(gateway);
        Assert.True(chat.CanPickSkills);

        chat.Input = "Answer this mail $";
        Assert.True(chat.SkillPicker.IsOpen);
        await Until(() => !chat.SkillPicker.IsLoading);
        Assert.Equal(Offered.Select(s => s.Slug), chat.SkillPicker.Options.Select(s => s.Slug));
        chat.Input = "Answer this mail $sty";
        Assert.Equal(new[] { "reply-in-house-style" }, chat.SkillPicker.Options.Select(s => s.Slug));
        chat.SkillPicker.Pick();
        Assert.False(chat.SkillPicker.IsOpen);
        Assert.Equal("Answer this mail ", chat.Input);
        Assert.Equal(new[] { "reply-in-house-style" }, chat.PickedSkills.Select(s => s.Slug));

        await chat.SendCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "reply-in-house-style" }, SkillsOf(gateway.ChatRequests[0]));
        Assert.Empty(chat.PickedSkills);
        Assert.Equal(new[] { new AttachmentChip("Reply in our style", null) { IsSkill = true } }, chat.Turns[0].Attachments);
        await chat.SendAsync("And another one");
        Assert.Null(SkillsOf(gateway.ChatRequests[1]));
        Assert.Single(gateway.SkillRequests); // the list was read once
    }

    [Fact]
    public async Task The_arrows_move_round_the_list_and_a_slash_at_the_start_opens_it_too()
    {
        var chat = await ChatAsync(new Gateway(skills: Offered));
        chat.Input = "/";
        await Until(() => !chat.SkillPicker.IsLoading);
        chat.SkillPicker.Move(-1);
        Assert.Equal(2, chat.SkillPicker.ActiveIndex);
        chat.SkillPicker.Move(1);
        Assert.Equal(0, chat.SkillPicker.ActiveIndex);
        chat.SkillPicker.PickCommand.Execute(null);
        Assert.Equal("", chat.Input);
        Assert.Equal(new[] { "reply-in-house-style" }, chat.PickedSkills.Select(s => s.Slug));
        chat.RemoveSkillCommand.Execute(chat.PickedSkills[0]);
        Assert.Empty(chat.PickedSkills);
    }

    [Fact]
    public async Task It_never_opens_on_a_server_without_skills()
    {
        var gateway = new Gateway();
        var chat = await ChatAsync(gateway);
        Assert.False(chat.CanPickSkills);
        chat.Input = "$";
        Assert.False(chat.SkillPicker.IsOpen);
        Assert.Empty(gateway.SkillRequests);
    }

    [Fact]
    public async Task The_picked_skills_and_the_pictures_stay_with_the_transcript()
    {
        var store = new MemorySessionStore();
        var chat = await ChatAsync(new Gateway(vision: "true", skills: Offered).Says("Five points."), store);
        chat.Input = "$summ";
        await Until(() => !chat.SkillPicker.IsLoading);
        chat.SkillPicker.Pick();
        Assert.True(chat.AttachPicture(PictureUrl, "Screenshot", "screenshot"));
        await chat.SendAsync("Summarise it");
        var id = chat.CurrentSessionId!;

        var again = await ChatAsync(new Gateway(vision: "true", skills: Offered), store);
        await again.OpenSessionAsync(id);
        Assert.Equal(new[]
        {
            new AttachmentChip("Short summary", null) { IsSkill = true },
            new AttachmentChip("Screenshot", "screenshot") { Image = PictureUrl },
        }, again.Turns[0].Attachments);
    }

    [Fact]
    public async Task The_picker_says_what_it_cannot_show_and_reads_the_list_again_after_a_failure_or_a_while()
    {
        var reads = 0;
        var fail = true;
        IReadOnlyList<SkillOption> list = new[] { new SkillOption { Slug = "reply", Name = "Reply in our style" } };
        var now = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
        SkillOption? picked = null;
        var picker = new SkillPickerViewModel(_ =>
        {
            reads++;
            return fail ? Task.FromException<IReadOnlyList<SkillOption>>(new FadenstackException("down")) : Task.FromResult(list);
        }, s => picked = s, () => UiStrings.For(CultureInfo.GetCultureInfo("en")), () => now);

        picker.Show("");
        await Until(() => !picker.IsLoading);
        Assert.Equal((true, "The skills could not be loaded."), (picker.Failed, picker.Note));
        fail = false;
        picker.Show("re");
        await Until(() => picker.HasOptions);
        Assert.Equal((2, false, ""), (reads, picker.Failed, picker.Note));
        picker.Show("zzz");
        Assert.Equal("No skill matches \"zzz\".", picker.Note);
        picker.Show("r");
        Assert.Equal(2, reads); // still fresh
        now = now.AddMinutes(6);
        list = Array.Empty<SkillOption>();
        picker.Show("re");
        await Until(() => reads == 3 && !picker.HasSkills);
        Assert.Equal("No skills are available to you.", picker.Note);
        picker.Pick();
        Assert.Null(picked);
        Assert.NotEqual(picker.Key, new SkillPickerViewModel(_ => Task.FromResult(list), _ => { }).Key);
    }

    [Fact]
    public async Task A_picture_goes_with_the_message_as_an_image_part_for_a_model_that_sees_pictures()
    {
        var gateway = new Gateway(vision: "true").Says("The disk is full.");
        var chat = await ChatAsync(gateway);
        Assert.True(chat.CanAttachPictures);
        Assert.True(chat.AttachPicture(PictureUrl, "Screenshot", "screenshot"));
        Assert.Equal((PictureUrl, true), (chat.Attachments[0].Image, chat.Attachments[0].HasImage));

        await chat.SendAsync("What does this error say?");

        var user = gateway.ChatRequests[0].Json.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
        var parts = user.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(new[] { "image_url", "text" }, parts.Select(p => p.GetProperty("type").GetString()));
        Assert.Equal((PictureUrl, "What does this error say?"),
            (parts[0].GetProperty("image_url").GetProperty("url").GetString(), parts[1].GetProperty("text").GetString()));
        var chip = chat.Turns[0].Attachments.Single();
        Assert.Equal(("Screenshot", PictureUrl), (chip.Title, chip.Image));
    }

    [Fact]
    public async Task A_pasted_picture_is_named_so_and_its_media_type_comes_from_its_url()
    {
        var store = new MemorySessionStore();
        var chat = await ChatAsync(new Gateway(vision: "true").Says("A cat."), store);
        chat.AttachPicture("data:image/png;base64,iVBORw0KGgo=");
        Assert.Equal(("Pasted picture", "Picture"), (chat.Attachments[0].Title, chat.Attachments[0].Subtitle));
        await chat.SendAsync("What is it?");
        var entries = await store.ReadAsync(Gateway.Slug, chat.CurrentSessionId!);
        var stored = entries.First(e => e.Type == EntryTypes.UserMessage).Data.GetProperty("attachments")[0];
        Assert.Equal(("Pasted picture", "picture", "image/png"),
            (stored.GetProperty("title").GetString(), stored.GetProperty("kind").GetString(), stored.GetProperty("media_type").GetString()));
        Assert.Throws<ArgumentException>(() => chat.AttachPicture("https://example.com/cat.png"));
    }

    [Fact]
    public async Task A_picture_is_refused_with_the_reason_when_the_model_does_not_see_pictures()
    {
        var chat = await ChatAsync(new Gateway(vision: "false"));
        Assert.False(chat.AttachPicture(PictureUrl));
        Assert.Empty(chat.Attachments);
        Assert.Equal("This agent's model does not see pictures.", chat.Error);
        chat.PictureNotRead("scan.tiff");
        Assert.Equal("scan.tiff could not be read as a picture.", chat.Error);
    }

    [Fact]
    public async Task A_picture_is_refused_by_a_server_that_does_not_say_whether_its_model_sees_them()
    {
        var chat = await ChatAsync(new Gateway());
        Assert.False(chat.CanAttachPictures);
        Assert.False(chat.AttachPicture(PictureUrl));
    }
}
