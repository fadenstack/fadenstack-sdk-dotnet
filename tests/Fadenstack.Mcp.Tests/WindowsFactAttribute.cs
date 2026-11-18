using System.Runtime.InteropServices;

namespace Fadenstack.Mcp.Tests;

/// <summary>A test that starts real Windows programs (cmd.exe, powershell.exe); it is skipped on other systems.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Windows only.";
        }
    }
}

/// <summary>A theory that starts real Windows programs; it is skipped on other systems.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Windows only.";
        }
    }
}
