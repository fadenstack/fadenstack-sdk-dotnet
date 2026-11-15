using Fadenstack.LocalTools;
using Fadenstack.UI.Tools;
using Xunit;
using static Fadenstack.UI.Tests.ToolsView.ToolsPageTests;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>
/// The owner's rules for "Allow plain http on the local network" (sdk/51 F05): hidden where plain http is not permitted; turning it
/// on shows the warning first and changes nothing until the person confirms, and the confirmation carries the warning's version;
/// turning it off needs no warning; each refusal has its own words.
/// </summary>
public class ToolsPlainHttpTests
{
    private static FakeLocalToolsCatalog WithNas(bool on = false, bool permitted = true, LocalServerState state = LocalServerState.Starting, LocalPlainHttpRefusal? refusal = null)
    {
        var catalog = new FakeLocalToolsCatalog { PlainHttpPermitted = permitted };
        catalog.Servers.Add(FakeLocalToolsCatalog.Nas(on, state, refusal));
        return catalog;
    }

    private static ServerDetailsViewModel Open(ToolsViewModel page)
    {
        Server(page, "nas").OpenCommand.Execute(null);
        return page.Details!;
    }

    [Fact]
    public void The_warnings_version_is_a_constant_next_to_its_text_and_the_warning_is_the_owners_words()
    {
        Assert.Equal("2026-11-15", UiStrings.McpPlainHttpWarningVersion);
        Assert.Equal(
            "Plain http is not encrypted. Anyone on your network can read and change what goes to and from this server: the tool calls, their arguments and their results, which may hold document content. A changed answer can steer the agent. Use https where the server offers it.",
            UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo("en")).McpPlainHttpWarning);
    }

    [Fact]
    public void The_switch_is_there_for_a_plain_http_server_beyond_this_computer_where_it_is_permitted()
    {
        using var page = Page(WithNas());

        var details = Open(page);

        Assert.True(details.ShowsAdvanced);
        Assert.False(details.PlainHttpOn);
        Assert.False(details.IsPlainHttpWarningOpen);
        Assert.Equal("Allow plain http on the local network", page.Strings.McpPlainHttpSwitch);
    }

    [Fact]
    public void The_switch_is_hidden_where_plain_http_is_not_permitted_and_for_a_server_it_does_not_apply_to()
    {
        var catalog = WithNas(permitted: false);
        using var notPermitted = Page(catalog);
        Assert.False(Open(notPermitted).ShowsAdvanced);

        var plain = FakeLocalToolsCatalog.Scene();
        using var page = Page(plain);
        Server(page, "demo").OpenCommand.Execute(null);
        Assert.False(page.Details!.ShowsAdvanced); // a program, or this computer, or https: no switch to show
    }

    [Fact]
    public void A_server_already_on_where_it_is_not_permitted_says_so_in_its_status_and_has_no_switch()
    {
        var catalog = WithNas(on: true, permitted: false, state: LocalServerState.NotAllowed, refusal: LocalPlainHttpRefusal.NotAllowed);
        using var page = Page(catalog);

        Assert.Equal(("Plain http is not allowed here.", ServerStatusKind.Warning), (Server(page, "nas").StatusText, Server(page, "nas").StatusKind));
        Assert.False(Open(page).ShowsAdvanced);
    }

    [Fact]
    public void A_server_on_that_the_module_has_not_tried_yet_is_already_not_allowed_where_it_is_not_permitted()
    {
        var catalog = WithNas(on: true, permitted: false, state: LocalServerState.Starting);
        using var page = Page(catalog);

        Assert.Equal("Plain http is not allowed here.", Server(page, "nas").StatusText);
        // Off says nothing about plain http: nothing is attempted.
        catalog.Servers[0].Enabled = false;
        catalog.Servers[0].State = LocalServerState.Off;
        page.Refresh();
        Assert.Equal("Off", Server(page, "nas").StatusText);
    }

    [Fact]
    public void Turning_it_on_shows_the_warning_and_changes_nothing_until_it_is_confirmed()
    {
        var catalog = WithNas();
        using var page = Page(catalog);
        var details = Open(page);
        var seen = new List<string?>();
        details.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        details.PlainHttpOn = true;

        Assert.True(details.IsPlainHttpWarningOpen);
        Assert.StartsWith("Plain http is not encrypted.", details.PlainHttpWarningText);
        Assert.False(details.PlainHttpOn); // the switch stays off
        Assert.Contains(nameof(ServerDetailsViewModel.PlainHttpOn), seen); // and a switch that was pressed is told so
        Assert.Empty(catalog.Calls);
        Assert.False(catalog.Servers[0].PlainHttpInLan);
        Assert.True(details.ConfirmPlainHttpCommand.CanExecute(null));
        Assert.True(details.CancelPlainHttpCommand.CanExecute(null));
    }

    [Fact]
    public async Task Confirming_turns_it_on_with_the_warnings_version_and_closes_the_warning()
    {
        var catalog = WithNas();
        using var page = Page(catalog);
        var details = Open(page);
        details.PlainHttpOn = true;

        await details.ConfirmPlainHttpCommand.ExecuteAsync(null);

        Assert.Equal(["plain-http nas True 2026-11-15"], catalog.Calls);
        Assert.False(details.IsPlainHttpWarningOpen);
        Assert.True(details.PlainHttpOn);
        Assert.Equal("", details.PlainHttpWarningText);
        Assert.False(details.ConfirmPlainHttpCommand.CanExecute(null)); // nothing to confirm any more
    }

    [Fact]
    public void Cancelling_leaves_it_off_and_asks_nothing_of_the_catalog()
    {
        var catalog = WithNas();
        using var page = Page(catalog);
        var details = Open(page);
        details.PlainHttpOn = true;

        details.CancelPlainHttpCommand.Execute(null);

        Assert.False(details.IsPlainHttpWarningOpen);
        Assert.False(details.PlainHttpOn);
        Assert.Empty(catalog.Calls);
        Assert.False(catalog.Servers[0].PlainHttpInLan);
    }

    [Fact]
    public async Task Turning_it_off_needs_no_warning()
    {
        var catalog = WithNas(on: true);
        using var page = Page(catalog);
        var details = Open(page);
        Assert.True(details.PlainHttpOn);

        details.PlainHttpOn = false;
        await Task.Delay(50);

        Assert.False(details.IsPlainHttpWarningOpen);
        Assert.Equal(["plain-http nas False -"], catalog.Calls);
        Assert.False(details.PlainHttpOn);
    }

    [Fact]
    public async Task A_confirmation_the_catalog_refuses_says_why_and_the_switch_stays_off()
    {
        var catalog = WithNas();
        catalog.PlainHttpAnswer = ServerProblem.PlainHttpWithHeaders;
        using var page = Page(catalog);
        var details = Open(page);
        details.PlainHttpOn = true;

        await details.ConfirmPlainHttpCommand.ExecuteAsync(null);

        Assert.False(details.PlainHttpOn);
        Assert.True(details.HasPlainHttpError);
        Assert.StartsWith("Headers hold secrets", details.PlainHttpError);
    }

    [Theory]
    [InlineData(LocalPlainHttpRefusal.NoOptIn, "Plain http goes only to this computer. Turn on “Allow plain http on the local network” under Advanced.")]
    [InlineData(LocalPlainHttpRefusal.NotLocal, "This address is not on the local network.")]
    [InlineData(LocalPlainHttpRefusal.Secret, "A server with a header value needs https.")]
    [InlineData(LocalPlainHttpRefusal.NotAllowed, "Plain http is not allowed here.")]
    public void Each_refusal_shows_its_own_text_in_the_status(LocalPlainHttpRefusal refusal, string text)
    {
        using var page = Page(WithNas(state: LocalServerState.NotAllowed, refusal: refusal));

        var nas = Server(page, "nas");

        Assert.Equal((text, ServerStatusKind.Warning, refusal), (nas.StatusText, nas.StatusKind, nas.PlainHttpRefusal));
    }

    [Theory]
    [InlineData(ServerProblem.PlainHttpWithHeaders, "Headers hold secrets, so a plain http address beyond this computer cannot have any. Use https or remove the headers.")]
    [InlineData(ServerProblem.PlainHttpNotLocal, "Plain http goes only to this computer or the local network. Use https for an address elsewhere.")]
    public void The_two_new_problems_have_their_words_on_the_sheet(ServerProblem problem, string text)
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.ValidateAnswer = problem;
        using var page = Page(catalog);
        page.AddServerCommand.Execute(null);
        var sheet = (ServerSheetViewModel)page.Sheet!;
        sheet.IsProgram = false;
        sheet.Url = "http://192.168.1.5/mcp";

        sheet.SaveCommand.Execute(null);

        Assert.Equal(text, sheet.ErrorText);
        Assert.DoesNotContain("save new", catalog.Calls);
    }

    [Theory]
    [InlineData(LocalPlainHttpRefusal.NoOptIn)]
    [InlineData(LocalPlainHttpRefusal.NotLocal)]
    [InlineData(LocalPlainHttpRefusal.Secret)]
    [InlineData(LocalPlainHttpRefusal.NotAllowed)]
    public async Task A_test_refused_for_plain_http_shows_the_refusal_not_a_generic_failure(LocalPlainHttpRefusal refusal)
    {
        var catalog = FakeLocalToolsCatalog.Scene();
        catalog.TestAnswer = new ServerTestInfo(TestOutcome.Failed, Array.Empty<string>(), "raw text from the host", refusal);
        using var page = Page(catalog);
        page.AddServerCommand.Execute(null);
        var sheet = (ServerSheetViewModel)page.Sheet!;
        sheet.Label = "nas";
        sheet.IsProgram = false;
        sheet.Url = "http://192.168.1.5:9/mcp";

        await sheet.TestCommand.ExecuteAsync(null);

        Assert.Equal(LocalServerViewModel.RefusalText(page.Strings, refusal), sheet.TestText);
        Assert.DoesNotContain("raw text", sheet.TestText);
    }

    [Theory]
    [InlineData("de", "Klartext-http ist nicht verschlüsselt. Jeder in Ihrem Netzwerk kann lesen und verändern, was zu diesem Server und von ihm kommt: die Werkzeugaufrufe, ihre Argumente und ihre Ergebnisse, die Dokumentinhalte enthalten können. Eine veränderte Antwort kann den Agenten lenken. Verwenden Sie https, wo der Server es anbietet.")]
    [InlineData("fr", "Le http en clair n’est pas chiffré. Toute personne de votre réseau peut lire et modifier ce qui va vers ce serveur et ce qui en vient : les appels d’outils, leurs arguments et leurs résultats, qui peuvent contenir le contenu de documents. Une réponse modifiée peut orienter l’agent. Utilisez https lorsque le serveur le propose.")]
    public void The_warning_is_translated_not_left_in_english(string language, string text) =>
        Assert.Equal(text, UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo(language)).McpPlainHttpWarning.Replace('\u00a0', ' '));
}
