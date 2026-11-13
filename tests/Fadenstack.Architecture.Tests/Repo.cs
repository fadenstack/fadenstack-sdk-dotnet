using System.Reflection;
using System.Xml.Linq;

namespace Fadenstack.Architecture.Tests;

/// <summary>The repository the tests run in: its root, the libraries in <c>src</c>, their project files and built assemblies.</summary>
internal static class Repo
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fadenstack.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Fadenstack.slnx was not found above the test output.");
    }

    /// <summary>Every library: each folder in <c>src</c> with a project of its own name.</summary>
    public static IReadOnlyList<string> Libraries { get; } = Directory.GetDirectories(Path.Combine(Root, "src"))
        .Select(d => Path.GetFileName(d)!)
        .Where(name => File.Exists(ProjectFile(name)))
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();

    public static string ProjectFile(string library) => Path.Combine(Root, "src", library, library + ".csproj");

    /// <summary>The project's <c>ProjectReference</c> and <c>PackageReference</c> items, by name, whatever their condition.</summary>
    public static (IReadOnlyList<string> Projects, IReadOnlyList<string> Packages) References(string library)
    {
        var project = XDocument.Load(ProjectFile(library));
        var projects = project.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string?)e.Attribute("Include") ?? "").Replace('\\', '/')))
            .ToList();
        var packages = project.Descendants("PackageReference").Select(e => (string?)e.Attribute("Include") ?? "").ToList();
        return (projects, packages);
    }

    /// <summary>The library's built assembly, the one next to the tests.</summary>
    public static Assembly Assembly(string library) => System.Reflection.Assembly.Load(new AssemblyName(library));

    /// <summary>The names of the assemblies a library's compiled code uses.</summary>
    public static IReadOnlyList<string> BuiltReferences(string library) =>
        Assembly(library).GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();

    /// <summary>A file's path relative to the root, with forward slashes (the same on every system).</summary>
    public static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    /// <summary>The files of a library's sources with one of the extensions, outside <c>bin</c> and <c>obj</c>.</summary>
    public static IEnumerable<string> Sources(string library, params string[] endings) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", library), "*", SearchOption.AllDirectories)
            .Where(f => endings.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !Relative(f).Split('/').Any(part => part is "bin" or "obj"))
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>The WPF skin builds and loads on Windows only.</summary>
    public static bool Readable(string library) => library != "Fadenstack.UI.Wpf" || OperatingSystem.IsWindows();
}
