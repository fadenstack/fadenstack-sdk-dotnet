using Fadenstack.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The person's own AI keeps its data in <c>users/standalone/</c>; a governed server that names a user <c>standalone</c> must not get
/// that folder. ASVS 5.0 V8 (data protection: separation of one person's data from another's).
/// </summary>
[Collection(PathsCollection.Name)]
public class StandaloneFolderTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-standalone-" + Guid.NewGuid());

    public StandaloneFolderTests() => FadenstackPaths.RootForTests = _temporary;

    public void Dispose()
    {
        FadenstackPaths.RootForTests = null;
        if (Directory.Exists(_temporary))
        {
            Directory.Delete(_temporary, true);
        }
    }

    private string Users(params string[] parts) => Path.Combine(new[] { _temporary, "data", "users" }.Concat(parts).ToArray());

    private static Models.AgentPrincipal Governed(string user) => System.Text.Json.JsonSerializer
        .Deserialize<Models.AgentProfile>($$$"""{"principal":{"tenant_id":"t-1","user_id":"{{{user}}}","agent_id":"a-1"}}""", Http.FadenstackJson.Options)!.Principal!;

    [Fact]
    public void Standalone_keeps_its_folder()
    {
        Assert.Equal(Users("standalone"), FadenstackPaths.UserFolder(LocalAgentBackend.StandaloneId));
        Assert.Equal(Users("standalone"), FadenstackPaths.User(LocalAgentBackend.StandaloneId));
        Assert.Equal(Users("standalone", "keys"), FadenstackPaths.User(LocalAgentBackend.StandaloneId, "keys"));
    }

    [Theory]
    [InlineData("standalone")]
    [InlineData("Standalone")]
    [InlineData("STANDALONE")]
    public void A_governed_user_named_standalone_lands_elsewhere(string user)
    {
        var principal = Governed(user);
        var folder = FadenstackPaths.UserFolder(principal.UserId);
        Assert.False(string.Equals(Users("standalone"), folder, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Users("governed." + user), folder);
        Assert.Equal(folder, FadenstackPaths.UserFolder(Governed(user).UserId)); // deterministic
        Assert.Equal(Path.Combine(folder, "sessions"), FadenstackPaths.User(principal.UserId, "sessions"));
        Assert.False(Directory.Exists(Users("standalone")));
    }

    [Theory]
    [InlineData("standalone ")]
    [InlineData("standalone.")]
    [InlineData("stand/alone")]
    [InlineData("u-1")]
    public void Another_user_id_keeps_its_safe_name(string user)
    {
        Assert.Equal(user, Governed(user).UserId);
        Assert.Equal(Users(FadenstackPaths.SafeName(user)), FadenstackPaths.UserFolder(Governed(user).UserId));
        Assert.False(string.Equals(Users("standalone"), FadenstackPaths.UserFolder(Governed(user).UserId), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_safe_name_can_equal_the_governed_folder()
    {
        // SafeName turns every dot into an underscore, so a folder name with a dot comes from no id's safe name.
        Assert.DoesNotContain('.', FadenstackPaths.SafeName("governed.standalone"));
    }

    [Fact]
    public void Deleting_the_governed_standalone_user_leaves_the_persons_own_folder()
    {
        File.WriteAllText(Path.Combine(FadenstackPaths.User("standalone", "keys"), "k"), "own");
        var governed = FadenstackPaths.User(Governed("standalone").UserId, "sessions");
        Assert.True(FadenstackPaths.DeleteUnderData(Path.GetDirectoryName(governed)!));
        Assert.True(File.Exists(Path.Combine(Users("standalone", "keys"), "k")));
    }

    [Fact]
    public void The_folder_standalone_still_does_not_count_as_a_person_for_the_move_from_before_0_3_0()
    {
        FadenstackPaths.User("standalone");
        File.WriteAllText(Path.Combine(FadenstackPaths.Data("sessions"), "s1.jsonl"), "x");
        UserDataMigration.Adopt(Governed("standalone").UserId, NullLogger.Instance);
        Assert.True(File.Exists(Path.Combine(Users("governed.standalone", "sessions"), "s1.jsonl"))); // the governed person adopts them
        Assert.False(Directory.Exists(Users("standalone", "sessions")));
    }

    [Fact]
    public void A_standalone_start_never_takes_the_older_data()
    {
        File.WriteAllText(Path.Combine(FadenstackPaths.Data("sessions"), "s1.jsonl"), "x");
        UserDataMigration.Adopt(LocalAgentBackend.StandaloneId, NullLogger.Instance);
        Assert.True(File.Exists(Path.Combine(FadenstackPaths.Data("sessions"), "s1.jsonl")));
    }

    [Fact]
    public void A_governed_folder_counts_as_a_person_so_nothing_moves_again()
    {
        FadenstackPaths.User(Governed("standalone").UserId);
        File.WriteAllText(Path.Combine(FadenstackPaths.Data("sessions"), "s1.jsonl"), "x");
        UserDataMigration.Adopt("u-1", NullLogger.Instance);
        Assert.True(File.Exists(Path.Combine(FadenstackPaths.Data("sessions"), "s1.jsonl")));
    }
}
