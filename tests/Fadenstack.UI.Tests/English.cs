using System.Globalization;
using System.Runtime.CompilerServices;
using Fadenstack.UI.Services;

namespace Fadenstack.UI.Tests;

/// <summary>
/// The tests read English texts and numbers whatever this machine's languages are, and keep their language picks in
/// memory, never in the user's preferences file.
/// </summary>
internal static class English
{
    [ModuleInitializer]
    internal static void Pin()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.CurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        PreferenceDefaults.Create = () => new MemoryPreferenceStore();
    }
}
