using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Fadenstack.Agents.Delegation;

/// <summary>
/// Pipes for delegation: created for the current OS user only. On .NET 8 with <c>PipeOptions.CurrentUserOnly</c>; on
/// .NET Framework (the netstandard2.0 build) with a <c>PipeSecurity</c> that names the current user's SID alone.
/// </summary>
internal static class DelegationPipes
{
    public const string Machine = ".";

    public static NamedPipeServerStream CreateServer(string name, int maxInstances)
    {
#if NET8_0_OR_GREATER
        return new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#else
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // A netstandard2.0 build on another system is not a supported host; the pipe is still found only through the owner-only registry.
            return new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        return CreateWindowsServer(name, maxInstances);
#endif
    }

#if !NET8_0_OR_GREATER
    private static NamedPipeServerStream CreateWindowsServer(string name, int maxInstances)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        // .NET Framework has a constructor that takes the PipeSecurity at creation (no moment with the default ACL, which lets
        // Everyone read); the netstandard2.0 reference lacks it, so it is called by reflection, and the ACL is set
        // afterwards where only another runtime has no such constructor.
        var constructor = typeof(NamedPipeServerStream).GetConstructor(new[]
        {
            typeof(string), typeof(PipeDirection), typeof(int), typeof(PipeTransmissionMode), typeof(PipeOptions),
            typeof(int), typeof(int), typeof(PipeSecurity),
        });
        if (constructor is not null)
        {
            return (NamedPipeServerStream)constructor.Invoke(new object[]
            {
                name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security,
            });
        }
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        pipe.SetAccessControl(security);
        return pipe;
    }
#endif

    public static NamedPipeClientStream CreateClient(string name)
    {
#if NET8_0_OR_GREATER
        return new NamedPipeClientStream(Machine, name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            System.Security.Principal.TokenImpersonationLevel.Identification);
#else
        return new NamedPipeClientStream(Machine, name, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
#endif
    }

    /// <summary>A random token (hex) for pipe names and launch secrets.</summary>
    public static string RandomHex(int bytes)
    {
        var data = new byte[bytes];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(data);
        var text = new char[bytes * 2];
        for (var i = 0; i < bytes; i++)
        {
            text[i * 2] = "0123456789abcdef"[data[i] >> 4];
            text[i * 2 + 1] = "0123456789abcdef"[data[i] & 15];
        }
        return new string(text);
    }
}
