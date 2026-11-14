using Fadenstack.Storage;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Agents;

/// <summary>
/// Adopts the sessions and approvals an older SDK kept in the shared data folder: the first user the SDK learns of on a
/// device, before any user has a folder of their own, gets them. Once any user folder exists nothing moves again --
/// one person's data must never go to another. The standalone principal (sdk/51 F02, the data guard) never adopts them, and
/// its folder does not count as a user's: a governed person signing in later still gets their older chats, and a standalone
/// start never takes them.
/// </summary>
internal static class UserDataMigration
{
    private static readonly string[] Folders = { "sessions", "approvals" };

    /// <summary>Moves them for <paramref name="userId"/>, and logs the result (folder count or failure kind; never the user or a path).</summary>
    internal static void Adopt(string userId, ILogger logger)
    {
        if (string.Equals(userId, LocalAgentBackend.StandaloneId, StringComparison.Ordinal))
        {
            return;
        }
        try
        {
            var legacy = Folders.Select(f => Path.Combine(FadenstackPaths.DataRoot, f)).Where(Directory.Exists).ToList();
            var users = Path.Combine(FadenstackPaths.DataRoot, "users");
            var standalone = Path.GetFileName(FadenstackPaths.UserFolder(LocalAgentBackend.StandaloneId));
            if (legacy.Count == 0 || (Directory.Exists(users)
                && Directory.EnumerateDirectories(users).Any(d => !string.Equals(Path.GetFileName(d), standalone, StringComparison.Ordinal))))
            {
                return;
            }
            var partition = FadenstackPaths.User(userId);
            foreach (var folder in legacy)
            {
                Directory.Move(folder, Path.Combine(partition, Path.GetFileName(folder)));
            }
            Log.DataAdopted(logger, legacy.Count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.DataAdoptionFailed(logger, e.GetType().Name);
        }
    }
}
