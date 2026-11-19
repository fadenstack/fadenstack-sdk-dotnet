using System.Text.Json;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Storage;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// An app's name in the panel's texts (0.3.1): <see cref="AppDescription.DisplayNames"/> are for About and "Your data" only. They
/// go with the app's record in the person's folder, so another app shows this one in its language; the manifest, the records'
/// key and the record of tool calls keep <see cref="AppDescription.Name"/>.
/// </summary>
[Collection(PathsCollection.Name)]
public sealed class AppDisplayNamesTests : IDisposable
{
    private const string Principal = """{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1","device_id":null}""";
    private const string Chrome = "Chrome extension";
    private const string Word = "Word add-in";
    private static readonly Dictionary<string, string> ChromeNames = new() { ["de"] = "Chrome-Erweiterung", ["zh-Hans"] = "Chrome 扩展" };
    private static readonly Dictionary<string, string> WordNames = new() { ["de"] = "Word-Add-in", ["zh-Hans"] = "Word 加载项" };
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-appnames-" + Guid.NewGuid().ToString("N"));

    public AppDisplayNamesTests()
    {
        FadenstackPaths.RootForTests = _temporary;
        SecretProtection.ResetForTests();
        SecretProtection.Use(new FakeEncryptingProtector());
    }

    public void Dispose()
    {
        SecretProtection.ResetForTests();
        FadenstackPaths.RootForTests = null;
        if (Directory.Exists(_temporary))
        {
            Directory.Delete(_temporary, true);
        }
    }

    private static string Person => FadenstackPaths.UserFolder("u-1");

    private static FadenstackAgent Agent(Gateway gateway, AppDescription app) => gateway.Builder().DescribeApp(app).Build();

    [Fact]
    public async Task The_display_names_are_kept_with_the_apps_record_and_another_app_lists_them()
    {
        var gateway = new Gateway { Principal = Principal };
        using var word = Agent(gateway, new AppDescription { Name = Word, Version = "1", DisplayNames = WordNames });
        await word.StartAsync();
        MyDataApps.TryRecord(Person, Chrome, "chrome-assistant", DateTimeOffset.UtcNow, ChromeNames); // the Chrome extension, as its own process writes

        var apps = await word.MyDataAppsAsync();

        var thisApp = Assert.Single(apps, a => a.IsThisApp);
        var other = Assert.Single(apps, a => !a.IsThisApp);
        Assert.Equal(Word, thisApp.Name);
        Assert.Equal(WordNames, thisApp.DisplayNames);
        Assert.Equal(Chrome, other.Name);
        Assert.Equal(ChromeNames, other.DisplayNames);
        Assert.Equal("Chrome-Erweiterung", AppNames.DisplayName(other.Name, other.DisplayNames, "de-AT"));
        Assert.Equal("Chrome 扩展", AppNames.DisplayName(other.Name, other.DisplayNames, "zh-Hans"));
        Assert.Equal(Chrome, AppNames.DisplayName(other.Name, other.DisplayNames, "fr"));
        var stored = JsonDocument.Parse(File.ReadAllText(MyDataApps.PathOf(Person, Chrome))).RootElement;
        Assert.Equal(Chrome, stored.GetProperty("app").GetString());
        Assert.Equal("Chrome-Erweiterung", stored.GetProperty("displayNames").GetProperty("de").GetString());
    }

