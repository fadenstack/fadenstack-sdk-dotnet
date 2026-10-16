using System.Net;
using Fadenstack.Auth;
using Fadenstack.Models;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>The skills a user can pick for a message, and what the profile says about skills and pictures.</summary>
public class SkillsTests
{
    private const string Profile = "/v1/agents/excel/profile";
    private const string Skills = "/v1/agents/excel/skills";

    private static FadenstackClient Client(FakeServer server) => new(new FadenstackClientOptions
    {
        ServerUrl = new Uri("https://faden.test"), Agent = "excel", Credentials = new StaticTokenProvider("t"),
        HttpClient = new HttpClient(server),
    });

    [Fact]
    public async Task The_profile_says_whether_the_model_sees_pictures_and_whether_skills_can_be_picked()
    {
        using var client = Client(new FakeServer().Json("GET", Profile, """
            {"object":"agent.profile","id":"a-1","slug":"excel","name":"Excel","model":"m","vision":true,"images":false,"skills":true}
            """));
        var profile = await client.Agent!.GetProfileAsync();
        Assert.Equal(((bool?)true, false, true), (profile.Vision, profile.Images, profile.Skills));

        using var older = Client(new FakeServer().Json("GET", Profile, """{"object":"agent.profile","id":"a-1","slug":"excel","name":"Excel"}"""));
        var said = await older.Agent!.GetProfileAsync();
        Assert.Equal(((bool?)null, false), (said.Vision, said.Skills));
    }

    [Fact]
    public async Task It_lists_the_skills_the_user_can_pick_narrowed_on_the_server()
    {
        var server = new FakeServer().Skills(Skills, new[]
        {
            new FakeSkill("pivot", "Pivot tables", "Builds one"),
            new FakeSkill("chart", Description: "Draws a chart"),
        });
        using var client = Client(server);

        var all = await client.Agent!.ListSkillsAsync();
        Assert.True(all.Available);
        Assert.Equal(new[]
        {
            new SkillOption { Id = "skill-pivot", Slug = "pivot", Name = "Pivot tables", Description = "Builds one", Activation = "manual" },
            new SkillOption { Id = "skill-chart", Slug = "chart", Name = "chart", Description = "Draws a chart", Activation = "manual" },
        }, all.Skills);

        var narrowed = await client.Agent!.ListSkillsAsync("  draws ", 500);
        Assert.Equal(new[] { "chart" }, narrowed.Skills.Select(s => s.Slug));
        var request = server.Requests.Last();
        Assert.Equal((Skills, "draws", "100"), (request.Path, request.QueryValue("q"), request.QueryValue("limit")));

        Assert.Single((await client.Agent!.ListSkillsAsync(limit: 0)).Skills);
        Assert.Equal("1", server.Requests.Last().QueryValue("limit"));
        Assert.Equal(200, (await Narrowed(server, client, new string('x', 250))).Length);
    }

    private static async Task<string> Narrowed(FakeServer server, FadenstackClient client, string query)
    {
        await client.Agent!.ListSkillsAsync(query);
        return server.Requests.Last().QueryValue("q")!;
    }

    [Fact]
    public async Task A_server_without_skills_or_one_too_old_for_the_route_offers_none()
    {
        using var without = Client(new FakeServer().Skills(Skills, null));
        Assert.Equal((false, 0), await Listed(without));

        using var old = Client(new FakeServer()); // no route: 404 {"detail":"Not Found"}
        Assert.Equal((false, 0), await Listed(old));

        using var gone = Client(new FakeServer().Json("GET", Skills,
            new { error = new { code = "agent_not_found", message = "No agent 'excel'." } }, HttpStatusCode.NotFound));
        var error = await Assert.ThrowsAsync<FadenstackApiException>(() => gone.Agent!.ListSkillsAsync());
        Assert.Equal("agent_not_found", error.Code);
    }

    private static async Task<(bool, int)> Listed(FadenstackClient client)
    {
        var list = await client.Agent!.ListSkillsAsync();
        return (list.Available, list.Skills.Count);
    }
}
