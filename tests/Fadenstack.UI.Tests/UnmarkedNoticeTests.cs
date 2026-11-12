using System.Globalization;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The notice for an answer without an AI mark (<c>faden.unmarked</c>), and the generic one for events nobody knows.</summary>
public class UnmarkedNoticeTests
{
    /// <summary>An answer whose gateway sends the event twice.</summary>
    private static Gateway Unmarked() => new Gateway().Then(
        Gateway.Custom("faden.unmarked", new { reason = "allowed-by-administrator" }),
        Gateway.Custom("faden.unmarked", new { reason = "allowed-by-administrator" }),
        Gateway.Text("Done."), Gateway.Finish("stop"), FakeServer.Done);

    private static AgentChatViewModel Chat(Gateway gateway, IPreferenceStore preferences, string language = "en")
    {
        var broker = new ApprovalBroker();
        var agent = gateway.Builder(new Fadenstack.Agents.Sessions.MemorySessionStore()).UseApprovals(broker).Build();
        var options = new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero,
            Preferences = preferences,
            AutomaticLanguage = CultureInfo.GetCultureInfo(language),
        };
        return new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, options);
    }

    private static async Task<IReadOnlyList<NoticePart>> NoticesAsync(AgentChatViewModel chat)
    {
        await chat.InitializeAsync();
        await chat.SendAsync("Hi");
        return chat.Turns.Single().Parts.OfType<NoticePart>().ToList();
    }

    [Fact]
    public async Task It_is_hidden_by_default()
    {
        var chat = Chat(Unmarked(), new MemoryPreferenceStore());
        Assert.False(chat.TellUnmarked);
        Assert.Empty(await NoticesAsync(chat));
    }

    [Theory]
    [InlineData("en", "Answer without an AI mark", "This answer carries no machine-readable AI mark. Your organisation allowed this model to answer without one.")]
    [InlineData("de", "Antwort ohne KI-Kennzeichen", "Diese Antwort trägt kein maschinenlesbares KI-Kennzeichen. Ihre Organisation hat diesem Modell erlaubt, ohne eines zu antworten.")]
    [InlineData("es", "Respuesta sin marca de IA", "Esta respuesta no lleva una marca de IA legible por máquina. Su organización permitió que este modelo responda sin ella.")]
    [InlineData("fr", "Réponse sans marque d'IA", "Cette réponse ne porte aucune marque d'IA lisible par machine. Votre organisation a autorisé ce modèle à répondre sans marque.")]
    [InlineData("zh-Hans", "无 AI 标记的回答", "此回答不带机器可读的 AI 标记。贵组织已允许该模型在没有标记的情况下作答。")]
    public async Task When_on_it_is_one_quiet_localised_notice_per_answer(string language, string title, string message)
    {
        var preferences = new MemoryPreferenceStore();
        preferences.Set(AgentChatViewModel.TellUnmarkedPreference, "1");
        var chat = Chat(Unmarked(), preferences, language); // the gateway sends the event twice
        var notice = Assert.Single(await NoticesAsync(chat));
        Assert.Equal((NoticeSeverity.Info, title, message), (notice.Severity, notice.Title, notice.Message));
    }

    [Fact]
    public async Task The_setting_is_kept_per_user_and_a_new_panel_finds_it()
    {
        var preferences = new MemoryPreferenceStore();
        var chat = Chat(new Gateway(), preferences);
        chat.ToggleTellUnmarkedCommand.Execute(null);
        Assert.True(chat.TellUnmarked);
        Assert.Equal("1", preferences.Get(AgentChatViewModel.TellUnmarkedPreference));

        var again = Chat(Unmarked(), preferences);
        Assert.True(again.TellUnmarked);
        Assert.Single(await NoticesAsync(again));

        again.TellUnmarked = false;
        Assert.Null(preferences.Get(AgentChatViewModel.TellUnmarkedPreference));
        Assert.False(Chat(new Gateway(), preferences).TellUnmarked);
    }

    [Fact]
    public async Task An_unknown_event_without_a_message_shows_nothing_and_with_one_it_shows_the_message()
    {
        var gateway = new Gateway().Then(
            Gateway.Custom("faden.mystery", new { reason = "x" }),
            Gateway.Custom("faden.nothing_at_all", new { }),
            Gateway.Custom("faden.extension_status", new { message = "Something an extension says." }),
            Gateway.Text("Done."), Gateway.Finish("stop"), FakeServer.Done);
        var notice = Assert.Single(await NoticesAsync(Chat(gateway, new MemoryPreferenceStore())));
        Assert.Equal(("Notice", "Something an extension says."), (notice.Title, notice.Message));
    }
}