    [Fact]
    public async Task A_record_without_display_names_lists_the_name_and_the_latest_start_replaces_the_names()
    {
        var gateway = new Gateway { Principal = Principal };
        using var word = Agent(gateway, new AppDescription { Name = Word });
        await word.StartAsync();
        MyDataApps.TryRecord(Person, Chrome, "chrome-assistant", DateTimeOffset.UtcNow); // an older SDK's record: no field
        Assert.False(JsonDocument.Parse(File.ReadAllText(MyDataApps.PathOf(Person, Chrome))).RootElement.TryGetProperty("displayNames", out _));
        Assert.All(await word.MyDataAppsAsync(), a => Assert.Null(a.DisplayNames));

        MyDataApps.TryRecord(Person, Chrome, "chrome-assistant", DateTimeOffset.UtcNow, ChromeNames);
        Assert.Equal(ChromeNames, Assert.Single(await word.MyDataAppsAsync(), a => !a.IsThisApp).DisplayNames);
        MyDataApps.TryRecord(Person, Chrome, "chrome-assistant", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["de"] = "Chrome-Zusatz" });
        Assert.Equal("Chrome-Zusatz", Assert.Single(await word.MyDataAppsAsync(), a => !a.IsThisApp).DisplayNames!["de"]);
    }

    [Fact]
    public async Task A_record_with_a_field_this_sdk_does_not_know_still_reads_and_a_damaged_names_field_loses_only_the_names()
    {
        var gateway = new Gateway { Principal = Principal };
        using var word = Agent(gateway, new AppDescription { Name = Word });
        await word.StartAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(MyDataApps.PathOf(Person, Chrome))!);
        File.WriteAllText(MyDataApps.PathOf(Person, Chrome),
            """{"app":"Chrome extension","agents":["chrome-assistant"],"firstSeen":"2026-11-01T00:00:00+00:00","lastSeen":"2026-11-02T00:00:00+00:00","futureField":{"x":1},"displayNames":{"de":"Chrome-Erweiterung","xx":"x","fr":"  "}}""");

        var other = Assert.Single(await word.MyDataAppsAsync(), a => !a.IsThisApp);

        Assert.Equal((Chrome, true), (other.Name, other.Recorded));
        Assert.Equal(new Dictionary<string, string> { ["de"] = "Chrome-Erweiterung" }, other.DisplayNames); // unknown key and blank dropped
    }

    [Fact]
    public async Task The_manifest_and_the_apps_name_keep_Name_and_never_carry_a_display_name()
    {
        var gateway = new Gateway { Principal = Principal };
        using var agent = Agent(gateway, new AppDescription { Name = Chrome, Version = "1.3.0", DisplayNames = ChromeNames });
        await agent.StartAsync();
        var sent = await Until(() => gateway.Manifests.FirstOrDefault());

        Assert.Equal(Chrome, agent.App!.Name);
        Assert.Equal(ChromeNames, agent.AppDisplayNames);
        Assert.Equal((Chrome, "1.3.0"), (sent.Json.GetProperty("app").GetProperty("name").GetString(), sent.Json.GetProperty("app").GetProperty("version").GetString()));
        var raw = sent.Json.GetRawText();
        Assert.DoesNotContain("Chrome-Erweiterung", raw); // the German display name is not in the manifest
        Assert.DoesNotContain("Chrome 扩展", raw);
        Assert.DoesNotContain("displayNames", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Chrome, Assert.Single(await agent.MyDataAppsAsync()).Name); // the list's name is the name
    }

    [Fact]
    public void Display_names_are_cleaned_and_a_region_counts_as_its_language()
    {
        Assert.Null(AppNames.CleanDisplayNames(null));
        Assert.Null(AppNames.CleanDisplayNames(new Dictionary<string, string> { ["de"] = "  ", ["xx"] = "x" }));
        Assert.Equal(new Dictionary<string, string> { ["es"] = "Extensión" },
            AppNames.CleanDisplayNames(new Dictionary<string, string> { ["de"] = "  ", ["fr"] = null!, ["xx"] = "x", ["es"] = " Extensión " }));
        Assert.Equal(200, AppNames.CleanDisplayNames(new Dictionary<string, string> { ["en"] = new string('a', 300) })!["en"].Length);
        Assert.Equal(Chrome, AppNames.DisplayName(Chrome, ChromeNames, "fr"));
        Assert.Equal(Chrome, AppNames.DisplayName(Chrome, null, "de"));
        Assert.Equal("Chrome-Erweiterung", AppNames.DisplayName(Chrome, ChromeNames, "de-AT"));
        Assert.Equal("Chrome-Erweiterung", AppNames.DisplayName(Chrome, ChromeNames, "DE"));
        Assert.Equal("Chrome add-on", AppNames.DisplayName(Chrome, new Dictionary<string, string> { ["en"] = "Chrome add-on" }, "en"));
        Assert.Equal(Chrome, AppNames.DisplayName(Chrome, ChromeNames, null));
    }

    private static async Task<Recorded> Until(Func<Recorded?> find)
    {
        for (var i = 0; i < 200; i++)
        {
            if (find() is { } found)
            {
                return found;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Nothing arrived.");
    }
}
