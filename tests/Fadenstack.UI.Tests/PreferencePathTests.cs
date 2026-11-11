using System.Runtime.InteropServices;
using Fadenstack.UI.Services;
using Xunit;

namespace Fadenstack.UI.Tests;

public class PreferencePathTests
{
    [Fact]
    public void On_Windows_the_preferences_stay_in_the_roaming_folder()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(Path.Combine(roaming, "Fadenstack", "preferences.json"), FilePreferenceStore.DefaultPath);
    }
}
