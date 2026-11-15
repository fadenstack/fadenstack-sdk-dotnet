using Fadenstack.Auth;
using Fadenstack.Mcp;

namespace Fadenstack.Mcp.Tests;

/// <summary>
/// One list kept by several apps (Word and Excel, Office and the desktop share <c>users/standalone/mcp</c>; Office's windows share
/// one store): no app's change is lost to another's save, and every host's module follows a list changed elsewhere.
/// </summary>
public sealed class McpSharedListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden-mcpshared-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class Reversing : ISecretProtector
    {
        public bool Encrypts => true;

        public byte[] Protect(byte[] data) => Enumerable.Reverse(data).ToArray();

        public byte[] Unprotect(byte[] data) => Enumerable.Reverse(data).ToArray();
    }

    /// <summary>Another app's store on the same file: a store of its own, as another process has.</summary>
    private McpServerStore App() => new(Path.Combine(_root, "standalone"), new Reversing());

    [Fact]
    public void Two_apps_saving_one_list_keep_both_changes()
    {
        var word = App();
        var excel = App();

        word.Add(McpStoreTests.Program("alpha"));
        excel.Add(McpStoreTests.Program("beta"));

        Assert.Equal(["alpha", "beta"], App().Entries.Select(e => e.Label));
        Assert.Equal(["alpha", "beta"], excel.Entries.Select(e => e.Label));
        Assert.True(word.Reload());
        Assert.Equal(["alpha", "beta"], word.Entries.Select(e => e.Label));
    }

    [Fact]
    public void A_server_removed_by_another_app_does_not_come_back_with_the_next_save()
    {
        var word = App();
        word.Add(McpStoreTests.Program("alpha"));
        word.Add(McpStoreTests.Program("beta"));
        var excel = App();

        Assert.True(excel.Remove("alpha"));
        Assert.True(word.SetEnabled("beta", false));

        var kept = Assert.Single(App().Entries);
        Assert.Equal(("beta", false), (kept.Label, kept.Enabled));
        Assert.Equal(["beta"], word.Entries.Select(e => e.Label));
    }

    [Fact]
    public void A_change_to_a_server_another_app_removed_finds_it_gone()
    {
        var word = App();
        word.Add(McpStoreTests.Program("alpha"));
        App().Remove("alpha");

        Assert.Equal(McpProblem.LabelMissing, word.Update("alpha", McpStoreTests.Program("alpha") with { CallTimeoutSeconds = 30 }).Problem);
        Assert.False(word.SetEnabled("alpha", false));
        Assert.Empty(App().Entries);
    }

    [Fact]
    public void A_label_another_app_took_meanwhile_is_refused()
    {
        var word = App();
        App().Add(McpStoreTests.Program("alpha"));

        Assert.False(word.Add(McpStoreTests.Address("alpha")).IsValid);
        Assert.True(Assert.Single(App().Entries).IsProgram);
    }

    [Fact]
    public void Reading_again_after_the_stores_own_save_finds_nothing_new()
    {
        var word = App();
        word.Add(McpStoreTests.Program("alpha"));
        var changed = 0;
        word.Changed += (_, _) => changed++;

        Assert.False(word.Reload());
        Assert.Equal(0, changed);
    }

    [Fact]
    public void A_list_deleted_by_another_app_is_empty_when_read_again()
    {
        var word = App();
        word.Add(McpStoreTests.Program("alpha"));
        File.Delete(Path.Combine(_root, "standalone", McpServerStore.FileName));

        Assert.True(word.Reload());
        Assert.Empty(word.Entries);
    }

    [Fact]
    public void Many_changes_from_two_apps_at_once_lose_none_and_the_lock_file_stays_empty()
    {
        var word = App();
        var excel = App();
        var start = new ManualResetEventSlim();
        var threads = new[] { (word, "w"), (excel, "x") }.Select(p => new Thread(() =>
        {
            start.Wait();
            for (var i = 0; i < 25; i++)
                Assert.True(p.Item1.Add(McpStoreTests.Program($"{p.Item2}-{i}")).IsValid);
        })).ToList();
        threads.ForEach(t => t.Start());

        start.Set();
        threads.ForEach(t => t.Join());

        Assert.Equal(50, App().Entries.Count);
        var lockFile = new FileInfo(Path.Combine(_root, "standalone", McpServerStore.LockFileName));
        Assert.True(lockFile.Exists);
        Assert.Equal(0, lockFile.Length);
    }

    [Fact]
    public async Task A_server_another_app_adds_joins_the_running_agent_and_leaves_with_its_removal()
    {
        var rig = new McpRig { Tools = { McpRig.Tool("add", "Adds.", (int a, int b) => a + b) } };
        using var word = new McpHost(_ => App(), server: e => rig.Server(e.Label));
        using var excel = new McpHost(_ => App(), server: e => rig.Server(e.Label));
        word.NewModule();
        excel.NewModule();
        await word.UseStandaloneAsync();
        await excel.UseStandaloneAsync();

        await excel.AddAsync(McpStoreTests.Program("beta"));
        await McpRig.EventuallyAsync(() => word.Module!.GetStatus("beta") is not null, "Word's module has the server Excel added");
        Assert.Equal(["beta"], word.Rows.Select(r => r.Label));

        await excel.SetEnabledAsync("beta", false);
        await McpRig.EventuallyAsync(() => word.Module!.GetStatus("beta")?.State == McpServerState.Off, "Excel's switch holds in Word");

        await excel.RemoveAsync("beta");
        await McpRig.EventuallyAsync(() => word.Module!.GetStatus("beta") is null, "the server leaves Word's module");
        Assert.Empty(word.Rows);
    }

    [Fact]
    public async Task Two_windows_on_one_store_follow_each_other()
    {
        var rig = new McpRig { Tools = { McpRig.Tool("add", "Adds.", (int a, int b) => a + b) } };
        var shared = App();
        using var first = new McpHost(_ => shared, server: e => rig.Server(e.Label));
        using var second = new McpHost(_ => shared, server: e => rig.Server(e.Label));
        first.NewModule();
        second.NewModule();
        await first.UseStandaloneAsync();
        await second.UseStandaloneAsync();

        await second.AddAsync(McpStoreTests.Program("gamma"));

        await McpRig.EventuallyAsync(() => first.Module!.GetStatus("gamma") is not null, "the first window's module has the server");
    }

    [Fact]
    public async Task Reading_again_on_request_brings_the_module_in_line_at_once()
    {
        var rig = new McpRig { Tools = { McpRig.Tool("add", "Adds.", (int a, int b) => a + b) } };
        using var host = new McpHost(_ => App(), server: e => rig.Server(e.Label));
        host.NewModule();
        await host.UseStandaloneAsync();
        App().Add(McpStoreTests.Program("delta"));

        await host.ReloadAsync();

        Assert.NotNull(host.Module!.GetStatus("delta"));
        Assert.Equal(["delta"], host.Rows.Select(r => r.Label));
    }
}
