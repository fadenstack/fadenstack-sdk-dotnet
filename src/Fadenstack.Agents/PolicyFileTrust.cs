using System.Security.AccessControl;
using System.Security.Principal;

namespace Fadenstack.Agents;

/// <summary>
/// Whether a machine policy file on Windows is IT's (OWASP ASVS 5.0 V13.1: configuration only from a trusted source). Every user may
/// create folders and files in <c>%ProgramData%</c>, and the creator owns them. Where IT has deployed no policy, another user of the
/// computer (a shared PC, a terminal server) could write an app's <c>policy.json</c> there and send everybody's agent to a server of
/// theirs, or switch their own AI off. So <see cref="MachinePolicy.Load(string)"/> reads the file only when:
/// <list type="bullet">
/// <item>the file is owned by SYSTEM, the Administrators or TrustedInstaller, and nobody else may write, delete or re-permission it;</item>
/// <item>each folder from the file up to <c>%ProgramData%</c> is owned by one of them, and nobody else may delete in it or
/// re-permission it (creating files in it is what Windows lets every user do there; an existing file owned by IT stays IT's).</item>
/// </list>
/// A deployment by an administrator, by Intune or by a group policy (as SYSTEM) passes. The desktop agent's check, shared
/// (security review of 17 Nov 2026). Windows only: elsewhere the app's policy folder is root's alone.
/// </summary>
#if NET5_0_OR_GREATER
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
public static class PolicyFileTrust
{
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    private static readonly FileSystemRights FileWrites = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly FileSystemRights FolderWrites = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>
    /// Why <paramref name="file"/> is not IT's, or null when it is: the file, and each folder above it up to (not including)
    /// <paramref name="root"/>; a file outside <paramref name="root"/> is judged with its own folder only.
    /// </summary>
    public static string? Check(string file, string root)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            throw new ArgumentException("A policy file is needed.", nameof(file));
        }
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A root folder is needed.", nameof(root));
        }
        var info = new FileInfo(file);
        if (Judge(info.FullName, info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), isFile: true) is { } why)
        {
            return why;
        }
        var top = Trim(Path.GetFullPath(root));
        for (var folder = info.Directory; folder is not null; folder = folder.Parent)
        {
            var full = Trim(folder.FullName);
            if (string.Equals(full, top, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (Judge(full, folder.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), isFile: false) is { } bad)
            {
                return bad;
            }
            if (!full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                break; // not below the root: the file and its own folder are what can be judged
            }
        }
        return null;
    }

    /// <summary>Why the object at <paramref name="name"/> is not IT's, by its owner and the rights its allow entries grant to others.</summary>
    public static string? Judge(string name, FileSystemSecurity security, bool isFile)
    {
        if (security is null)
        {
            throw new ArgumentNullException(nameof(security));
        }
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsAdministration(owner))
        {
            return $"{name} is not owned by SYSTEM or the Administrators";
        }
        var writes = isFile ? FileWrites : FolderWrites;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
            {
                continue; // a deny takes away; an inherit-only entry is for what is created inside, not for this object
            }
            if (rule.IdentityReference is not SecurityIdentifier who || IsAdministration(who) || IsOwnerPlaceholder(who))
            {
                continue;
            }
            var rights = (int)rule.FileSystemRights;
            if ((rights & (int)writes) != 0 || (rights & GenericAll) != 0 || (isFile && (rights & GenericWrite) != 0))
            {
                return $"{name} can be changed by {who.Value}";
            }
        }
        return null;
    }

    private static string Trim(string path) =>
        path.Length > 3 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path; // "C:\" stays

    private static bool IsAdministration(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid == TrustedInstaller;

    // CREATOR OWNER and OWNER RIGHTS stand for the owner, who is checked above.
    private static bool IsOwnerPlaceholder(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) || sid.Value == "S-1-3-4";
}
