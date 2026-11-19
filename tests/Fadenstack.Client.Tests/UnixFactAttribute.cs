using System.Runtime.InteropServices;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>A test of file modes (0600, 0700) on macOS and Linux; it is skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "macOS and Linux only.";
        }
    }
}
