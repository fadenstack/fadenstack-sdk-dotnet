using System.Text.Json;
using Fadenstack.Http;
using Fadenstack.Models;
using Fadenstack.Storage;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>
/// The tests that point <see cref="FadenstackPaths"/> at a temporary folder run alone: the roots are process-wide, and a
/// test running beside them would put its files there.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PathsCollection
{
    public const string Name = "FadenstackPaths";
}

/// <summary>Where the SDK keeps things (<see cref="FadenstackPaths"/>) and who the agent's user is (<see cref="AgentPrincipal"/>).</summary>
[Collection(PathsCollection.Name)]
public class StorageTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-paths-" + Guid.NewGuid());

    public void Dispose()
    {
        FadenstackPaths.RootForTests = null;
        if (Directory.Exists(_temporary))
        {
            Directory.Delete(_temporary, true);
        }
    }

    private static FadenstackPaths.Roots Windows() => FadenstackPaths.Resolve(FadenstackPaths.Os.Windows, _ => null, f => f switch
    {
        Environment.SpecialFolder.LocalApplicationData => @"C:\Users\ann\AppData\Local",
        Environment.SpecialFolder.ApplicationData => @"C:\Users\ann\AppData\Roaming",
        Environment.SpecialFolder.CommonApplicationData => @"C:\ProgramData",
        Environment.SpecialFolder.UserProfile => @"C:\Users\ann",
        _ => "",
    });

    private static FadenstackPaths.Roots Unix(FadenstackPaths.Os os, params (string Name, string Value)[] variables) =>
        FadenstackPaths.Resolve(os, name => variables.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault(), _ => "");

    [Fact]
    public void Windows_keeps_data_local_and_preferences_roaming()
    {
        var roots = Windows();
        var data = Path.Combine(@"C:\Users\ann\AppData\Local", "Fadenstack");
        Assert.Equal(data, roots.Data);
        Assert.Equal(Path.Combine(@"C:\Users\ann\AppData\Roaming", "Fadenstack"), roots.Settings);
        Assert.Equal(Path.Combine(data, "logs"), roots.Logs);
    }

    [Fact]
    public void MacOS_uses_application_support_and_library_logs()
    {
        var roots = Unix(FadenstackPaths.Os.MacOs, ("HOME", "/Users/ann"));
        var data = Path.Combine("/Users/ann", "Library", "Application Support", "Fadenstack");
        Assert.Equal(data, roots.Data);
        Assert.Equal(data, roots.Settings);
        Assert.Equal(Path.Combine("/Users/ann", "Library", "Logs", "Fadenstack"), roots.Logs);
    }

    [Fact]
    public void Linux_uses_the_home_defaults_without_xdg_variables()
    {
        var roots = Unix(FadenstackPaths.Os.Unix, ("HOME", "/home/ann"));
        Assert.Equal(Path.Combine("/home/ann", ".local", "share", "Fadenstack"), roots.Data);
        Assert.Equal(Path.Combine("/home/ann", ".config", "Fadenstack"), roots.Settings);
        Assert.Equal(Path.Combine("/home/ann", ".local", "state", "Fadenstack", "logs"), roots.Logs);
    }

    [Fact]
    public void Linux_follows_absolute_xdg_variables_and_ignores_relative_ones()
    {
        var set = Unix(FadenstackPaths.Os.Unix, ("HOME", "/home/ann"), ("XDG_DATA_HOME", "/srv/data"),
            ("XDG_CONFIG_HOME", "/srv/config"), ("XDG_STATE_HOME", "/srv/state"));
        Assert.Equal(Path.Combine("/srv/data", "Fadenstack"), set.Data);
        Assert.Equal(Path.Combine("/srv/config", "Fadenstack"), set.Settings);
        Assert.Equal(Path.Combine("/srv/state", "Fadenstack", "logs"), set.Logs);

        var relative = Unix(FadenstackPaths.Os.Unix, ("HOME", "/home/ann"), ("XDG_DATA_HOME", "data"),
            ("XDG_CONFIG_HOME", ""), ("XDG_STATE_HOME", "state/x"));
        Assert.Equal(Unix(FadenstackPaths.Os.Unix, ("HOME", "/home/ann")), relative);
    }

    [Fact]
    public void No_root_lies_in_a_machine_wide_folder()
    {
        var all = new[]
        {
            (Windows(), @"C:\Users\ann"),
            (Unix(FadenstackPaths.Os.MacOs, ("HOME", "/Users/ann")), "/Users/ann"),
            (Unix(FadenstackPaths.Os.Unix, ("HOME", "/home/ann")), "/home/ann"),
        };
        foreach (var (roots, home) in all)
        {
            foreach (var root in new[] { roots.Data, roots.Settings, roots.Logs })
            {
                Assert.StartsWith(home, root);
                Assert.DoesNotContain("ProgramData", root);
            }
        }
    }

    [Fact]
    public void A_user_and_the_logs_get_their_own_sanitised_folders()
    {
        FadenstackPaths.RootForTests = _temporary;
        var sessions = FadenstackPaths.User("../u:1", "sessions");
        Assert.Equal(Path.Combine(_temporary, "data", "users", "___u_1", "sessions"), sessions);
        Assert.True(Directory.Exists(sessions));
        Assert.NotEqual(FadenstackPaths.User("u-2", "sessions"), FadenstackPaths.User("u-1", "sessions"));

        Assert.Equal(Path.Combine(_temporary, "logs"), FadenstackPaths.Logs(null));
        Assert.Equal(Path.Combine(_temporary, "logs", "u-1"), FadenstackPaths.Logs("u-1"));
        Assert.Equal(Path.Combine(_temporary, "data", "tokens"), FadenstackPaths.Data("tokens"));
        Assert.Equal(Path.Combine(_temporary, "data", "tokens"), Auth.SecretProtection.DataDirectory("tokens"));
        Assert.Equal("_", FadenstackPaths.SafeName(""));
    }

    private static AgentProfile Profile(string json) => JsonSerializer.Deserialize<AgentProfile>(json, FadenstackJson.Options)!;

    [Fact]
    public void The_principal_is_read_from_the_profile()
    {
        var principal = Profile("""{"principal":{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1","device_id":"d-1"}}""").Principal;
        Assert.Equal(new AgentPrincipal { TenantId = "t-1", UserId = "u-1", AgentId = "a-1", DeviceId = "d-1" }, principal);
        Assert.Null(Profile("""{"principal":{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1"}}""").Principal!.DeviceId);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"principal":null}""")]
    [InlineData("""{"principal":"u-1"}""")]
    [InlineData("""{"principal":{"tenant_id":"t-1","agent_id":"a-1"}}""")]
    [InlineData("""{"principal":{"tenant_id":"t-1","user_id":"","agent_id":"a-1"}}""")]
    [InlineData("""{"principal":{"tenant_id":"t-1","user_id":7,"agent_id":"a-1"}}""")]
    public void Without_a_complete_principal_there_is_none(string json) => Assert.Null(Profile(json).Principal);
}
