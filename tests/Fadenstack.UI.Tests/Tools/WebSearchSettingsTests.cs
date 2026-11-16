using System.Globalization;
using System.Reflection;
using Fadenstack.UI.Tools;
using Fadenstack.WebSearch;
using Xunit;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>
/// The web search settings sheet (sdk/51 F06): the three choices, the key that goes to the key store and is never read back into
/// the box, the address check, Save and Cancel, the typed key that does not outlive the sheet, and the row on the Tools page that opens it.
/// </summary>
public class WebSearchSettingsTests
{
    private static WebSearchSettingsViewModel Sheet(WebSearchFixture fixture, Func<UiStrings>? strings = null) => new(fixture.Store, new FakeUi(), strings);

    [Fact]
    public void It_starts_off_for_a_person_who_has_not_set_it_up_and_shows_what_the_store_keeps_otherwise()
    {
        using var fresh = new WebSearchFixture();
        using var off = Sheet(fresh);
        Assert.Equal(WebSearchChoice.Off, off.Choice);
        Assert.True(off.IsOff);
        Assert.False(off.IsBrave);
        Assert.False(off.IsSearXng);
        Assert.Equal("", off.Address);

        using var brave = new WebSearchFixture().WithBrave();
        using var kept = Sheet(brave);
        Assert.True(kept.IsBrave);

        using var searx = new WebSearchFixture();
        searx.Store.Save(new WebSearchSettings { Provider = WebSearchProvider.SearXng, SearXngUrl = new Uri("https://search.example.org/") });
        using var named = Sheet(searx);
        Assert.True(named.IsSearXng);
        Assert.Equal("https://search.example.org/", named.Address);
    }

    [Fact]
    public void A_pick_sets_the_choice_and_an_unchecked_radio_button_changes_nothing()
    {
        using var fixture = new WebSearchFixture();
        using var sheet = Sheet(fixture);
        var seen = new List<string?>();
        sheet.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        sheet.IsBrave = true;
        Assert.Equal(WebSearchChoice.Brave, sheet.Choice);
        Assert.Contains(nameof(WebSearchSettingsViewModel.IsOff), seen);
        Assert.Contains(nameof(WebSearchSettingsViewModel.IsBrave), seen);

        sheet.IsOff = false; // the radio button that was left reports false after the pick
        Assert.Equal(WebSearchChoice.Brave, sheet.Choice);

        sheet.IsSearXng = true;
        Assert.Equal(WebSearchChoice.SearXng, sheet.Choice);
    }

