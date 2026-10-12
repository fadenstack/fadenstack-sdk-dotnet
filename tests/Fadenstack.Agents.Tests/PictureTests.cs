using System.ComponentModel;
using System.Text.Json;
using Fadenstack.Client.Tests;
using Fadenstack.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>A tool that returns a picture, as a slide render does.</summary>
public sealed class RenderTools
{
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    [AgentTool(ToolClass.Read), Description("Renders a page as a picture.")]
    public IList<AIContent> RenderPage(int page) =>
        new List<AIContent> { new TextContent($"page {page} rendered; check: ok"), new DataContent(Png, "image/png") };
}

/// <summary>Pictures from tools: sent with the tool's result only when the model sees, only the newest, never stored.</summary>
public class PictureTests
{
    private static List<JsonElement> Messages(Recorded request) => request.Json.GetProperty("messages").EnumerateArray().ToList();

    private static JsonElement Tool(Recorded request, string callId) =>
        Messages(request).Single(m => m.GetProperty("role").GetString() == "tool" && m.GetProperty("tool_call_id").GetString() == callId);

    [Fact]
    public async Task A_picture_goes_back_with_its_tools_result_when_the_model_sees()
    {
        var gateway = new Gateway(vision: "true").Calls(("call-1", "render_page", """{"page":3}""")).Says("Page 3 looks fine.");
        using var agent = gateway.Builder().AddTools(new RenderTools()).Build();
        var session = await agent.StartSessionAsync();
        var completed = new List<ToolCallCompleted>();

        await foreach (var e in session.StreamAsync("How does page 3 look?"))
        {
            if (e is ToolCallCompleted c)
            {
                completed.Add(c);
            }
        }

        var parts = Tool(gateway.ChatRequests[1], "call-1").GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(new[] { "text", "image_url" }, parts.Select(p => p.GetProperty("type").GetString()));
        Assert.Equal("page 3 rendered; check: ok", parts[0].GetProperty("text").GetString());
        Assert.StartsWith("data:image/png;base64,", parts[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("page 3 rendered; check: ok", completed.Single().Result);
        Assert.Single(completed.Single().Pictures);
        // The transcript keeps the text and a count, never the picture.
        var stored = session.Transcript.Single(t => t.Type == Sessions.EntryTypes.ToolComplete).Data;
        Assert.Equal(("page 3 rendered; check: ok", 1), (stored.GetProperty("result").GetString(), stored.GetProperty("pictures").GetInt32()));
        Assert.DoesNotContain("base64", stored.GetRawText());
    }

    [Fact]
    public async Task Only_the_newest_picture_goes_and_none_in_a_later_turn()
    {
        var gateway = new Gateway(vision: "true")
            .Calls(("call-1", "render_page", """{"page":3}"""))
            .Calls(("call-2", "render_page", """{"page":4}"""))
            .Says("Both look fine.")
            .Says("Yes.");
        using var agent = gateway.Builder().AddTools(new RenderTools()).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("Check pages 3 and 4.");
        await session.SendAsync("All good?");

        var third = gateway.ChatRequests[2];
        Assert.Equal(JsonValueKind.String, Tool(third, "call-1").GetProperty("content").ValueKind);
        Assert.Equal(JsonValueKind.Array, Tool(third, "call-2").GetProperty("content").ValueKind);
        var nextTurn = gateway.ChatRequests[3];
        Assert.All(new[] { "call-1", "call-2" }, id => Assert.Equal(JsonValueKind.String, Tool(nextTurn, id).GetProperty("content").ValueKind));
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("false", true)]
    [InlineData("true", false)]
    public async Task No_picture_goes_when_the_model_cannot_see_or_the_app_says_no(string vision, bool appAllows)
    {
        var gateway = new Gateway(vision: vision).Calls(("call-1", "render_page", """{"page":3}""")).Says("Fine.");
        using var agent = gateway.Builder().AddTools(new RenderTools()).UsePictures(appAllows).Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("How does page 3 look?");

        var content = Tool(gateway.ChatRequests[1], "call-1").GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("page 3 rendered; check: ok\n(The model cannot see pictures: the picture was not sent.)", content.GetString());
    }
}
