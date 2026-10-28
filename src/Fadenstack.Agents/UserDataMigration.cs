using System.Diagnostics;
using Fadenstack.Storage;

namespace Fadenstack.Agents;

/// <summary>
/// Adopts the sessions and approvals an older SDK kept in the shared data folder: the first user the SDK learns of on a
/// device, before any user has a folder of their own, gets them. Once any user folder exists nothing moves again --
/// one person's data must never go to another.
/// </summary>
internal static class UserDataMigration
{
    private static readonly string[] Folders = { "sessions", "approvals" };

    internal static void Adopt(string userId)
    {
        try
        {
            var legacy = Folders.Select(f => Path.Combine(FadenstackPaths.DataRoot, f)).Where(Directory.Exists).ToList();
            var users = Path.Combine(FadenstackPaths.DataRoot, "users");
            if (legacy.Count == 0 || (Directory.Exists(users) && Directory.EnumerateDirectories(users).Any()))
            {
                return;
            }
            var partition = FadenstackPaths.User(userId);
            foreach (var folder in legacy)
            {
                Directory.Move(folder, Path.Combine(partition, Path.GetFileName(folder)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not move the earlier sessions and approvals into the user's own folder ({e.Message}); they stay where they are.");
        }
    }
}
