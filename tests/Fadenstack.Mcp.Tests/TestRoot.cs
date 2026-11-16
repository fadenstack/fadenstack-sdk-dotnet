using System.Runtime.CompilerServices;
using Fadenstack.Storage;

namespace Fadenstack.Mcp.Tests;

/// <summary>
/// Every agent keeps its record of tool calls (and its other data) in the person's folder unless the app says otherwise, so the tests'
/// process never touches the OS user's own folders: it points every root at a temporary folder. A test that sets its own root and clears it
/// falls back to this one.
/// </summary>
internal static class TestRoot
{
#pragma warning disable CA2255 // a test assembly: the one place the process's folders are chosen
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Redirect() =>
        FadenstackPaths.FallbackRootForTests = Path.Combine(Path.GetTempPath(), "faden-mcp-tests-" + Guid.NewGuid().ToString("N"));
}