    [Fact]
    public void Saving_Off_writes_the_choice_and_closes_the_sheet()
    {
        using var fixture = new WebSearchFixture().WithBrave();
        using var sheet = Sheet(fixture);
        var closed = 0;
        sheet.Closed += (_, _) => closed++;

        sheet.IsOff = true;
        sheet.SaveCommand.Execute(null);

        Assert.Equal(WebSearchProvider.None, fixture.Store.Settings.Provider);
        Assert.True(fixture.Store.HasBraveKey); // turning it off does not throw the key away
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Brave_needs_a_key_and_nothing_is_written_without_one()
    {
        using var fixture = new WebSearchFixture();
        using var sheet = Sheet(fixture);
        var closed = 0;
        sheet.Closed += (_, _) => closed++;

        sheet.IsBrave = true;
        sheet.SaveCommand.Execute(null);

        Assert.True(sheet.HasError);
        Assert.Equal("Enter your Brave API key, or pick another choice.", sheet.ErrorText);
        Assert.Equal(WebSearchProvider.None, fixture.Store.Settings.Provider);
        Assert.False(fixture.Store.HasBraveKey);
        Assert.Equal(0, closed);

        sheet.Key = "   ";
        sheet.SaveCommand.Execute(null);
        Assert.True(sheet.HasError); // blanks are not a key

        sheet.IsOff = true; // another choice clears the problem
        Assert.False(sheet.HasError);
    }

    [Fact]
    public void A_typed_key_goes_to_the_key_store_and_is_gone_from_the_sheet()
    {
        using var fixture = new WebSearchFixture();
        using var sheet = Sheet(fixture);
        sheet.IsBrave = true;
        sheet.Key = "  BSA-secret-key  ";
        Assert.True(sheet.ShowsKeyInput);

        sheet.SaveCommand.Execute(null);

        Assert.Equal(WebSearchProvider.Brave, fixture.Store.Settings.Provider);
        Assert.True(fixture.Store.HasBraveKey);
        Assert.Equal(["BSA-secret-key"], fixture.Protector.Protected);
        Assert.Equal("", sheet.Key);
        Assert.False(File.ReadAllText(Path.Combine(fixture.Folder, "preferences", "web-search.json")).Contains("BSA-secret-key", StringComparison.Ordinal));
        Assert.True(sheet.ShowsKeptKey); // what the sheet shows now is "kept", never the key
        Assert.False(sheet.ShowsKeyInput);
    }

    [Fact]
    public void A_kept_key_is_never_read_back_into_the_box_and_Save_keeps_it()
    {
        using var fixture = new WebSearchFixture().WithBrave("BSA-kept-key");
        using var sheet = Sheet(fixture);

        Assert.Equal("", sheet.Key);
        Assert.True(sheet.ShowsKeptKey);
        Assert.False(sheet.ShowsKeyInput);
        Assert.True(sheet.ShowsReplaceKey);
        Assert.False(sheet.ShowsRemovedKey);
        Assert.DoesNotContain("BSA-kept-key", new[] { sheet.Key, sheet.Address, sheet.ErrorText, sheet.KeptWhereText, sheet.PrivacyText, sheet.SavedText });

        var before = fixture.Protector.Protected.Count;
        sheet.SaveCommand.Execute(null);

        Assert.False(sheet.HasError);
        Assert.True(fixture.Store.HasBraveKey);
        Assert.Equal(before, fixture.Protector.Protected.Count); // the key store was not written again
        Assert.Equal(WebSearchProvider.Brave, fixture.Store.Settings.Provider);
    }

    [Fact]
    public void Replace_key_opens_an_empty_box_and_the_new_key_replaces_the_old_one()
    {
        using var fixture = new WebSearchFixture().WithBrave("old-key");
        using var sheet = Sheet(fixture);

        sheet.ReplaceKeyCommand.Execute(null);

        Assert.True(sheet.ShowsKeyInput);
        Assert.False(sheet.ShowsKeptKey);
        Assert.Equal("", sheet.Key);

        sheet.Key = "new-key";
        sheet.SaveCommand.Execute(null);
        Assert.Equal("new-key", fixture.Protector.Protected[^1]);
        Assert.True(fixture.Store.HasBraveKey);
    }

    [Fact]
    public void Remove_key_takes_effect_on_Save_and_Cancel_keeps_the_key()
    {
        using var fixture = new WebSearchFixture().WithBrave();
        using var sheet = Sheet(fixture);

        sheet.RemoveKeyCommand.Execute(null);
        Assert.True(sheet.ShowsRemovedKey);
        Assert.False(sheet.ShowsKeptKey);
        Assert.True(sheet.ShowsReplaceKey);
        Assert.True(fixture.Store.HasBraveKey); // nothing is written before Save

        sheet.SaveCommand.Execute(null); // Brave without a key is refused
        Assert.True(sheet.HasError);
        Assert.True(fixture.Store.HasBraveKey);

        sheet.IsOff = true;
        sheet.SaveCommand.Execute(null);
        Assert.False(fixture.Store.HasBraveKey);
        Assert.Equal(WebSearchProvider.None, fixture.Store.Settings.Provider);

        using var other = new WebSearchFixture().WithBrave();
        using var cancelled = Sheet(other);
        cancelled.RemoveKeyCommand.Execute(null);
        cancelled.CancelCommand.Execute(null);
        Assert.True(other.Store.HasBraveKey);
        Assert.True(cancelled.ShowsKeptKey);
    }

    [Fact]
    public void Replace_key_after_Remove_key_types_a_new_key()
    {
        using var fixture = new WebSearchFixture().WithBrave();
        using var sheet = Sheet(fixture);

        sheet.RemoveKeyCommand.Execute(null);
        sheet.ReplaceKeyCommand.Execute(null);
        Assert.True(sheet.ShowsKeyInput);
        Assert.False(sheet.ShowsRemovedKey);

        sheet.Key = "again";
        sheet.SaveCommand.Execute(null);
        Assert.True(fixture.Store.HasBraveKey);
        Assert.Equal("again", fixture.Protector.Protected[^1]);
    }

    [Theory]
    [InlineData("", "Enter the address of your SearXNG instance.")]
    [InlineData("   ", "Enter the address of your SearXNG instance.")]
    [InlineData("search.example.org", "Use an https address, or http on this computer only, without a user name or password.")]
    [InlineData("http://search.example.org", "Use an https address, or http on this computer only, without a user name or password.")]
    [InlineData("https://user:secret@search.example.org", "Use an https address, or http on this computer only, without a user name or password.")]
    [InlineData("ftp://search.example.org", "Use an https address, or http on this computer only, without a user name or password.")]
    public void A_SearXNG_address_that_is_missing_or_not_allowed_is_refused_with_a_clear_message(string address, string message)
    {
        using var fixture = new WebSearchFixture();
        using var sheet = Sheet(fixture);
        var closed = 0;
        sheet.Closed += (_, _) => closed++;
        sheet.IsSearXng = true;
        sheet.Address = address;

        sheet.SaveCommand.Execute(null);

        Assert.True(sheet.HasError);
        Assert.Equal(message, sheet.ErrorText);
        Assert.Equal(WebSearchProvider.None, fixture.Store.Settings.Provider);
        Assert.Equal(0, closed);

        sheet.Address = "https://search.example.org"; // typing again clears the problem
        Assert.False(sheet.HasError);
    }

    [Theory]
    [InlineData("https://search.example.org/")]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://127.0.0.1:8888/")]
    public void A_SearXNG_address_that_is_allowed_is_saved(string address)
    {
        using var fixture = new WebSearchFixture();
        using var sheet = Sheet(fixture);
        sheet.IsSearXng = true;
        sheet.Address = "  " + address + "  ";

        sheet.SaveCommand.Execute(null);

        Assert.False(sheet.HasError);
        Assert.Equal(WebSearchProvider.SearXng, fixture.Store.Settings.Provider);
        Assert.Equal(address, fixture.Store.Settings.SearXngUrl!.AbsoluteUri);
    }

    [Fact]
    public void Cancel_writes_nothing_forgets_the_typed_key_and_shows_what_the_store_keeps()
    {
        using var fixture = new WebSearchFixture();
        using var sheet = Sheet(fixture);
        var closed = 0;
        sheet.Closed += (_, _) => closed++;
        sheet.IsBrave = true;
        sheet.Key = "typed-and-dropped";
        sheet.IsSearXng = true;
        sheet.Address = "https://search.example.org";

        sheet.CancelCommand.Execute(null);

        Assert.Equal("", sheet.Key);
        Assert.Equal(WebSearchChoice.Off, sheet.Choice);
        Assert.Equal("", sheet.Address);
        Assert.Equal(WebSearchProvider.None, fixture.Store.Settings.Provider);
        Assert.False(fixture.Store.HasBraveKey);
        Assert.Empty(fixture.Protector.Protected);
        Assert.False(File.Exists(Path.Combine(fixture.Folder, "preferences", "web-search.json")));
        Assert.Equal(1, closed);
    }

    [Fact]
    public void The_typed_key_is_cleared_when_the_sheet_is_disposed()
    {
        using var fixture = new WebSearchFixture();
        var sheet = Sheet(fixture);
        sheet.IsBrave = true;
        sheet.Key = "typed";

        sheet.Dispose();

        Assert.Equal("", sheet.Key);
        sheet.Key = "again";
        sheet.SaveCommand.Execute(null); // a disposed sheet does nothing
        Assert.False(fixture.Store.HasBraveKey);
        sheet.Dispose(); // twice is fine
    }

    [Fact]
    public void A_line_says_where_the_key_is_kept_and_that_the_query_leaves_the_organisation()
    {
        using var disk = new WebSearchFixture(encrypts: true);
        using var onDisk = Sheet(disk);
        Assert.Equal("The key is kept encrypted in this device\u2019s key store and is never shown again.", onDisk.KeptWhereText);
        Assert.Contains("outside your organisation", onDisk.PrivacyText);
        Assert.Contains("Auto", onDisk.PrivacyText);

        using var memory = new WebSearchFixture(encrypts: false);
        using var session = Sheet(memory);
        Assert.Contains("this session only", session.KeptWhereText);
        session.IsBrave = true;
        session.Key = "mem-key";
        session.SaveCommand.Execute(null);
        Assert.True(memory.Store.HasBraveKey);
        Assert.Empty(memory.Protector.Protected); // nothing was written to disk
    }

    [Fact]
    public void A_key_that_goes_away_in_the_store_is_followed()
    {
        using var fixture = new WebSearchFixture().WithBrave();
        using var sheet = Sheet(fixture);
        Assert.True(sheet.ShowsKeptKey);

        fixture.Store.SetBraveKey(null);

        Assert.False(sheet.ShowsKeptKey);
        Assert.True(sheet.ShowsKeyInput);
    }

    [Fact]
    public void Retext_says_the_texts_again_in_the_new_language()
    {
        var language = "en";
        using var fixture = new WebSearchFixture();
        using var sheet = Sheet(fixture, () => UiStrings.For(CultureInfo.GetCultureInfo(language)));
        sheet.IsBrave = true;
        sheet.SaveCommand.Execute(null); // an error that is said in the language
        var seen = new List<string?>();
        sheet.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        Assert.Equal("Web search", sheet.Title);
        Assert.StartsWith("Enter your Brave", sheet.ErrorText);

        language = "de";
        sheet.Retext();

        Assert.Contains(string.Empty, seen);
        Assert.Equal("Websuche", sheet.Title);
        Assert.StartsWith("Geben Sie Ihren Brave", sheet.ErrorText);
    }

    // ---- the Tools page -----------------------------------------------------------------------------------------------

    private static ToolsViewModel Page(WebSearchFixture? fixture, Func<UiStrings>? strings = null) =>
        new(FakeLocalToolsCatalog.Scene(), new FakeFiles(), new FakeUi(), strings, fixture?.Store);

    [Fact]
    public void An_app_without_web_search_has_no_row()
    {
        using var page = Page(null);
        Assert.False(page.ShowsWebSearch);
        page.OpenWebSearchCommand.Execute(null);
        Assert.Null(page.Sheet);
    }

    [Fact]
    public void The_row_names_the_state_and_offers_Set_up_then_Change()
    {
        using var fixture = new WebSearchFixture();
        using var page = Page(fixture);
        Assert.True(page.ShowsWebSearch);
        Assert.Equal("Web search", page.WebSearchName);
        Assert.Equal("Off", page.WebSearchState);
        Assert.Equal("Set up", page.WebSearchButtonText);

        fixture.Store.Save(new WebSearchSettings { Provider = WebSearchProvider.Brave });
        Assert.Equal("Brave", page.WebSearchState);
        Assert.Equal("Change", page.WebSearchButtonText);

        fixture.Store.Save(new WebSearchSettings { Provider = WebSearchProvider.SearXng, SearXngUrl = new Uri("https://search.example.org:8443/") });
        Assert.Equal("SearXNG \u00B7 search.example.org:8443", page.WebSearchState);
    }

    [Fact]
    public void The_button_opens_the_sheet_and_Save_closes_it_and_the_row_follows()
    {
        using var fixture = new WebSearchFixture();
        using var page = Page(fixture);

        page.OpenWebSearchCommand.Execute(null);
        var sheet = Assert.IsType<WebSearchSettingsViewModel>(page.Sheet);
        Assert.True(page.HasSheet);

        sheet.IsSearXng = true;
        sheet.Address = "https://search.example.org";
        sheet.SaveCommand.Execute(null);

        Assert.Null(page.Sheet);
        Assert.False(page.HasSheet);
        Assert.Equal("SearXNG \u00B7 search.example.org", page.WebSearchState);
        Assert.Equal("Change", page.WebSearchButtonText);
    }

    [Fact]
    public void Cancel_and_Esc_close_the_sheet_and_a_closed_sheet_forgets_the_typed_key()
    {
        using var fixture = new WebSearchFixture();
        using var page = Page(fixture);
        page.OpenWebSearchCommand.Execute(null);
        var sheet = (WebSearchSettingsViewModel)page.Sheet!;
        sheet.IsBrave = true;
        sheet.Key = "typed";

        page.CloseTopCommand.Execute(null);

        Assert.Null(page.Sheet);
        Assert.Equal("", sheet.Key);
        Assert.False(fixture.Store.HasBraveKey);

        page.OpenWebSearchCommand.Execute(null);
        var second = (WebSearchSettingsViewModel)page.Sheet!;
        second.IsBrave = true;
        second.Key = "typed again";
        second.CancelCommand.Execute(null);
        Assert.Null(page.Sheet);
        Assert.Equal("", second.Key);
    }

    [Fact]
    public void Disposing_the_page_forgets_the_key_typed_in_its_open_sheet()
    {
        using var fixture = new WebSearchFixture();
        var page = Page(fixture);
        page.OpenWebSearchCommand.Execute(null);
        var sheet = (WebSearchSettingsViewModel)page.Sheet!;
        sheet.IsBrave = true;
        sheet.Key = "typed";

        page.Dispose();

        Assert.Equal("", sheet.Key);
    }

    [Fact]
    public void The_filter_narrows_the_row_by_its_name_and_Retext_says_it_in_the_new_language()
    {
        var language = "en";
        using var fixture = new WebSearchFixture();
        using var page = Page(fixture, () => UiStrings.For(CultureInfo.GetCultureInfo(language)));

        page.Filter = "read_page";
        Assert.False(page.ShowsWebSearch);
        page.Filter = "SEARCH";
        Assert.True(page.ShowsWebSearch);
        page.Filter = "";

        language = "de";
        page.Retext();
        Assert.Equal("Websuche", page.WebSearchName);
        Assert.Equal("Aus", page.WebSearchState);
        Assert.Equal("Einrichten", page.WebSearchButtonText);

        page.OpenWebSearchCommand.Execute(null);
        language = "fr";
        page.Retext();
        Assert.Equal("Recherche web", ((WebSearchSettingsViewModel)page.Sheet!).Title);
    }

    // ---- the texts ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void Every_web_search_text_has_a_value_in_every_language(string language)
    {
        var strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
        var english = UiStrings.For(CultureInfo.GetCultureInfo("en"));
        var members = typeof(UiStrings).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.StartsWith("WebSearch", StringComparison.Ordinal) && m is PropertyInfo or MethodInfo { IsSpecialName: false })
            .Select(m => m.Name).Distinct().ToList();

        Assert.True(members.Count >= 30, "the web search members were not found");
        foreach (var name in members)
        {
            Assert.True(strings.Find(name) is { Length: > 0 }, $"{language}: no text for {name}");
        }
        Assert.Contains("search.example.org", strings.WebSearchStateSearxng("search.example.org"));
        if (language != "en")
        {
            // A translation, not the English text (a brand name or a mark alone may stay).
            var same = members.Where(n => strings.Find(n) == english.Find(n)).ToList();
            Assert.True(same.Count <= 4, $"{language}: too many texts equal the English: {string.Join(", ", same)}");
        }
    }
}
