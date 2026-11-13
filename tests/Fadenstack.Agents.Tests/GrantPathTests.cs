using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Fadenstack.Client.Tests;
using Fadenstack.Tools;
using Xunit;
using static Fadenstack.Agents.Tests.ApprovalTests;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// A stretch of work's places where they really lead (sdk/38 §6, the second line; ASVS 5.0 5.3.2): the gateway matches a
/// call's path by its text, so the device resolves links, junctions and mount points before a covered call runs, and
/// asks when the real place lies outside the grant. On Windows the links are junctions (no rights needed), elsewhere
/// symbolic links.
/// </summary>
public sealed class GrantPathTests : IDisposable
{
    // add_comment names its place in "text", a path in a granted folder.
    private const string Contract = """[{"name":"add_comment","risk":"write","target_argument":"/text","target_type":"folder"}]""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "faden-grant-paths-" + Guid.NewGuid().ToString("N"));

    public GrantPathTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "granted"));
        Directory.CreateDirectory(Path.Combine(_root, "outside"));
        File.WriteAllText(Path.Combine(_root, "granted", "plain.txt"), "in the grant");
        File.WriteAllText(Path.Combine(_root, "outside", "secret.txt"), "not in the grant");
    }

    public void Dispose()
    {
        // The links go first, on their own: a delete that followed one would empty "outside".
        foreach (var link in Directory.EnumerateFileSystemEntries(At("granted"))
                     .Where(p => File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)).ToList())
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Directory.Delete(link);
            }
            else
            {
                File.Delete(link);
            }
        }
        Directory.Delete(_root, recursive: true);
    }

    private string At(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    /// <summary>A junction on Windows (mklink /J, no window, no rights needed), a symbolic link elsewhere.</summary>
    private static void Link(string link, string target)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
#if NET
            Directory.CreateSymbolicLink(link, target);
            return;
#else
            throw new PlatformNotSupportedException("The net48 tests run on Windows.");
#endif
        }
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0, mklink.StandardError.ReadToEnd());
    }

    private static string Comment(string path) => JsonSerializer.Serialize(new { page = 1, text = path });

    /// <summary>A work grant on <paramref name="folder"/>, and one covered call per path, each in its own turn.</summary>
    private static async Task<(List<ApprovalRequest> Asked, PageTools Tools, ChatSession Session)> RunAsync(string folder, params string[] paths)
    {
        var gateway = new Gateway(contract: Contract);
        var grants = gateway.UseGrants();
        var id = grants.Hold("work", new { classes = new[] { "write" }, targets = new[] { new { type = "folder", identifiers = new[] { folder } } } });
        for (var i = 0; i < paths.Length; i++)
        {
            // The gateway's word: the text lies under the granted folder.
            gateway.Then(Gateway.Custom("faden.grant_used", new { toolCallId = $"call-{i}", toolCallName = "add_comment", grantId = id }),
                Gateway.ToolCalls(($"call-{i}", "add_comment", Comment(paths[i]))), Gateway.Finish("tool_calls"), FakeServer.Done).Says("Done.");
        }
        var asked = new List<ApprovalRequest>();
        var tools = new PageTools();
        var agent = gateway.Builder().AddTools(tools)
            .UseApprovals((r, _) => { asked.Add(r); return Task.FromResult(ApprovalDecision.Deny()); }).Build();
        var session = await agent.StartSessionAsync(ToolMode.Ask);
        foreach (var path in paths)
        {
            await session.SendAsync("Comment on " + Path.GetFileName(path));
        }
        return (asked, tools, session);
    }

    [Fact]
    public async Task A_junction_inside_a_granted_folder_that_leads_outside_asks_and_the_card_says_where_it_leads()
    {
        Link(At("granted", "link"), At("outside"));
        var outside = GrantPaths.Real(At("outside"))!;

        var (asked, tools, session) = await RunAsync(At("granted"),
            At("granted", "plain.txt"),
            At("granted", "link", "secret.txt"), // an existing file behind the junction
            At("granted", "link", "new.txt"), // a file the call would make there
            At("granted", "link")); // the junction itself

        Assert.Equal(new[] { $"add_comment 1 {At("granted", "plain.txt")}" }, tools.Ran); // the others were declined
        Assert.Equal(new[] { Path.Combine(outside, "secret.txt"), Path.Combine(outside, "new.txt"), outside },
            asked.Select(r => r.LeadsOutside));
        var records = Decisions(session).ToList();
        Assert.Equal(new[] { "grant", null, null, null }, records.Select(d => Str(d, "scope")));
        Assert.Equal(new[] { false, true, true, true }, records.Select(d => d.GetProperty("outside_grant").GetBoolean()));
    }

    [Fact]
    public async Task A_granted_folder_reached_through_a_junction_covers_what_lies_in_it()
    {
        // The person granted the folder by a path that is itself a link: the place and the target lead to the same folder.
        Link(At("granted", "project"), At("outside"));

        var (asked, tools, _) = await RunAsync(At("granted", "project"), At("granted", "project", "secret.txt"));

        Assert.Empty(asked);
        Assert.Single(tools.Ran);
    }

    [Fact]
    public void Without_libc_a_path_cannot_be_resolved_so_it_covers_nothing_and_nothing_throws()
    {
        // A minimal container without libc, or without realpath in it (sdk/51 I00 #9, N8): resolving a path then covers
        // nothing, as a link whose target is missing does (the test below: the call asks).
        Assert.Null(GrantPaths.Real(At("granted"), (string _, out bool _) => throw new DllNotFoundException("libc")));
        Assert.Null(GrantPaths.Real(At("granted"), (string _, out bool _) => throw new EntryPointNotFoundException("realpath")));
    }

    [Fact]
    public async Task A_link_whose_target_is_missing_asks_as_where_it_leads_cannot_be_told()
    {
        Link(At("granted", "dead"), At("gone"));

        var (asked, tools, _) = await RunAsync(At("granted"), At("granted", "dead", "new.txt"));

        Assert.Null(Assert.Single(asked).LeadsOutside); // asked, with the class's own reason
        Assert.Empty(tools.Ran);
    }

    [Fact]
    public void Real_follows_every_link_on_the_way_and_keeps_a_missing_end_as_it_is()
    {
        Link(At("granted", "link"), At("outside"));
        var outside = GrantPaths.Real(At("outside"))!;

        Assert.Equal(Path.Combine(outside, "secret.txt"), GrantPaths.Real(At("granted", "link", "secret.txt")));
        Assert.Equal(Path.Combine(outside, "a", "b.txt"), GrantPaths.Real(At("granted", "link", "a", "b.txt")));
        // ".." is taken from the text, as .NET opens a path (Path.GetFullPath); the gateway never covers a path with "..".
        Assert.Equal(Path.Combine(GrantPaths.Real(At("granted"))!, "plain.txt"),
            GrantPaths.Real(At("granted", "link", "..", "plain.txt")));
    }

    [Fact]
    public void A_folder_holds_what_lies_under_it_not_a_folder_whose_name_starts_the_same()
    {
        var granted = GrantPaths.Real(At("granted"))!;

        Assert.True(GrantPaths.Lies("folder", granted, Path.Combine(granted, "plain.txt")));
        Assert.True(GrantPaths.Lies("folder", granted, granted + Path.DirectorySeparatorChar));
        Assert.False(GrantPaths.Lies("folder", granted, granted + "-other" + Path.DirectorySeparatorChar + "plain.txt"));
        Assert.False(GrantPaths.Lies("document", granted, Path.Combine(granted, "plain.txt"))); // a document by identity only
        Assert.True(GrantPaths.Lies("folder", Path.GetPathRoot(granted)!, granted));
    }

    [Theory]
    [InlineData("site", "https://purchase.example.de", "https://Purchase.example.de/orders/7", true)]
    [InlineData("site", "https://purchase.example.de", "https://purchase.example.de.evil.test/", false)]
    [InlineData("document", "https://d.docs.live.net/1/report.docx", "https://d.docs.live.net/1/Report.docx", true)]
    [InlineData("folder", "/srv/share", "/srv/share/../etc/passwd", false)]
    [InlineData("app", "excel", "EXCEL", true)]
    public void A_place_that_names_no_path_here_is_matched_by_its_text_as_the_gateway_does(string type, string target, string place, bool lies)
    {
        Assert.Equal(lies, GrantPaths.Named(type, target, place));
    }
}
