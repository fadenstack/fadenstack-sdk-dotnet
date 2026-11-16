using System.Runtime.CompilerServices;
using Fadenstack.Storage;

namespace Fadenstack.Provenance.Tests;

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
        FadenstackPaths.UseRootForTests(Path.Combine(Path.GetTempPath(), "faden-prov-tests-" + Guid.NewGuid().ToString("N")));
}
