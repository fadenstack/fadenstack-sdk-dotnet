using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Fadenstack.Agents.Delegation;
using Microsoft.Win32.SafeHandles;
using Xunit;

#pragma warning disable CA1416 // every test returns first where the OS is not Windows

namespace Fadenstack.Agents.Delegation.Tests;

/// <summary>
/// The client checks who runs the pipe's server before it sends anything (OWASP ASVS 5.0 V8): on .NET Framework, which has no
/// <c>PipeOptions.CurrentUserOnly</c>, by the server's process owner; fail closed. Windows only.
/// </summary>
public sealed class PipeServerOwnerTests
{
    private static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private sealed class Source(SecurityIdentifier? owner, bool works = true) : PipeServerOwner.IOwnerSource
    {
        public int Asked { get; private set; }

        public bool TryGetServerOwner(SafePipeHandle pipe, out SecurityIdentifier? result)
        {
            Asked++;
            result = owner;
            return works;
        }
    }

    private static SecurityIdentifier Me()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    [Fact]
    public async Task A_pipe_served_by_this_process_is_accepted()
    {
        if (!Windows)
        {
            return;
        }
        var name = "faden-owner-" + DelegationPipes.RandomHex(8);
        using var server = DelegationPipes.CreateServer(name, 1);
        using var client = DelegationPipes.CreateClient(name);
        var accepted = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accepted;

        PipeServerOwner.RequireCurrentUser(client); // does not throw

        PipeServerOwner.Require(client.SafePipeHandle, Me(), PipeServerOwner.Windows);
    }

    [Fact]
    public async Task A_pipe_served_by_another_user_is_refused()
    {
        if (!Windows)
        {
            return;
        }
        var name = "faden-owner-" + DelegationPipes.RandomHex(8);
        using var server = DelegationPipes.CreateServer(name, 1);
        using var client = DelegationPipes.CreateClient(name);
        var accepted = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accepted;
        var other = new SecurityIdentifier("S-1-5-21-1-2-3-1001");

        Assert.Throws<UnauthorizedAccessException>(() => PipeServerOwner.Require(client.SafePipeHandle, other, PipeServerOwner.Windows));
    }

    [Fact]
    public void A_different_sid_refuses_and_the_same_sid_passes()
    {
        if (!Windows)
        {
            return;
        }
        var me = Me();
        var handle = new SafePipeHandle(IntPtr.Zero, false);

        PipeServerOwner.Require(handle, me, new Source(new SecurityIdentifier(me.Value)));
        Assert.Throws<UnauthorizedAccessException>(() => PipeServerOwner.Require(handle, me, new Source(new SecurityIdentifier("S-1-5-18"))));
        Assert.Throws<UnauthorizedAccessException>(() => PipeServerOwner.Require(handle, me, new Source(new SecurityIdentifier("S-1-1-0"))));
    }

    [Fact]
    public void A_failed_call_or_a_missing_owner_or_a_missing_current_user_refuses()
    {
        if (!Windows)
        {
            return;
        }
        var me = Me();
        var handle = new SafePipeHandle(IntPtr.Zero, false);

        Assert.Throws<UnauthorizedAccessException>(() => PipeServerOwner.Require(handle, me, new Source(me, works: false)));
        Assert.Throws<UnauthorizedAccessException>(() => PipeServerOwner.Require(handle, me, new Source(null)));
        Assert.Throws<UnauthorizedAccessException>(() => PipeServerOwner.Require(handle, null, new Source(me)));
    }

    [Fact]
    public void A_handle_that_is_not_a_pipe_makes_the_native_call_fail_and_refuses()
    {
        if (!Windows)
        {
            return;
        }
        var path = Path.GetTempFileName();
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read);
            var notAPipe = new SafePipeHandle(file.SafeFileHandle.DangerousGetHandle(), ownsHandle: false);

            Assert.Throws<UnauthorizedAccessException>(() => PipeServerOwner.Require(notAPipe, Me(), PipeServerOwner.Windows));
            Assert.Throws<UnauthorizedAccessException>(() => PipeServerOwner.Require(new SafePipeHandle(IntPtr.Zero, false), Me(), PipeServerOwner.Windows));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
