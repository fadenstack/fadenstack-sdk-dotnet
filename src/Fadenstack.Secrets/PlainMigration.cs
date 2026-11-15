using System.Security.Cryptography;
using Fadenstack.Auth;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Secrets;

/// <summary>
/// The one move of the files <see cref="OwnerOnlyFileProtector"/> wrote in the clear before the OS key store was on (sdk/51 F08
/// §1; review R1 and N5): the sign-ins (<c>tokens/*.token</c>), the "always allow" approvals and their seal key, shared and per
/// person. Each plain one is encrypted where it lies; then the move is recorded (<c>keystore/plain-files-moved</c>), and from then
/// on <see cref="OsKeyStoreProtector"/> refuses plain data: a plain file planted later is never read. A file that cannot be moved
/// now keeps the move open for the next start; nothing is ever deleted.
/// </summary>
internal static class PlainMigration
{
    internal const string MarkerName = "plain-files-moved";

    /// <summary>Moves the plain files under <paramref name="dataRoot"/>, once; true when the move is recorded (now or before).</summary>
    internal static bool Run(OsKeyStoreProtector protector, string dataRoot, ILogger? logger = null)
    {
        var marker = Path.Combine(dataRoot, "keystore", MarkerName);
        if (File.Exists(marker))
        {
            return true;
        }
        protector.AcceptsPlain = true;
        try
        {
            var moved = 0;
            foreach (var file in Candidates(dataRoot))
            {
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length == 0 || OsKeyStoreProtector.IsEncrypted(bytes))
                {
                    continue;
                }
                SecretProtection.WriteOwnerOnly(file, protector.Protect(bytes));
                moved++;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            SecretProtection.WriteOwnerOnly(marker, System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
            if (logger is not null)
            {
                Log.PlainFilesMoved(logger, moved);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            if (logger is not null)
            {
                Log.PlainMoveFailed(logger, e.GetType().Name);
            }
            return false;
        }
        finally
        {
            protector.AcceptsPlain = false;
        }
    }

    /// <summary>The files the owner-only protector writes: sign-ins, approvals and their seal key (shared, and in each person's folder).</summary>
    private static IEnumerable<string> Candidates(string dataRoot)
    {
        IEnumerable<string> In(string folder, string pattern) =>
            Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern) : Enumerable.Empty<string>();
        var people = Directory.Exists(Path.Combine(dataRoot, "users"))
            ? Directory.EnumerateDirectories(Path.Combine(dataRoot, "users")).ToList()
            : new List<string>();
        return In(Path.Combine(dataRoot, "tokens"), "*.token")
            .Concat(In(Path.Combine(dataRoot, "approvals"), "*.json"))
            .Concat(In(Path.Combine(dataRoot, "approvals"), "approvals-seal.key"))
            .Concat(people.SelectMany(p => In(Path.Combine(p, "approvals"), "*.json").Concat(In(Path.Combine(p, "approvals"), "approvals-seal.key"))))
            .ToList();
    }
}
