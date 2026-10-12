using System.ComponentModel;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>A tool's picture (a rendered slide) shows small under its line.</summary>
public class PictureRowTests
{
    public sealed class SlideTools
    {
        public static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

        [AgentTool(ToolClass.Read), Description("Renders a slide.")]
        public IList<AIContent> RenderSlide(int slide) =>
            new List<AIContent> { new TextContent($"slide {slide}: nothing found"), new DataContent(Png, "image/png") };
    }

    [Fact]
    public async Task A_tools_picture_is_on_its_row()
    {
        var gateway = new Gateway(vision: "true").Calls(("call-1", "render_slide", """{"slide":3}""")).Says("Slide 3 is clean.");
        var broker = new ApprovalBroker();
        using var agent = gateway.Builder().AddTools(new SlideTools()).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions { RenderInterval = TimeSpan.Zero });
        await chat.InitializeAsync();

        await chat.SendAsync("How does slide 3 look?");

        var row = chat.Turns.Single().Parts.OfType<ToolCallPart>().Single();
        Assert.True(row.HasPicture);
        Assert.Equal(SlideTools.Png, row.Picture);
        Assert.Equal("Ran render_slide", row.Title);
    }
}
