using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Http;
using Fadenstack.Storage;

namespace Fadenstack.Agents;

/// <summary>A store that can name the agents it holds data for, which the export walks (the stores' own folders).</summary>
internal interface IListsAgents
{
    IReadOnlyList<string> Agents();
}

/// <summary>What an export wrote.</summary>
internal sealed record ExportCounts(int Agents, int Sessions, int Approvals, int Unreadable);

/// <summary>
/// The export of a user's data on the device (an access request): sessions and "Allow always" approvals, read through the
/// stores -- so decrypted the way the stores decrypt them -- into a ZIP. Never tokens or secrets.
/// </summary>
internal static class DataExport
{
    private static readonly JsonSerializerOptions Readable = new(FadenstackJson.Options) { WriteIndented = true };

    /// <summary>
    /// Writes the ZIP to <paramref name="destination"/> (left open): <c>README.txt</c>, <c>sessions/&lt;agent&gt;/&lt;session&gt;.json</c>
    /// and <c>approvals/&lt;agent&gt;.json</c>. A store that cannot list its agents (the host's own) is read for <paramref name="slug"/> only.
    /// </summary>
    internal static async Task<ExportCounts> WriteAsync(Stream destination, ISessionStore sessions, IApprovalStore approvals, string slug,
        CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var sessionAgents = Agents(sessions, slug);
        var approvalAgents = Agents(approvals, slug);
        int sessionCount = 0, approvalCount = 0, unreadable = 0;
        foreach (var agent in sessionAgents)
        {
            foreach (var info in await sessions.ListAsync(agent, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var entries = await sessions.ReadAsync(agent, info.Id, cancellationToken).ConfigureAwait(false);
                    Add(zip, $"sessions/{agent}/{FadenstackPaths.SafeName(info.Id)}.json", JsonSerializer.Serialize(entries, Readable));
                    sessionCount++;
                }
                catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
                {
                    unreadable++; // damaged, or another user's: it cannot be read, so it cannot be handed over
                }
            }
        }
        foreach (var agent in approvalAgents)
        {
            var list = await approvals.ListAsync(agent, cancellationToken).ConfigureAwait(false);
            if (list.Count > 0)
            {
                Add(zip, $"approvals/{agent}.json", JsonSerializer.Serialize(list, Readable));
                approvalCount += list.Count;
            }
        }
        var counts = new ExportCounts(sessionAgents.Union(approvalAgents).Count(), sessionCount, approvalCount, unreadable);
        Add(zip, "README.txt", Readme(counts));
        return counts;
    }

    private static IReadOnlyList<string> Agents(object store, string slug) =>
        store is IListsAgents lister ? lister.Agents() : new[] { FadenstackPaths.SafeName(slug) };

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string Readme(ExportCounts counts)
    {
        var text = new StringBuilder()
            .AppendLine("Your Fadenstack data on this device")
            .AppendLine($"Exported {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC.")
            .AppendLine()
            .AppendLine("Included")
            .AppendLine($"- sessions/<agent>/<session>.json: {counts.Sessions} chat session(s) of {counts.Agents} agent(s), as this device stores them")
            .AppendLine("  (every entry of the transcript, decrypted).")
            .AppendLine($"- approvals/<agent>.json: {counts.Approvals} tool(s) you allowed always on this device.")
            .AppendLine();
        if (counts.Unreadable > 0)
        {
            text.AppendLine($"{counts.Unreadable} session file(s) could not be read (damaged, or stored for another user) and are not included.")
                .AppendLine();
        }
        return text
            .AppendLine("Not included")
            .AppendLine("- Sign-in tokens and other secrets: they give access to your account and are never exported.")
            .AppendLine("- Anything the server keeps (accounts, requests it served, audit and usage records): ask the operator of your")
            .AppendLine("  Fadenstack server for it.")
            .ToString();
    }
}
