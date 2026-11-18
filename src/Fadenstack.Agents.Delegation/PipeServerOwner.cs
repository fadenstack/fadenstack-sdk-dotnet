using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

// SecurityIdentifier is Windows-only on .NET 8; every use here is behind the Windows check in RequireCurrentUser (the unit-tested
// Require only compares what a source gives).
#pragma warning disable CA1416

namespace Fadenstack.Agents.Delegation;

/// <summary>
/// Who runs the process that serves a pipe (OWASP ASVS 5.0 V8, authorization: a local user must not stand in for the agent).
/// On .NET 8 the client's <c>PipeOptions.CurrentUserOnly</c> makes that check; .NET Framework has no such option, and another
/// local user could take the pipe's name for the moment it is re-created after an <see cref="IOException"/>. There the
/// client asks Windows: <c>GetNamedPipeServerProcessId</c> on the connected handle, then that process's token's owner, and
/// compares it with the current user's SID before anything, the launch's secret included, is sent. Any failure refuses
/// (fail closed), with the error a refused user gives on .NET 8: an <see cref="UnauthorizedAccessException"/>.
/// </summary>
internal static class PipeServerOwner
{
    /// <summary>The two questions Windows answers; tests replace them to make a call fail or name another user.</summary>
    internal interface IOwnerSource
    {
        /// <summary>The SID that owns the process serving <paramref name="pipe"/>; false when any call fails.</summary>
        bool TryGetServerOwner(SafePipeHandle pipe, out SecurityIdentifier? owner);
    }

    /// <summary>The Windows calls themselves (for tests).</summary>
    internal static IOwnerSource Windows => WindowsSource.Instance;

    /// <summary>Refuses unless the server of the connected <paramref name="pipe"/> runs as the current user. Windows only.</summary>
    public static void RequireCurrentUser(NamedPipeClientStream pipe)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        Require(pipe.SafePipeHandle, identity.User, WindowsSource.Instance);
    }

    /// <summary>The comparison and the fail-closed rule: no owner, no current user, or two different SIDs refuse.</summary>
    internal static void Require(SafePipeHandle pipe, SecurityIdentifier? current, IOwnerSource source)
    {
        if (current is null || !source.TryGetServerOwner(pipe, out var owner) || owner is null || !owner.Equals(current))
        {
            throw new UnauthorizedAccessException("The pipe is not served by the current user.");
        }
    }

    private sealed class WindowsSource : IOwnerSource
    {
        public static readonly WindowsSource Instance = new();

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const int TokenUserClass = 1;

        public bool TryGetServerOwner(SafePipeHandle pipe, out SecurityIdentifier? owner)
        {
            owner = null;
            try
            {
                if (pipe.IsInvalid || pipe.IsClosed || !GetNamedPipeServerProcessId(pipe, out var processId) || processId == 0)
                {
                    return false;
                }
                using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
                if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
                {
                    return false;
                }
                using (token)
                {
                    GetTokenInformation(token, TokenUserClass, IntPtr.Zero, 0, out var size); // asks how large; fails by design
                    if (size <= 0 || size > 4096)
                    {
                        return false;
                    }
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (!GetTokenInformation(token, TokenUserClass, buffer, size, out _))
                        {
                            return false;
                        }
                        // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the pointer to the SID.
                        var sid = Marshal.ReadIntPtr(buffer);
                        if (sid == IntPtr.Zero)
                        {
                            return false;
                        }
                        owner = new SecurityIdentifier(sid);
                        return true;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            catch (Exception e) when (e is Win32Exception or ArgumentException or DllNotFoundException or EntryPointNotFoundException
                or InvalidOperationException or ObjectDisposedException)
            {
                owner = null;
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern NativeHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(NativeHandle process, uint desiredAccess, out NativeHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(NativeHandle token, int informationClass, IntPtr information, int length, out int returnLength);
    }

    /// <summary>A process or token handle, closed with <c>CloseHandle</c>.</summary>
    private sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
