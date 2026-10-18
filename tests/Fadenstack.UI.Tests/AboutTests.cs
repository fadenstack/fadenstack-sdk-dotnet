using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Agents.Tests;
using Fadenstack.Models;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>The links to fadenstack.com: <c>ref</c> and the SDK's major.minor, nothing else.</summary>
public class AboutLinksTests
{
    [Theory]
    [InlineData("office", "office")]
    [InlineData("chrome", "chrome")]
    [InlineData("nextcloud", "nextcloud")]
    [InlineData("my-app2", "my-app2")]
    [InlineData("a1-b2-c3", "a1-b2-c3")]
    [InlineData("abcdefghij0123456789", "abcdefghij0123456789")] // 20 characters
    [InlineData("abcdefghij0123456789x", "sdk")] // 21
    [InlineData(null, "sdk")]
    [InlineData("", "sdk")]
    [InlineData("Office", "sdk")]
    [InlineData("office app", "sdk")]
    [InlineData("office_app", "sdk")]
    [InlineData("-office", "sdk")]
    [InlineData("office-", "sdk")]
    [InlineData("office--app", "sdk")]
    [InlineData("office\n", "sdk")]
    [InlineData("büro", "sdk")]
    [InlineData("office&v=9", "sdk")]
    [InlineData("anna.example.com", "sdk")]
    public void The_hosts_app_name_is_kept_only_when_it_is_short_lower_case_words_joined_by_hyphens(string? refSource, string expected)
    {
        Assert.Equal(expected, AboutLinks.Source(refSource));
    }

