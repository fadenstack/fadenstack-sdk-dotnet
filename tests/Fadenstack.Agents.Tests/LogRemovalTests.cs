using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Logging;
using Fadenstack.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>"Remove my data" takes the app's own log files (this app's with ThisApp, every file of the user's log folder with AllApps).</summary>
[Collection(PathsCollection.Name)]
public sealed class LogRemovalTests : IDisposable
{
    private const string Principal = """{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1","device_id":null}""";
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-logremoval-" + Guid.NewGuid().ToString("N"));

    public LogRemovalTests()
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

    private static FadenstackAgent Agent() => FadenstackAgent.CreateBuilder()
        .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"),
            o => o.HttpClient = new HttpClient(new Gateway { Principal = Principal }.Server))
        .UseAgent(Gateway.Slug)
        .Build();

    private static FadenstackFileLoggerProvider Provider(string app, ManualResetEventSlim? gate = null) =>
        new(app, null, () => DateTimeOffset.Parse("2026-10-28T09:30:00Z"), gate);

    private static void Write(FadenstackFileLoggerProvider provider, string text) =>
        provider.CreateLogger("c").Log(LogLevel.Information, new EventId(1), text, null, (s, _) => s);

    private static string Logs => FadenstackPaths.LogRoot;

    private static string[] Names(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).OrderBy(n => n).ToArray() : Array.Empty<string>();

    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            Thread.Sleep(20);
        }
    }

    private static string Other(string name)
    {
        Directory.CreateDirectory(Logs);
        var path = Path.Combine(Logs, name);
        File.WriteAllText(path, "another app's line\n");
        return path;
    }

    [Fact]
    public async Task ThisApp_removes_the_providers_files_and_logging_goes_on_in_a_new_file()
    {
        using var provider = Provider("word");
        Write(provider, "before");
        WaitFor(() => Names(Logs).Contains("word-20261028.log"));
        var other = Other("desktop-20261028.log");
        using var agent = Agent();

        var deletion = await agent.DeleteMyDataAsync(DataScope.ThisApp);

        Assert.Equal(1, deletion.LogFilesRemoved);
        Assert.Equal(0, deletion.LogFilesLeft);
        Assert.True(File.Exists(other)); // another app's log is not this app's
        Assert.DoesNotContain("word-20261028.log", Names(Logs));
        Write(provider, "after");
        WaitFor(() => Names(Logs).Contains("word-20261028.log"));
        provider.Dispose();
        var text = File.ReadAllText(Path.Combine(Logs, "word-20261028.log"));
        Assert.Contains("after", text);
        Assert.DoesNotContain("before", text);
    }

    [Fact]
    public async Task AllApps_removes_every_other_file_in_the_log_folder_and_the_folders_below_it()
    {
        using var provider = Provider("word");
        Write(provider, "before");
        WaitFor(() => Names(Logs).Contains("word-20261028.log"));
        Other("desktop-20261028.log");
        Other("desktop-20261028.log.1");
        var perUser = Path.Combine(Logs, "u-1");
        Directory.CreateDirectory(perUser);
        File.WriteAllText(Path.Combine(perUser, "agent-20261028.log"), "x");
        using var agent = Agent();

        var deletion = await agent.DeleteMyDataAsync(DataScope.AllApps);

        Assert.Equal(4, deletion.LogFilesRemoved);
        Assert.Equal(0, deletion.LogFilesLeft);
        Assert.Empty(Names(Logs));
        Assert.Empty(Names(perUser));
    }

    [Fact]
    public async Task What_was_queued_before_the_removal_is_not_written_after_it()
    {
        using var gate = new ManualResetEventSlim(false);
        using var provider = Provider("word", gate);
        Write(provider, "queued one");
        Write(provider, "queued two");
        using var agent = Agent();

        var deletion = await agent.DeleteMyDataAsync(DataScope.ThisApp);
        gate.Set();
        Write(provider, "after the removal");
        WaitFor(() => Names(Logs).Contains("word-20261028.log"));
        provider.Dispose();

        Assert.Equal(0, deletion.LogFilesRemoved); // nothing was written yet
        var text = File.ReadAllText(Path.Combine(Logs, "word-20261028.log"));
        Assert.Contains("after the removal", text);
        Assert.DoesNotContain("queued", text);
    }

    [Fact]
    public async Task A_file_another_process_holds_without_sharing_is_counted_as_left_where_the_OS_refuses_its_deletion()
    {
        var path = Other("desktop-20261028.log");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); // no FileShare.Delete
        using var agent = Agent();

        var deletion = await agent.DeleteMyDataAsync(DataScope.AllApps);

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            Assert.Equal(1, deletion.LogFilesLeft);
            Assert.True(File.Exists(path));
        }
        else
        {
            Assert.Equal(0, deletion.LogFilesLeft); // unlink works on an open file
        }
    }
}
