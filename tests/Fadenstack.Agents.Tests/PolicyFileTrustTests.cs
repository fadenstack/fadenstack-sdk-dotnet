using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// A machine policy file counts only when it is IT's (security review of 17 Nov 2026, found by the desktop session; OWASP ASVS 5.0
/// V13.1). Every user may create files in %ProgramData%, so another user of a shared computer could otherwise write an app's
/// policy.json and redirect or narrow everybody's agent.
/// </summary>
#if NET5_0_OR_GREATER
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
public sealed class PolicyFileTrustTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "faden-policy-trust-" + Guid.NewGuid().ToString("N"));

    public PolicyFileTrustTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // a scanner may hold a file a moment; the temporary folder is cleaned later
        }
    }

    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static SecurityIdentifier Users => new(WellKnownSidType.BuiltinUsersSid, null);

    private static SecurityIdentifier Admins => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static SecurityIdentifier LocalSystem => new(WellKnownSidType.LocalSystemSid, null);

    private static SecurityIdentifier SomeUser => new("S-1-5-21-1000000001-1000000002-1000000003-1001");

    private string File(string json)
    {
        var path = Path.Combine(_folder, "policy.json");
        System.IO.File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void A_file_that_is_not_ITs_is_not_read_at_all_neither_its_server_nor_a_narrowing()
    {
        // Read as if there were none: not the policy that forbids (that would let the other user switch this person's own AI off),
        // and the reason for the app's log.
        var path = File("""{ "server": "https://evil.example", "standalone": "forbidden" }""");

        var policy = MachinePolicy.Load(path, _ => "not IT's", out var ignored);

        Assert.Same(MachinePolicy.None, policy);
        Assert.Null(policy.Server);
        Assert.Equal(PolicyAccess.Allowed, policy.Standalone);
        Assert.Equal("not IT's", ignored);
    }

    [Fact]
    public void A_file_IT_owns_is_read()
    {
        var path = File("""{ "server": "https://ai.plant.example" }""");

        var policy = MachinePolicy.Load(path, _ => null, out var ignored);

        Assert.Equal("https://ai.plant.example", policy.Server?.ToString().TrimEnd('/'));
        Assert.Null(ignored);
    }

    [Fact]
    public void On_Windows_a_policy_file_a_user_wrote_is_not_ITs()
    {
        if (!OnWindows)
        {
            return;
        }
        var path = File("""{ "server": "https://evil.example" }""");

        var why = PolicyFileTrust.Check(path, Path.GetTempPath());
        var policy = MachinePolicy.Load(path, null, out var ignored); // the SDK's own check
        var plain = MachinePolicy.Load(path);

        Assert.NotNull(why);
        Assert.Same(MachinePolicy.None, policy);
        Assert.Same(MachinePolicy.None, plain);
        Assert.Equal(why, ignored);
    }

    [Fact]
    public void A_file_owned_by_the_Administrators_that_only_they_and_SYSTEM_may_change_is_ITs()
    {
        if (!OnWindows)
        {
            return;
        }
        var file = new FileSecurity();
        file.SetOwner(Admins);
        file.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        file.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        file.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));

        Assert.Null(PolicyFileTrust.Judge("policy.json", file, isFile: true));
    }

    [Fact]
    public void A_file_owned_by_a_user_or_that_others_may_write_is_not_ITs()
    {
        if (!OnWindows)
        {
            return;
        }
        var owned = new FileSecurity();
        owned.SetOwner(SomeUser);
        var writable = new FileSecurity();
        writable.SetOwner(Admins);
        writable.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.WriteData, AccessControlType.Allow));
        var repermission = new FileSecurity();
        repermission.SetOwner(LocalSystem);
        repermission.AddAccessRule(new FileSystemAccessRule(SomeUser, FileSystemRights.ChangePermissions, AccessControlType.Allow));

        Assert.Contains("not owned", PolicyFileTrust.Judge("policy.json", owned, isFile: true));
        Assert.Contains(Users.Value, PolicyFileTrust.Judge("policy.json", writable, isFile: true));
        Assert.Contains(SomeUser.Value, PolicyFileTrust.Judge("policy.json", repermission, isFile: true));
    }

    [Fact]
    public void A_folder_where_users_may_only_create_files_as_ProgramData_lets_them_is_ITs_one_where_they_may_delete_is_not()
    {
        if (!OnWindows)
        {
            return;
        }

        Assert.Null(PolicyFileTrust.Judge("Office", Folder(FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.ReadAndExecute), isFile: false));
        Assert.NotNull(PolicyFileTrust.Judge("Office", Folder(FileSystemRights.DeleteSubdirectoriesAndFiles), isFile: false));
        Assert.NotNull(PolicyFileTrust.Judge("Office", Folder(FileSystemRights.FullControl), isFile: false));
        // An entry only for what is created inside does not apply to the folder itself.
        Assert.Null(PolicyFileTrust.Judge("Office", Folder(FileSystemRights.FullControl, PropagationFlags.InheritOnly), isFile: false));
    }

    private static DirectorySecurity Folder(FileSystemRights users, PropagationFlags propagation = PropagationFlags.None)
    {
        var folder = new DirectorySecurity();
        folder.SetOwner(Admins);
        folder.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        folder.AddAccessRule(new FileSystemAccessRule(Users, users, InheritanceFlags.ContainerInherit, propagation, AccessControlType.Allow));
        return folder;
    }
}
