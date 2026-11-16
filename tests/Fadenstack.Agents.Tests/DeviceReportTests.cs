using System.Text.Json;
using Fadenstack.Client.Tests;
using Fadenstack.Models;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// What the app reports about the device (sdk/51 8.3): the manifest's <c>device</c> block, with the operating system, the app's
/// version, the plugins the app declares and the programs of local MCP servers, cut to the server's limits; never a path, a user
/// name, an argument or an environment value.
/// </summary>
public class DeviceReportTests
{
    private static readonly string Sha = new('a', 64);

    private sealed class Programs(params ManifestMcpProgram[] programs) : IToolSource, IDeviceProgramSource
    {
        public Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentTool>>(Array.Empty<AgentTool>());

        public IReadOnlyList<ManifestMcpProgram> McpPrograms() => programs;
    }

    private static async Task<JsonElement> DeviceAsync(Gateway gateway, FadenstackAgent agent)
    {
        await agent.StartAsync();
        for (var i = 0; i < 200 && gateway.Manifests.Count == 0; i++)
        {
            await Task.Delay(10);
        }
        return gateway.Manifests.First().Json.GetProperty("device");
    }

    [Fact]
    public async Task The_block_holds_the_os_the_apps_version_the_declared_plugins_and_no_program_by_default()
    {
        var gateway = new Gateway();
        using var agent = gateway.Builder().DescribeApp(new AppDescription { Name = "Viewer add-in", Version = "2.1.0" })
            .DescribePlugins(new ManifestPlugin { Name = "contoso-crm", Version = "1.0.0", Sha256 = Sha.ToUpperInvariant() }).Build();

        var device = await DeviceAsync(gateway, agent);

        Assert.Equal("2.1.0", device.GetProperty("app_version").GetString());
        var os = device.GetProperty("os").GetString()!;
        Assert.True(os.Length is > 0 and <= DeviceLimits.Os);
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), os);
        var plugin = Assert.Single(device.GetProperty("plugins").EnumerateArray());
        Assert.Equal(("contoso-crm", "1.0.0", Sha), (plugin.GetProperty("name").GetString(), plugin.GetProperty("version").GetString(), plugin.GetProperty("sha256").GetString()));
        Assert.Empty(device.GetProperty("mcp_programs").EnumerateArray());
        Assert.Equal(["app_version", "mcp_programs", "os", "plugins"], device.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_tool_source_that_knows_programs_adds_them_and_the_block_is_there_without_describing_the_app()
    {
        var gateway = new Gateway();
        using var agent = gateway.Builder().AddToolSource(new Programs(new ManifestMcpProgram { Name = "files", Program = "server-files.exe", Sha256 = Sha })).Build();

        var device = await DeviceAsync(gateway, agent);

        var program = Assert.Single(device.GetProperty("mcp_programs").EnumerateArray());
        Assert.Equal(("files", "server-files.exe", Sha), (program.GetProperty("name").GetString(), program.GetProperty("program").GetString(), program.GetProperty("sha256").GetString()));
        Assert.Equal("", device.GetProperty("app_version").GetString());
    }

    [Fact]
    public async Task An_entry_that_does_not_fit_is_left_out_and_the_log_says_only_its_kind_and_why()
    {
        var logs = new CapturingLoggerFactory();
        var gateway = new Gateway();
        using var agent = gateway.Builder().UseLogging(logs)
            .DescribePlugins(
                new ManifestPlugin { Name = "secret-plugin-name", Version = "1", Sha256 = "not-a-hash" },
                new ManifestPlugin { Name = "", Version = "1", Sha256 = Sha },
                new ManifestPlugin { Name = "long-version", Version = new string('v', 51), Sha256 = Sha },
                new ManifestPlugin { Name = "good", Version = "", Sha256 = Sha })
            .AddToolSource(new Programs(
                new ManifestMcpProgram { Name = "folder", Program = "", Sha256 = Sha },
                new ManifestMcpProgram { Name = "ok", Program = "server.exe", Sha256 = Sha }))
            .Build();

        var device = await DeviceAsync(gateway, agent);

        Assert.Equal(["good"], device.GetProperty("plugins").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(["ok"], device.GetProperty("mcp_programs").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(
            ["A plugin was left out of the device report: sha256_invalid", "A plugin was left out of the device report: name_invalid",
                "A plugin was left out of the device report: version_too_long", "A mcp_program was left out of the device report: program_invalid"],
            logs.Entries.Where(e => e.EventId == 2018).Select(e => e.Message));
        Assert.DoesNotContain("secret-plugin-name", string.Join(" | ", logs.Texts));
    }

    [Fact]
    public async Task At_most_100_plugins_and_100_programs_go_and_the_os_and_version_are_cut()
    {
        var gateway = new Gateway();
        using var agent = gateway.Builder().DescribeApp(new AppDescription { Name = "A", Version = new string('9', 80) })
            .DescribePlugins(Enumerable.Range(0, 130).Select(i => new ManifestPlugin { Name = "p" + i, Version = "1", Sha256 = Sha }))
            .AddToolSource(new Programs([.. Enumerable.Range(0, 120).Select(i => new ManifestMcpProgram { Name = "s" + i, Program = "x.exe", Sha256 = Sha })]))
            .Build();

        var device = await DeviceAsync(gateway, agent);

        Assert.Equal(DeviceLimits.Entries, device.GetProperty("plugins").GetArrayLength());
        Assert.Equal(DeviceLimits.Entries, device.GetProperty("mcp_programs").GetArrayLength());
        Assert.Equal(DeviceLimits.AppVersion, device.GetProperty("app_version").GetString()!.Length);
    }

    [Fact]
    public void The_os_and_the_checks_follow_the_limits_the_server_states()
    {
        var long100 = DeviceReport.Build(new string('o', 300), "1", [], [], (_, _) => { });
        Assert.Equal(DeviceLimits.Os, long100.Os.Length);
        Assert.Null(DeviceReport.PluginProblem(new ManifestPlugin { Name = new string('n', 128), Version = new string('1', 50), Sha256 = Sha }));
        Assert.Equal(DeviceDropReason.NameInvalid, DeviceReport.PluginProblem(new ManifestPlugin { Name = new string('n', 129), Version = "1", Sha256 = Sha }));
        Assert.Equal(DeviceDropReason.Sha256Invalid, DeviceReport.PluginProblem(new ManifestPlugin { Name = "n", Version = "1", Sha256 = new string('g', 64) }));
        Assert.Equal(DeviceDropReason.ProgramInvalid, DeviceReport.ProgramProblem(new ManifestMcpProgram { Name = "n", Program = new string('p', 256), Sha256 = Sha }));
        Assert.Equal("server.exe", DeviceReport.FileNameOf(@"C:\Users\anna\bin\server.exe"));
        Assert.Equal("server", DeviceReport.FileNameOf("/home/anna/bin/server"));
    }

    [Fact]
    public async Task No_block_goes_where_event_reporting_is_off_as_no_manifest_does()
    {
        var gateway = new Gateway();
        using var agent = gateway.Builder().UseEventReporting(false).DescribePlugins(new ManifestPlugin { Name = "p", Version = "1", Sha256 = Sha }).Build();
        await agent.StartAsync();
        await Task.Delay(50);
        Assert.Empty(gateway.Manifests);
    }
}