    [Theory]
    [InlineData("0.4.5", "0.4")]
    [InlineData("0.2.0-rc.3", "0.2")]
    [InlineData("0.2.1+4ac729f", "0.2")]
    [InlineData("1.10", "1.10")]
    [InlineData("v2.3.1", "2.3")]
    [InlineData("02.05.1", "2.5")]
    [InlineData("1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("next", null)]
    [InlineData("1.2x", null)]
    public void The_version_a_link_carries_is_its_first_two_numbers(string? version, string? expected)
    {
        Assert.Equal(expected, AboutLinks.MajorMinor(version));
    }

    [Fact]
    public void The_links_carry_where_they_were_opened_and_the_sdks_major_minor()
    {
        Assert.Equal("https://fadenstack.com/?ref=sdk-panel&v=0.2", AboutLinks.PanelWebsite(null, "0.2.0-rc.3").AbsoluteUri);
        Assert.Equal("https://fadenstack.com/?ref=office-panel&v=0.2", AboutLinks.PanelWebsite("office", "0.2.1").AbsoluteUri);
        Assert.Equal("https://fadenstack.com/?ref=office-about&v=0.2", AboutLinks.AboutWebsite("office", "0.2.1").AbsoluteUri);
        Assert.Equal("https://developers.fadenstack.com/?ref=chrome-about&v=1.4", AboutLinks.AboutDeveloperDocs("chrome", "1.4.0").AbsoluteUri);
        Assert.Equal("https://fadenstack.com/?ref=sdk-about&v=0.2", AboutLinks.AboutWebsite("Anna's Laptop", "0.2.1").AbsoluteUri);
    }

    [Fact]
    public void Without_a_version_the_link_leaves_v_out()
    {
        Assert.Equal("https://fadenstack.com/?ref=sdk-panel", AboutLinks.PanelWebsite("sdk", "").AbsoluteUri);
        Assert.Equal("https://developers.fadenstack.com/?ref=sdk-about", AboutLinks.AboutDeveloperDocs(null, null).AbsoluteUri);
    }

    [Theory]
    [InlineData("https://ai.example.internal", "https://ai.example.internal/help/", "https://ai.example.internal")]
    [InlineData("https://ai.example.internal/", "https://ai.example.internal/help/", "https://ai.example.internal")]
    [InlineData("https://host.example:8443/faden", "https://host.example:8443/faden/help/", "https://host.example:8443/faden")]
    [InlineData("https://anna:secret@ai.example.internal/?token=x#top", "https://ai.example.internal/help/", "https://ai.example.internal")]
    [InlineData("http://192.0.2.10:8090", "http://192.0.2.10:8090/help/", "http://192.0.2.10:8090")]
    public void Help_is_the_organisations_own_server_without_a_ref(string server, string help, string shown)
    {
        Assert.Equal(help, AboutLinks.Help(new Uri(server))!.AbsoluteUri);
        Assert.Equal(shown, AboutLinks.ServerText(new Uri(server)));
    }

    [Fact]
    public void No_help_link_without_a_web_server()
    {
        Assert.Null(AboutLinks.Help(null));
        Assert.Null(AboutLinks.Help(new Uri("file:///C:/temp/")));
        Assert.Equal("", AboutLinks.ServerText(null));
    }
}

/// <summary>About in the chat: the agent, the app, the SDK, the server, the links, "Copy details", the footer's link.</summary>
public class AboutTests
{
    private static readonly string V = AboutLinks.MajorMinor(SdkInfo.Version) is { } v ? "&v=" + v : "";

    private static AgentChatViewModel Chat(Gateway gateway, Action<AgentChatOptions>? configure = null, Action<AgentBuilder>? build = null,
        IClipboard? clipboard = null)
    {
        var broker = new ApprovalBroker();
        var builder = gateway.Builder().UseApprovals(broker);
        build?.Invoke(builder);
        var options = new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero,
            Preferences = new MemoryPreferenceStore(),
            AutomaticLanguage = CultureInfo.GetCultureInfo("en-GB"),
            Clipboard = clipboard ?? new NoClipboard(),
        };
        configure?.Invoke(options);
        return new AgentChatViewModel(builder.Build(), broker, InlineDispatcher.Instance, options);
    }

    [Fact]
    public async Task About_shows_the_agent_the_app_the_sdk_and_the_server()
    {
        var chat = Chat(new Gateway(), build: b => b.DescribeApp(new AppDescription { Name = "Viewer add-in", Version = "2.1.0" }));
        await chat.InitializeAsync();
        var about = chat.About;
        Assert.Equal(("Docs", "Version 3", "qwen3-32b", "Version 3 · Model: qwen3-32b"), (about.AgentName, about.VersionText, about.Model, about.AgentDetail));
        Assert.False(about.IsDraft);
        Assert.Equal("Runs in Viewer add-in 2.1.0", about.AppText);
        Assert.Equal($"Fadenstack SDK {SdkInfo.Version} (.NET)", about.SdkText);
        Assert.Equal(("https://faden.test", "Server: https://faden.test"), (about.Server, about.ServerText));
        Assert.Equal(new[] { "Help", "Fadenstack", "Developer docs" }, about.Links.Select(l => l.Label));
        Assert.Equal(new[] { "https://faden.test/help/", "https://fadenstack.com/?ref=sdk-about" + V, "https://developers.fadenstack.com/?ref=sdk-about" + V },
            about.Links.Select(l => l.Url.AbsoluteUri));
        Assert.Equal("Help (opens in your browser)", about.Links[0].Name);
    }

    [Fact]
    public void About_is_there_before_the_agent_starts_without_its_lines()
    {
        var chat = Chat(new Gateway());
        Assert.False(chat.About.HasAgent);
        Assert.Equal(("", "", ""), (chat.About.VersionText, chat.About.ModelText, chat.About.AppText));
        Assert.Equal(new[] { $"Fadenstack SDK {SdkInfo.Version} (.NET)", "Server: https://faden.test" },
            chat.About.Details.Split(new[] { Environment.NewLine }, StringSplitOptions.None));
        Assert.Equal(3, chat.About.Links.Count);
    }

    [Fact]
    public void A_draft_says_so_instead_of_a_version_and_an_unknown_model_is_left_out()
    {
        var strings = UiStrings.For(CultureInfo.GetCultureInfo("en"));
        var draft = AboutInfo.Create(new AgentProfile { Name = "Docs", Version = 4, Draft = true }, null, "0.2.0-rc.3", null, null, strings);
        Assert.Equal(("Draft", "", "Draft", true), (draft.VersionText, draft.ModelText, draft.AgentDetail, draft.IsDraft));
        Assert.Equal("Fadenstack SDK 0.2.0-rc.3 (.NET)", draft.SdkText);
        Assert.Equal("", draft.ServerText);
        Assert.Equal(new[] { "Fadenstack", "Developer docs" }, draft.Links.Select(l => l.Label)); // no server: no Help
        Assert.Equal("https://fadenstack.com/?ref=sdk-about&v=0.2", draft.Links[0].Url.AbsoluteUri);
    }

    [Fact]
    public async Task Copy_details_copies_the_agent_its_version_and_model_the_app_the_sdk_and_the_server_one_per_line()
    {
        var clipboard = new NoClipboard();
        var chat = Chat(new Gateway(), build: b => b.DescribeApp(new AppDescription { Name = "Viewer add-in", Version = "2.1.0" }), clipboard: clipboard);
        await chat.InitializeAsync();
        chat.CopyDetailsCommand.Execute(null);
        Assert.Equal(new[]
        {
            "Agent: Docs",
            "Version 3",
            "Model: qwen3-32b",
            "Runs in Viewer add-in 2.1.0",
            $"Fadenstack SDK {SdkInfo.Version} (.NET)",
            "Server: https://faden.test",
        }, clipboard.LastText!.Split(new[] { Environment.NewLine }, StringSplitOptions.None));
    }

    [Fact]
    public async Task About_opens_from_the_settings_closes_them_and_closes_again()
    {
        var chat = Chat(new Gateway());
        await chat.InitializeAsync();
        chat.IsSettingsOpen = true;
        chat.OpenAboutCommand.Execute(null);
        Assert.Equal((true, false), (chat.IsAboutOpen, chat.IsSettingsOpen));
        chat.CloseAboutCommand.Execute(null);
        Assert.False(chat.IsAboutOpen);
    }

    [Fact]
    public async Task A_link_opens_through_the_skins_hook_and_the_view_model_opens_nothing_itself()
    {
        var chat = Chat(new Gateway());
        await chat.InitializeAsync();
        var opened = new List<Uri>();
        chat.LinkRequested += (_, url) => opened.Add(url);
        chat.OpenLinkCommand.Execute(chat.About.Links[1]);
        chat.OpenLinkCommand.Execute(chat.BrandingLink);
        Assert.False(chat.OpenLinkCommand.CanExecute(null));
        Assert.Equal(new[] { "https://fadenstack.com/?ref=sdk-about" + V, "https://fadenstack.com/?ref=sdk-panel" + V }, opened.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task The_footer_link_goes_to_the_website_with_the_panels_ref_and_branding_off_hides_only_it()
    {
        var chat = Chat(new Gateway());
        await chat.InitializeAsync();
        Assert.True(chat.Branding);
        Assert.Equal(("Fadenstack", "https://fadenstack.com/?ref=sdk-panel" + V), (chat.BrandingLink!.Label, chat.BrandingLink.Url.AbsoluteUri));
        Assert.Equal("Fadenstack (opens in your browser)", chat.BrandingLink.Name);

        var plain = Chat(new Gateway(), o => o.Branding = false);
        await plain.InitializeAsync();
        Assert.False(plain.Branding);
        Assert.Null(plain.BrandingLink);
        plain.OpenAboutCommand.Execute(null);
        Assert.True(plain.IsAboutOpen); // About stays
        Assert.Contains(plain.About.Links, l => l.Label == "Fadenstack");
    }

    [Fact]
    public async Task The_host_names_its_app_for_the_ref_and_a_name_that_is_not_allowed_is_sdk()
    {
        var office = Chat(new Gateway(), o => o.RefSource = "office");
        await office.InitializeAsync();
        Assert.Equal("https://fadenstack.com/?ref=office-panel" + V, office.BrandingLink!.Url.AbsoluteUri);
        Assert.Equal(new[] { "https://fadenstack.com/?ref=office-about" + V, "https://developers.fadenstack.com/?ref=office-about" + V },
            office.About.Links.Skip(1).Select(l => l.Url.AbsoluteUri));

        var named = Chat(new Gateway(), o => o.RefSource = "Contoso Word");
        Assert.Equal("https://fadenstack.com/?ref=sdk-panel" + V, named.BrandingLink!.Url.AbsoluteUri);
    }

    [Fact]
    public async Task No_link_to_fadenstack_com_carries_the_server_the_agent_the_app_or_the_user()
    {
        var gateway = new Gateway(userLanguage: "de");
        var chat = Chat(gateway, o => o.RefSource = "office", b => b.DescribeApp(new AppDescription { Name = "Viewer add-in", Version = "2.1.0" }));
        await chat.InitializeAsync();
        var outside = chat.About.Links.Concat(new[] { chat.BrandingLink! }).Where(l => l.Url.Host.EndsWith("fadenstack.com")).ToList();
        Assert.Equal(
            new[] { "?ref=office-about" + V, "?ref=office-about" + V, "?ref=office-panel" + V },
            outside.Select(l => l.Url.Query));
        Assert.All(outside, l => Assert.Equal("/", l.Url.AbsolutePath));
        Assert.All(outside, l => Assert.Equal("", l.Url.Fragment));
    }

    [Fact]
    public async Task About_and_the_footer_link_follow_the_panels_language()
    {
        var chat = Chat(new Gateway(), build: b => b.DescribeApp(new AppDescription { Name = "Viewer add-in", Version = "2.1.0" }));
        await chat.InitializeAsync();
        await chat.SetLanguageAsync("de");
        Assert.Equal(("Version 3 · Modell: qwen3-32b", "Läuft in Viewer add-in 2.1.0", "Server: https://faden.test"),
            (chat.About.AgentDetail, chat.About.AppText, chat.About.ServerText));
        Assert.Equal(new[] { "Hilfe", "Fadenstack", "Entwicklerdokumentation" }, chat.About.Links.Select(l => l.Label));
        Assert.Equal("Hilfe (öffnet sich im Browser)", chat.About.Links[0].Name);
        Assert.Equal("Fadenstack (öffnet sich im Browser)", chat.BrandingLink!.Name);
        Assert.Equal(("Info", "Details kopieren"), (chat.Strings.About, chat.Strings.CopyDetails));

        await chat.SetLanguageAsync("zh-Hans");
        Assert.Equal("版本 3 · 模型：qwen3-32b", chat.About.AgentDetail);
        await chat.SetLanguageAsync("fr");
        Assert.Equal("Version 3 · Modèle\u00a0: qwen3-32b", chat.About.AgentDetail);
        await chat.SetLanguageAsync("es");
        Assert.Equal("Borrador", AboutInfo.Create(new AgentProfile { Name = "Docs", Draft = true }, null, "", null, null, chat.Strings).VersionText);
    }
}
