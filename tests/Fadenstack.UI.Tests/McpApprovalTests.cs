using System.Globalization;
using Fadenstack.Agents.Tests;
using Fadenstack.Tools;
using Fadenstack.UI.Markdown;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The approval card for a tool from a local MCP server says where it comes from and that nobody has checked it.</summary>
public class McpApprovalTests
{
    private static async Task<ApprovalPart> CardAsync(Fadenstack.Agents.AgentTool tool, string culture = "en-GB", string risk = "")
    {
        var gateway = new Gateway(contract: "[{\"name\":\"" + tool.Name + "\"" + risk + "}]").Calls(("call-1", tool.Name, """{"path":"a"}""")).Says("Done.");
        var broker = new ApprovalBroker();
        var agent = gateway.Builder().AddTool(tool).UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo(culture),
        });
        await chat.InitializeAsync();
        var sending = chat.SendAsync("Go.");
        ApprovalPart? card = null;
        for (var i = 0; i < 500 && card is null; i++)
        {
            await Task.Delay(10);
            card = chat.Turns.LastOrDefault()?.Parts.ToArray().OfType<ApprovalPart>().FirstOrDefault();
        }
        Assert.NotNull(card);
        card!.AllowCommand.Execute(null);
        await sending;
        return card;
    }

    private static Fadenstack.Agents.AgentTool Tool(string name, string source, string? server, ToolClass toolClass = ToolClass.Unknown,
        bool? readOnly = null, bool? destructive = null) =>
        new(AIFunctionFactory.Create(new Func<string, string>(_ => "ok"), name), toolClass, source, server)
        {
            ReadOnlyHint = readOnly, DestructiveHint = destructive,
        };

    [Fact]
    public async Task A_local_MCP_tool_shows_its_server_and_that_the_organisation_has_not_checked_it()
    {
        var card = await CardAsync(Tool("allowed_folders", "mcp", "Files"));

        Assert.True(card.HasSourceLine);
        Assert.Equal("From Files, an MCP server on this computer", card.SourceLine);
        Assert.Equal("Your organisation has not checked this tool.", card.Reason);
        Assert.Equal("Run allowed_folders?", card.Title);
    }

    [Fact]
    public async Task The_servers_own_hint_follows_when_it_gave_one()
    {
        Assert.Equal("Your organisation has not checked this tool. The server says it only reads.",
            (await CardAsync(Tool("long_report", "mcp", "Files", readOnly: true))).Reason);
        Assert.Equal("Your organisation has not checked this tool. The server says it may delete or overwrite.",
            (await CardAsync(Tool("long_report", "mcp", "Files", destructive: true))).Reason);
        // Both, as a server may say: the warning wins.
        Assert.Equal("Your organisation has not checked this tool. The server says it may delete or overwrite.",
            (await CardAsync(Tool("long_report", "mcp", "Files", readOnly: true, destructive: true))).Reason);
    }

    [Fact]
    public async Task The_texts_follow_the_language()
    {
        var card = await CardAsync(Tool("allowed_folders", "mcp", "Files", readOnly: true), "de-DE");

        Assert.Equal("Von Files, einem MCP-Server auf diesem Computer", card.SourceLine);
        Assert.Equal("Ihre Organisation hat dieses Tool nicht geprüft. Der Server gibt an, dass es nur liest.", card.Reason);
    }

    [Fact]
    public async Task Another_unclassified_tool_keeps_its_text_and_has_no_source_line()
    {
        var card = await CardAsync(Tool("do_it", "host", null, readOnly: true));

        Assert.False(card.HasSourceLine);
        Assert.Equal("", card.SourceLine);
        Assert.Equal("Nobody has said what this tool does.", card.Reason);
    }

    [Fact]
    public async Task A_classified_tool_keeps_the_text_of_its_class_even_from_an_MCP_server()
    {
        var card = await CardAsync(Tool("write_it", "mcp", "Files", ToolClass.Write), risk: ",\"risk\":\"write\"");

        Assert.Equal("From Files, an MCP server on this computer", card.SourceLine);
        Assert.Equal("It changes something, which can be changed back.", card.Reason);
    }

    [Fact]
    public async Task The_title_has_the_tool_name_as_a_code_run()
    {
        var card = await CardAsync(Tool("allowed_folders", "mcp", "Files"));

        Assert.Equal(new[] { ("Run ", false), ("allowed_folders", true), ("?", false) },
            card.TitleInlines.Select(r => (r.Text, (r.Style & InlineStyle.Code) != 0)));
    }
}
