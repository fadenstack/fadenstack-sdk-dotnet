using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Client.Tests;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>Skills the user picks for a message: on every model call of its turn, kept with it in the transcript.</summary>
public class SkillTests
{
    private static readonly FakeSkill[] Offered =
    {
        new("reply-in-house-style", "Reply in our style", "Answers a customer mail the way we write"),
        new("contract-check", "Contract check"),
    };

    private static string[]? SkillsOf(Recorded request) =>
        request.Json.TryGetProperty("skills", out var skills) ? skills.EnumerateArray().Select(s => s.GetString()!).ToArray() : null;

    [Fact]
    public async Task Every_model_call_of_the_turn_gets_the_picked_skills_and_the_transcript_keeps_them()
    {
        var gateway = new Gateway(skills: Offered).Calls(("call-1", "read_page", """{"page":1}""")).Says("Done.").Says("Again.");
        using var agent = gateway.Builder().AddTools(new PageTools()).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("Answer this mail.", new SendOptions
        {
            Skills = [new PickedSkill("reply-in-house-style", "Reply in our style"), new PickedSkill("reply-in-house-style"), new PickedSkill("")],
        });
        await session.SendAsync("And another one.");

        Assert.Equal(new[] { "reply-in-house-style" }, SkillsOf(gateway.ChatRequests[0]));
        Assert.Equal(new[] { "reply-in-house-style" }, SkillsOf(gateway.ChatRequests[1])); // the call after the tool's result too
        Assert.Null(SkillsOf(gateway.ChatRequests[2])); // the next message has none
        var user = session.Transcript.First(e => e.Type == EntryTypes.UserMessage).Data;
        Assert.Equal(JsonValueKind.Array, user.GetProperty("skills").ValueKind);
        var kept = Assert.Single(user.GetProperty("skills").EnumerateArray());
        Assert.Equal(("reply-in-house-style", "Reply in our style"), (kept.GetProperty("slug").GetString(), kept.GetProperty("name").GetString()));
        Assert.False(session.Transcript.Last(e => e.Type == EntryTypes.UserMessage).Data.TryGetProperty("skills", out _));
    }

    [Fact]
    public async Task The_skills_to_pick_come_from_the_server_and_none_without_the_module()
    {
        var gateway = new Gateway(skills: Offered);
        using var agent = gateway.Builder().Build();
        await agent.StartAsync();
        Assert.True(agent.Profile.Skills);
        Assert.Equal(new[] { "reply-in-house-style", "contract-check" }, (await agent.ListSkillsAsync()).Select(s => s.Slug));
        Assert.Equal(new[] { "contract-check" }, (await agent.ListSkillsAsync("contract")).Select(s => s.Slug));
        Assert.Equal("100", gateway.SkillRequests.Last().QueryValue("limit"));

        var without = new Gateway();
        using var plain = without.Builder().Build();
        await plain.StartAsync();
        Assert.Empty(await plain.ListSkillsAsync());
        Assert.Empty(without.SkillRequests); // the profile said so: nothing asked
    }
}
