using Fadenstack.Logging;
using Fadenstack.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>The file sink: only in the user's own log folder, rolling, limited in age and size, and never in the caller's way.</summary>
[Collection(PathsCollection.Name)]
public class FileLoggerTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-logs-" + Guid.NewGuid());
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-28T09:30:00Z");

    public FileLoggerTests() => FadenstackPaths.RootForTests = _temporary;

    public void Dispose()
    {
        FadenstackPaths.RootForTests = null;
        if (Directory.Exists(_temporary))
        {
            Directory.Delete(_temporary, true);
        }
    }

    private string Logs => Path.Combine(_temporary, "logs");

    private FadenstackFileLoggerProvider Provider(FadenstackFileLoggerOptions? options = null, ManualResetEventSlim? gate = null) =>
        new("addin", options, () => _now, gate);

    private static EventId Id(int id) => new(id);

    [Fact]
    public void Entries_go_to_a_dated_file_in_the_log_root_one_line_each()
    {
        using (var provider = Provider())
        {
            var log = provider.CreateLogger("Fadenstack.Test");
            log.Log(LogLevel.Information, Id(1000), "first", null, (s, _) => s);
            log.Log(LogLevel.Warning, Id(1001), "two\nlines", new InvalidOperationException("broke\nhere"), (s, _) => s);
        }

        var file = Assert.Single(Directory.GetFiles(Logs));
        Assert.Equal(Path.Combine(Logs, "addin-20261028.log"), file);
        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-10-28T09:30:00.0000000Z INF Fadenstack.Test 1000 first", lines[0]);
        Assert.StartsWith("2026-10-28T09:30:00.0000000Z WRN Fadenstack.Test 1001 two\\nlines | System.InvalidOperationException: broke\\nhere | ", lines[1]);
    }

    [Fact]
    public void The_app_name_is_made_safe_as_a_file_name()
    {
        using (var provider = new FadenstackFileLoggerProvider("../evil/app", null, () => _now, null))
        {
            provider.CreateLogger("c").Log(LogLevel.Information, Id(1), "x", null, (s, _) => s);
        }

        Assert.Equal("___evil_app-20261028.log", Path.GetFileName(Assert.Single(Directory.GetFiles(Logs))));
    }

    [Fact]
    public void A_folder_below_the_log_root_is_allowed_and_any_other_is_refused()
    {
        var inside = Path.Combine(Logs, "agent");
        using (var provider = new FadenstackFileLoggerProvider("addin", new FadenstackFileLoggerOptions { Folder = inside }, () => _now, null))
        {
            provider.CreateLogger("c").Log(LogLevel.Information, Id(1), "x", null, (s, _) => s);
        }
        Assert.Single(Directory.GetFiles(inside));

        foreach (var outside in new[] { Path.Combine(_temporary, "data"), Path.Combine(Logs, "..", "elsewhere"), Path.Combine(_temporary, "logs-x"),
            Path.GetPathRoot(_temporary)! })
        {
            Assert.Throws<ArgumentException>(() => new FadenstackFileLoggerProvider("addin", new FadenstackFileLoggerOptions { Folder = outside }));
        }
        Assert.False(Directory.Exists(Path.Combine(_temporary, "elsewhere")));
    }

    [Fact]
    public void A_file_that_would_pass_the_size_limit_is_numbered_and_a_new_one_started()
    {
        using (var provider = Provider(new FadenstackFileLoggerOptions { MaxFileBytes = 300 }))
        {
            var log = provider.CreateLogger("c");
            for (var i = 0; i < 10; i++)
            {
                log.Log(LogLevel.Information, Id(i), new string('x', 60), null, (s, _) => s);
            }
        }

        var names = Directory.GetFiles(Logs).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Contains("addin-20261028.log", names);
        Assert.Contains("addin-20261028.log.1", names);
        Assert.Contains("addin-20261028.log.2", names);
        Assert.All(Directory.GetFiles(Logs), f => Assert.True(new FileInfo(f).Length <= 300));
        Assert.Equal(10, Directory.GetFiles(Logs).Sum(f => File.ReadAllLines(f).Length));
        Assert.StartsWith("2026-10-28T09:30:00.0000000Z INF c 0 ", File.ReadAllLines(Path.Combine(Logs, "addin-20261028.log.1"))[0]); // .1 is the oldest
    }

    [Fact]
    public void Files_older_than_the_limit_are_deleted_at_start_but_only_this_apps()
    {
        Directory.CreateDirectory(Logs);
        string Make(string name, int daysOld)
        {
            var path = Path.Combine(Logs, name);
            File.WriteAllText(path, "old");
            File.SetLastWriteTimeUtc(path, _now.UtcDateTime.AddDays(-daysOld));
            return path;
        }
        var old = Make("addin-20260801.log", 90);
        var rolled = Make("addin-20260801.log.3", 90);
        var recent = Make("addin-20261020.log", 8);
        var otherApp = Make("agent-20260801.log", 90);
        var notALog = Make("addin-notes.txt", 90);

        using (Provider(new FadenstackFileLoggerOptions { MaxAge = TimeSpan.FromDays(14) }))
        {
        }

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(rolled));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(otherApp));
        Assert.True(File.Exists(notALog));
    }

    [Fact]
    public void The_oldest_files_go_first_when_the_total_is_over_its_cap()
    {
        Directory.CreateDirectory(Logs);
        for (var day = 1; day <= 4; day++)
        {
            var path = Path.Combine(Logs, $"addin-2026102{day}.log");
            File.WriteAllText(path, new string('x', 100));
            File.SetLastWriteTimeUtc(path, _now.UtcDateTime.AddDays(day - 5));
        }

        using (Provider(new FadenstackFileLoggerOptions { MaxTotalBytes = 250 }))
        {
        }

        Assert.Equal(new[] { "addin-20261023.log", "addin-20261024.log" },
            Directory.GetFiles(Logs).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_full_queue_drops_entries_instead_of_holding_up_the_caller_and_says_so_in_the_file()
    {
        using var gate = new ManualResetEventSlim(false); // the writer waits: the queue fills
        long dropped;
        using (var provider = Provider(new FadenstackFileLoggerOptions { QueueCapacity = 2 }, gate))
        {
            var log = provider.CreateLogger("c");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
            {
                log.Log(LogLevel.Information, Id(i), "entry", null, (s, _) => s);
            }
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            dropped = provider.Dropped;
            gate.Set();
        }

        Assert.Equal(8, dropped);
        var lines = File.ReadAllLines(Directory.GetFiles(Logs).Single());
        Assert.Equal(3, lines.Length);
        Assert.Single(lines, l => l.EndsWith("8 entries were dropped: the queue was full", StringComparison.Ordinal));
    }

    [Fact]
    public void A_log_that_cannot_be_written_never_throws_into_the_caller()
    {
        Directory.CreateDirectory(Logs);
        File.WriteAllText(Path.Combine(Logs, "addin-20261028.log"), "");
        using var blocker = new FileStream(Path.Combine(Logs, "addin-20261028.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var provider = Provider();
        var log = provider.CreateLogger("c");

        var thrown = Record.Exception(() => log.Log(LogLevel.Information, Id(1), "x", null, (s, _) => s));
        var bad = Record.Exception(() => log.Log<string>(LogLevel.Information, Id(2), "x", null, (_, _) => throw new InvalidOperationException("formatter")));

        Assert.Null(thrown);
        Assert.Null(bad);
    }

    [Fact]
    public void Dispose_writes_what_is_queued_and_a_later_entry_is_ignored()
    {
        var provider = Provider();
        var log = provider.CreateLogger("c");
        for (var i = 0; i < 50; i++)
        {
            log.Log(LogLevel.Information, Id(i), "entry", null, (s, _) => s);
        }

        provider.Dispose();
        var after = Record.Exception(() => log.Log(LogLevel.Information, Id(99), "late", null, (s, _) => s));

        Assert.Null(after);
        Assert.Equal(50, File.ReadAllLines(Directory.GetFiles(Logs).Single()).Length);
    }
}
