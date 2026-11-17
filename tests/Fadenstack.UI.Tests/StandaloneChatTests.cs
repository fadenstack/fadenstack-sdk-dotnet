using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Client.Tests;
using Fadenstack.Endpoints;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// A standalone agent's panel: pictures when the model's vision is not known (F2), no thinking pill where the dialect has no
/// switch (F7), and what Off says when the model thinks anyway (F10). Governed behaviour is in SkillsPicturesTests and ReasoningPillTests.
/// </summary>
public class StandaloneChatTests
{
    private const string Definition = """
        {"slug":"local-assistant","name":"Local","version":{"instructions":"Hi.","host_tool_contract":[],"ui":{}}}
        """;

    private const string Picture = "data:image/png;base64,iVBORw0KGgo=";

    private static EndpointReport Report(bool? vision = null, bool? thinksWhenOff = null) => new()
    {
        BaseUrl = new Uri("http://localhost:11434/v1"), Model = "m", Server = EndpointServer.Ollama, Reachable = true, KeyAccepted = true,
        Models = ["m"], Chat = true, Usage = true, ToolCalls = true, Vision = vision, ThinksWhenOff = thinksWhenOff,
    };

    private static async Task<AgentChatViewModel> Standalone(EndpointDialect dialect, EndpointReport? report = null)
    {
        var broker = new ApprovalBroker();
        var agent = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Model = "m", Dialect = dialect, Handler = new OpenAiFake() }, report)
            .UseLocalProfile(AgentDefinition.FromJson(Definition))
            .UseSessionStore(new MemorySessionStore())
            .UseApprovalStore(new MemoryApprovalStore())
            .UseApprovals(broker)
            .Build();
        var options = new AgentChatOptions { RenderInterval = TimeSpan.Zero, AutomaticLanguage = new CultureInfo("en") };
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, options);
        await chat.InitializeAsync();
        return chat;
    }

    [Fact]
    public async Task A_model_whose_vision_is_not_known_accepts_a_picture()
    {
        var chat = await Standalone(EndpointDialect.Ollama, Report(vision: null));

        Assert.True(chat.CanAttachPictures);
        Assert.True(chat.AttachPicture(Picture, "Pasted"));
        Assert.Null(chat.Error);
    }

    [Fact]
    public async Task A_model_checked_to_see_pictures_accepts_one()
    {
        Assert.True((await Standalone(EndpointDialect.Ollama, Report(vision: true))).CanAttachPictures);
    }

    [Fact]
    public async Task A_model_checked_not_to_see_pictures_refuses_with_the_sentence()
    {
        var chat = await Standalone(EndpointDialect.Ollama, Report(vision: false));

        Assert.False(chat.CanAttachPictures);
        Assert.False(chat.AttachPicture(Picture, "Pasted"));
        Assert.Empty(chat.Attachments);
        Assert.Equal("This agent's model does not see pictures.", chat.Error);
    }

    public static IEnumerable<object[]> NoSwitch() => [[EndpointDialect.OpenAICompatible], [EndpointDialect.LMStudio]];

    public static IEnumerable<object[]> WithSwitch() => [[EndpointDialect.Ollama], [EndpointDialect.OpenRouter], [EndpointDialect.Vllm]];

    [Theory]
    [MemberData(nameof(NoSwitch))]
    public async Task The_thinking_pill_is_not_shown_where_the_dialect_sends_no_switch(EndpointDialect dialect)
    {
        var chat = await Standalone(dialect);

        Assert.Empty(chat.ReasoningOptions);
        Assert.False(chat.OffersReasoning);
    }

    [Theory]
    [MemberData(nameof(WithSwitch))]
    public async Task The_thinking_pill_is_shown_with_its_levels_where_the_dialect_has_a_switch(EndpointDialect dialect)
    {
        var chat = await Standalone(dialect);

        Assert.True(chat.OffersReasoning);
        Assert.Equal(new[] { "Auto", "Off", "Low", "Medium", "High" }, chat.ReasoningOptions.Select(o => o.Label));
    }

    private static string Off(AgentChatViewModel chat) => chat.ReasoningOptions.Single(o => o.Level == ReasoningLevel.Off).Description;

    [Fact]
    public async Task On_a_reasoning_effort_dialect_Off_uses_the_lowest_level_when_the_model_thinks_anyway()
    {
        var chat = await Standalone(EndpointDialect.Ollama, Report(thinksWhenOff: true));

        Assert.Equal("This model always thinks. Off uses its lowest level.", Off(chat));
    }

    [Fact]
    public async Task On_a_chat_template_dialect_the_model_thinks_even_when_Off()
    {
        var chat = await Standalone(EndpointDialect.Vllm, Report(thinksWhenOff: true));

        Assert.Equal("This model thinks even when Off.", Off(chat));
    }

    [Fact]
    public async Task Otherwise_Off_keeps_its_own_line()
    {
        Assert.Equal("Answers at once.", Off(await Standalone(EndpointDialect.Ollama, Report(thinksWhenOff: false))));
        Assert.Equal("Answers at once.", Off(await Standalone(EndpointDialect.Ollama)));
    }
}
