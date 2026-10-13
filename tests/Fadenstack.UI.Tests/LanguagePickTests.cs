using System.ComponentModel;
using System.Globalization;
using System.Resources;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The language the user picks in the panel's settings.</summary>
public class LanguagePickTests
{
    private static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);

    private static AgentChatViewModel Chat(Gateway gateway, Action<AgentChatOptions> configure)
    {
        var broker = new ApprovalBroker();
        var agent = gateway.Builder(new Fadenstack.Agents.Sessions.MemorySessionStore()).AddTools(new PageTools()).UseApprovals(broker).Build();
        var options = new AgentChatOptions { RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore() };
        configure(options);
        return new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, options);
    }

    [Fact]
    public void It_starts_in_the_hosts_language_and_lists_every_language_in_its_own_name()
    {
        var chat = Chat(new Gateway(), o => o.AutomaticLanguage = C("de-AT"));
        Assert.Equal(("de", AgentChatViewModel.Automatic, true, true), (chat.Strings.Language, chat.Language, chat.CanChangeLanguage, chat.HasLanguageSettings));
        Assert.Equal(new[]
        {
            ("auto", "Automatisch (Deutsch)", "de", true),
            ("en", "English", "en", false),
            ("de", "Deutsch", "de", false),
            ("fr", "Français", "fr", false),
            ("es", "Español", "es", false),
            ("zh-Hans", "中文（简体）", "zh-Hans", false),
        }, chat.LanguageOptions.Select(o => (o.Id, o.Label, o.Language, o.IsSelected)));
    }

    [Fact]
    public async Task Everything_on_screen_follows_the_pick_the_conversation_too_and_it_is_remembered()
    {
        var gateway = new Gateway().Calls(("call-1", "read_page", """{"page":3}"""))
            .Then(Gateway.Custom("faden.pii", new { entities = new { PERSON = 2 } }), Gateway.Text("Fertig."), Gateway.Finish("stop"), FakeServer.Done);
        var preferences = new MemoryPreferenceStore();
        var chat = Chat(gateway, o =>
        {
            o.AutomaticLanguage = C("de-DE");
            o.Preferences = preferences;
        });
        await chat.InitializeAsync();
        await chat.SendAsync("Lies Seite 3.");
        var turn = chat.Turns.Single();
        var tool = turn.Parts.OfType<ToolCallPart>().Single();
        var notice = turn.Parts.OfType<NoticePart>().Single();
        Assert.Equal(("read_page ausgeführt", "Personenbezogene Daten geschützt", "Nachfragen", "Was möchten Sie Docs fragen?"),
            (tool.Title, notice.Title, chat.ToolModes[2].Label, chat.Greeting));

        var changed = new List<string?>();
        PropertyChangedEventHandler record = (_, e) => changed.Add(e.PropertyName);
        tool.PropertyChanged += record;
        turn.PropertyChanged += record;
        chat.PropertyChanged += record;
        chat.IsSettingsOpen = true;
        Assert.True(chat.SetLanguageCommand.CanExecute("fr"));
        chat.SetLanguageCommand.Execute("fr");

        Assert.False(chat.IsSettingsOpen);
        Assert.Equal(("fr", "fr", "read_page exécuté", "Données personnelles protégées", "Demander", "Posez votre question à Docs."),
            (chat.Language, chat.Strings.Language, tool.Title, notice.Title, chat.ToolModes[2].Label, chat.Greeting));
        Assert.Equal("Automatique (Deutsch)", chat.LanguageOptions[0].Label);
        Assert.True(chat.LanguageOptions.Single(o => o.Id == "fr").IsSelected);
        Assert.Contains(nameof(AgentChatViewModel.Strings), changed);
        Assert.Contains(string.Empty, changed); // the turn and the tool line: every binding reads again
        Assert.Equal("fr", preferences.Get("language"));

        var again = Chat(new Gateway(), o =>
        {
            o.AutomaticLanguage = C("de-DE");
            o.Preferences = preferences;
        });
        Assert.Equal("fr", again.Strings.Language); // from the start: the pick is read when the chat is made

        chat.SetLanguage(AgentChatViewModel.Automatic);
        Assert.Equal(("de", "read_page ausgeführt", (string?)null), (chat.Strings.Language, tool.Title, preferences.Get("language")));
    }

    [Fact]
    public void The_language_the_organisation_fixed_is_kept_and_the_users_old_pick_is_not()
    {
        var preferences = new MemoryPreferenceStore();
        preferences.Set("language", "de");
        var chat = Chat(new Gateway(), o =>
        {
            o.AutomaticLanguage = C("fr");
            o.LockedLanguage = "es-MX";
            o.Preferences = preferences;
        });
        Assert.Equal(("es", "es", false, true, true), (chat.Strings.Language, chat.Language, chat.CanChangeLanguage, chat.IsLanguageLocked, chat.HasLanguageSettings));
        Assert.Equal("es", chat.LanguageOptions.Single(o => o.IsSelected).Id);
        Assert.False(chat.SetLanguageCommand.CanExecute("de"));
        chat.SetLanguage("de");
        Assert.Equal(("es", "de"), (chat.Strings.Language, preferences.Get("language")));
    }

    [Fact]
    public void Texts_the_host_fixed_offer_no_choice_and_a_language_not_offered_is_ignored()
    {
        var chat = Chat(new Gateway(), o => o.Strings = UiStrings.For(C("de")));
        Assert.Equal((false, false, 0, "de"), (chat.CanChangeLanguage, chat.HasLanguageSettings, chat.LanguageOptions.Count, chat.Strings.Language));

        var open = Chat(new Gateway(), o => o.AutomaticLanguage = C("en"));
        open.SetLanguage("klingon");
        open.SetLanguage("ja");
        Assert.Equal("en", open.Strings.Language);
        open.SetLanguage("ZH-hans"); // the offered tag, whatever its case
        Assert.Equal(("zh-Hans", "zh-Hans"), (open.Language, open.Strings.Language));
    }

    [Fact]
    public void The_host_offers_its_own_languages_from_its_resources()
    {
        var host = new ResourceManager("Fadenstack.UI.Tests.HostTexts", typeof(LanguagePickTests).Assembly);
        var chat = Chat(new Gateway(), o =>
        {
            o.AutomaticLanguage = C("en");
            o.TextOverrides = host;
            o.Languages = UiStrings.BuiltInLanguages.Append("it").ToList();
        });
        Assert.Equal("Italiano", chat.LanguageOptions.Last().Label);
        chat.SetLanguage("it");
        Assert.Equal(("it", "Invia", "Allow once"), (chat.Strings.Language, chat.Strings.Send, chat.Strings.Allow));
    }

    [Fact]
    public void A_preferences_file_keeps_the_pick_for_every_panel_and_an_unreadable_one_keeps_nothing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fadenstack-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(folder, "Fadenstack", "preferences.json");
            new FilePreferenceStore(file).Set("language", "de");
            Assert.Equal("de", new FilePreferenceStore(file).Get("language"));
            Assert.Contains("\"language\": \"de\"", File.ReadAllText(file));
            new FilePreferenceStore(file).Set("other", "x");
            new FilePreferenceStore(file).Set("language", null);
            Assert.Equal((null, "x"), (new FilePreferenceStore(file).Get("language"), new FilePreferenceStore(file).Get("other")));

            File.WriteAllText(file, "{ not json");
            Assert.Null(new FilePreferenceStore(file).Get("language"));
            new FilePreferenceStore(file).Set("language", "fr"); // starts again
            Assert.Equal("fr", new FilePreferenceStore(file).Get("language"));
            Assert.EndsWith(Path.Combine("Fadenstack", "preferences.json"), FilePreferenceStore.DefaultPath);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
