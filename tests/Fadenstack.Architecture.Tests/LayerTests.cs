using Xunit;

namespace Fadenstack.Architecture.Tests;

/// <summary>
/// A1 and A4 (fadenstack-dev sdk/52 §4): references point down only; a module never references another module or the UI
/// layer; the UI layer references no module; heavy dependencies live in the module that needs them, so the core stays light
/// enough for Outlook. Read twice: the project files (a reference the compiler drops still ships in the package) and the
/// built assemblies (what the code really uses).
/// </summary>
public class LayerTests
{
    private const string Client = "Fadenstack.Client";
    private const string Agents = "Fadenstack.Agents";
    private const string Ui = "Fadenstack.UI";

    /// <summary>What a library may reference: the SDK's own assemblies, and external names (a name or its dotted children).</summary>
    private sealed record Row(string[] Fadenstack, string[] External);

    /// <summary>The platform, and what every library may use.</summary>
    private static readonly string[] Everywhere =
        { "System", "netstandard", "mscorlib", "Microsoft.Win32", "Microsoft.Bcl", "Microsoft.Extensions.Logging.Abstractions" };

    private static Row Module(params string[] heavy) =>
        new(new[] { Agents, Client }, new[] { "Microsoft.Extensions.AI.Abstractions" }.Concat(heavy).ToArray());

    /// <summary>
    /// Every library has a row. A new one (Fadenstack.Skills with F04, Fadenstack.WebSearch with F06) gets its row when it
    /// is made, and fails <see cref="Every_library_has_a_row"/> until then.
    /// </summary>
    private static readonly Dictionary<string, Row> Rows = new()
    {
        // 1 · connection and platform
        [Client] = new(Array.Empty<string>(), new[] { "Microsoft.Extensions.AI.Abstractions" }),
        ["Fadenstack.Secrets"] = new(new[] { Client }, new[] { "Microsoft.Identity.Client" }),
        // 2 · the core: Microsoft.Extensions.AI, and of MAF its Abstractions at most (A4)
        [Agents] = new(new[] { Client },
            new[] { "Microsoft.Extensions.AI", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Agents.AI.Abstractions" }),
        // 3 · modules: the core below them, never another module or the UI; each brings its own heavy dependency
        ["Fadenstack.Mcp"] = Module("ModelContextProtocol"),
        ["Fadenstack.Provenance"] = Module(),
        ["Fadenstack.Workspaces"] = Module(),
        // the desktop branch's delegation between agents on one device, over the A2A library (apps only, off nuget.org)
        ["Fadenstack.Agents.Delegation"] = Module("A2A"),
        // 4 · UI logic: view models and texts, no UI framework, no module
        [Ui] = new(new[] { Agents, Client }, new[] { "CommunityToolkit.Mvvm", "Microsoft.Extensions.AI.Abstractions" }),
        // 5 · skins: the UI layer and below, and their own framework
        ["Fadenstack.UI.Wpf"] = new(new[] { Ui, Agents, Client },
            new[] { "CommunityToolkit.Mvvm", "PresentationCore", "PresentationFramework", "WindowsBase", "Microsoft.NETFramework.ReferenceAssemblies" }),
        ["Fadenstack.UI.Avalonia"] = new(new[] { Ui, Agents, Client }, new[] { "CommunityToolkit.Mvvm", "Avalonia" }),
    };

    public static TheoryData<string> Libraries()
    {
        var data = new TheoryData<string>();
        foreach (var library in Repo.Libraries.Where(Rows.ContainsKey))
        {
            data.Add(library);
        }
        return data;
    }

    private static bool AllowedExternal(Row row, string name) =>
        Everywhere.Concat(row.External).Any(allowed => name == allowed || name.StartsWith(allowed + ".", StringComparison.Ordinal));

    [Fact]
    public void Every_library_has_a_row()
    {
        Assert.NotEmpty(Repo.Libraries);
        Assert.DoesNotContain(Repo.Libraries, library => !Rows.ContainsKey(library));
    }

    [Theory]
    [MemberData(nameof(Libraries))]
    public void A_project_references_only_what_its_layer_allows(string library)
    {
        var row = Rows[library];
        var (projects, packages) = Repo.References(library);

        var wrong = projects.Where(p => !row.Fadenstack.Contains(p)).Select(p => "project " + p)
            .Concat(packages.Where(p => p.StartsWith("Fadenstack.", StringComparison.Ordinal) ? !row.Fadenstack.Contains(p) : !AllowedExternal(row, p))
                .Select(p => "package " + p))
            .ToList();

        Assert.True(wrong.Count == 0, $"{library} references what its layer does not allow: {string.Join(", ", wrong)}");
    }

    [Theory]
    [MemberData(nameof(Libraries))]
    public void A_built_assembly_uses_only_what_its_layer_allows(string library)
    {
        if (!Repo.Readable(library))
        {
            return; // the WPF skin is read on Windows
        }
        var row = Rows[library];

        var wrong = Repo.BuiltReferences(library)
            .Where(name => name.StartsWith("Fadenstack.", StringComparison.Ordinal) ? !row.Fadenstack.Contains(name) : !AllowedExternal(row, name))
            .ToList();

        Assert.True(wrong.Count == 0, $"{library} uses what its layer does not allow: {string.Join(", ", wrong)}");
    }

    /// <summary>A4: the core references MAF's Abstractions at most, never <c>Microsoft.Agents.AI</c> itself (sdk/52 §4 rule 5).</summary>
    [Fact]
    public void The_core_takes_at_most_MAFs_abstractions()
    {
        var (_, packages) = Repo.References(Agents);

        Assert.DoesNotContain("Microsoft.Agents.AI", packages);
        Assert.DoesNotContain("Microsoft.Agents.AI", Repo.BuiltReferences(Agents));
        Assert.DoesNotContain(Repo.BuiltReferences(Client), name => name.StartsWith("Microsoft.Agents.", StringComparison.Ordinal));
    }
}
