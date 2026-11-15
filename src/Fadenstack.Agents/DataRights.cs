using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Http;
using Fadenstack.Storage;

namespace Fadenstack.Agents;

/// <summary>A store that can name the agents it holds data for, which the export walks (the stores' own folders).</summary>
internal interface IListsAgents
{
    IReadOnlyList<string> Agents();
}

/// <summary>
/// What <see cref="FadenstackAgent.DeleteMyDataAsync"/> did: the folders it removed, and whether a store the host passed was
/// left to the host (it may hold other people's data, so the SDK does not empty it; the host deletes this person's part).
/// </summary>
public sealed record DataDeletion(int FoldersRemoved, bool HostSessionStoreLeft, bool HostApprovalStoreLeft)
{
    /// <summary>Whether the person's data may still be in a store the host passed.</summary>
    public bool HostStoresLeft => HostSessionStoreLeft || HostApprovalStoreLeft;
}

/// <summary>What an export wrote.</summary>
internal sealed record ExportCounts(int Agents, int Sessions, int Approvals, int Unreadable)
{
    /// <summary>The other files of the person's folder that went in (preferences, skills, the MCP list, provenance).</summary>
    public int Files { get; init; }
}

/// <summary>
/// The export of a user's data on the device (an access request, GDPR Art. 15 and 20): sessions and "Allow always" approvals,
/// read through the stores -- so decrypted the way the stores decrypt them -- and everything else the deletion removes from the
/// person's folder (<c>users/&lt;user&gt;/</c>), by an allowlist: preferences, the language, skills, the MCP servers without what
/// can hold a secret, their audit log, the provenance records and placement log. Never tokens, keys or secret values; a file
/// the list does not know is named in the README, not exported.
/// </summary>
internal static class DataExport
{
    private static readonly JsonSerializerOptions Readable = new(FadenstackJson.Options) { WriteIndented = true };

    /// <summary>The MCP list's keys that hold no secret and go out as they are (the desktop's file, version 1, and its F05 keys).</summary>
    private static readonly string[] ServerKeys =
        { "label", "enabled", "command", "workingFolder", "callTimeoutSeconds", "origin", "trusted", "plainHttpInLan", "toolClasses" };

    /// <summary>
    /// Writes the ZIP to <paramref name="destination"/> (left open): <c>README.txt</c>, <c>sessions/&lt;agent&gt;/&lt;session&gt;.json</c>
    /// and <c>approvals/&lt;agent&gt;.json</c>, then the person's other files from <paramref name="personFolder"/> (null: a server that
    /// names no user, whose shared <c>preferences</c> in <paramref name="sharedPreferences"/> go in instead). A store that cannot
    /// list its agents (the host's own) is read for <paramref name="slug"/> only.
    /// </summary>
    internal static async Task<ExportCounts> WriteAsync(Stream destination, ISessionStore sessions, IApprovalStore approvals, string slug,
        CancellationToken cancellationToken, string? personFolder = null, string? sharedPreferences = null)
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
        var files = new PersonsFiles();
        if (personFolder is not null)
        {
            files.AddFolder(zip, personFolder);
        }
        else if (sharedPreferences is not null)
        {
            files.AddPreferences(zip, sharedPreferences);
        }
        var counts = new ExportCounts(sessionAgents.Union(approvalAgents).Count(), sessionCount, approvalCount, unreadable) { Files = files.Count };
        Add(zip, "README.txt", Readme(counts, files));
        return counts;
    }

    /// <summary>What a file of the person's folder is to the export.</summary>
    private enum Kind
    {
        /// <summary>Read through the stores (sessions, approvals).</summary>
        Store,
        /// <summary>Plain, no secret: it goes in as it is.</summary>
        Copy,
        /// <summary>The MCP list: decrypted, and only what holds no secret goes in.</summary>
        ServerList,
        /// <summary>A key: never exported.</summary>
        Secret,
        /// <summary>Nothing of the person's (a lock, a write in progress).</summary>
        Skip,
        /// <summary>Not on the list: named in the README, not exported.</summary>
        Unknown,
    }

    private static Kind KindOf(string relative)
    {
        var parts = relative.Split('/');
        var name = parts[parts.Length - 1];
        if (name.StartsWith(".", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return Kind.Skip; // SecretProtection.WriteOwnerOnly's file before its rename
        }
        return parts[0] switch
        {
            "sessions" or "approvals" => Kind.Store,
            "keys" => Kind.Secret,
            "language" when parts.Length == 1 => Kind.Copy,
            "preferences" when parts.Length == 2 && name.EndsWith(".json", StringComparison.Ordinal) => Kind.Copy,
            "skills" when parts.Length > 1 => Kind.Copy,
            "mcp" when relative == "mcp/servers.dat" => Kind.ServerList,
            "mcp" when relative == "mcp/servers.dat.lock" => Kind.Skip,
            "mcp" when parts.Length == 2 && IsLog(name, "audit.jsonl") => Kind.Copy,
            "provenance" when parts.Length == 3 && parts[1] == "records" && name.EndsWith(".json", StringComparison.Ordinal) => Kind.Copy,
            "provenance" when parts.Length == 2 && IsLog(name, "placements.jsonl") => Kind.Copy,
            _ => Kind.Unknown,
        };
    }

    /// <summary>The log or one of its rotations (<c>.1</c>, <c>.2</c>, ...).</summary>
    private static bool IsLog(string name, string log) =>
        name == log || (name.StartsWith(log + ".", StringComparison.Ordinal) && name.Substring(log.Length + 1).All(char.IsDigit));

    /// <summary>The person's files beyond sessions and approvals, as they go into the ZIP.</summary>
    private sealed class PersonsFiles
    {
        public int Count { get; private set; }

        /// <summary>The top folders (or files) something went in from: the README names only those.</summary>
        public SortedSet<string> Kinds { get; } = new(StringComparer.Ordinal);

        public int Servers { get; private set; }

        public bool KeyLeft { get; private set; }

        public List<string> Left { get; } = new();

        public void AddFolder(ZipArchive zip, string folder)
        {
            if (!Directory.Exists(folder))
            {
                return;
            }
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var relative = path.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                switch (KindOf(relative))
                {
                    case Kind.Copy:
                        Copy(zip, path, relative);
                        break;
                    case Kind.ServerList:
                        AddServers(zip, path, relative);
                        break;
                    case Kind.Secret:
                        KeyLeft = true;
                        break;
                    case Kind.Unknown:
                        Left.Add(relative);
                        break;
                }
            }
        }

        /// <summary>A server that names no user: the shared preferences of an older SDK (the thinking level).</summary>
        public void AddPreferences(ZipArchive zip, string folder)
        {
            if (!Directory.Exists(folder))
            {
                return;
            }
            foreach (var path in Directory.EnumerateFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                Copy(zip, path, "preferences/" + Path.GetFileName(path));
            }
        }

        private void Copy(ZipArchive zip, string path, string relative)
        {
            byte[] bytes;
            try
            {
                bytes = SecretProtection.ReadShared(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Left.Add(relative);
                return;
            }
            using (var entry = zip.CreateEntry(relative).Open())
            {
                entry.Write(bytes, 0, bytes.Length);
            }
            Count++;
            Kinds.Add(relative.Split('/')[0]);
        }

        // The list is decrypted as the MCP store decrypts it; each server goes out with the keys that hold no secret, the address
        // without what can (user and password, query, fragment), the number of arguments, and the names of its environment
        // variables and headers. A key this list does not know is left out, so a later secret never slips through.
        private void AddServers(ZipArchive zip, string path, string relative)
        {
            JsonArray servers;
            try
            {
                var file = JsonNode.Parse(SecretProtection.Default.Unprotect(SecretProtection.ReadShared(path))) as JsonObject;
                servers = file?["servers"] as JsonArray ?? new JsonArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException
                or InvalidOperationException or FormatException)
            {
                Left.Add(relative);
                return;
            }
            var safe = new JsonArray();
            foreach (var node in servers)
            {
                if (node is not JsonObject server)
                {
                    continue;
                }
                var item = new JsonObject();
                foreach (var key in ServerKeys)
                {
                    if (server[key] is { } value && Plain(value))
                    {
                        item[key] = JsonNode.Parse(value.ToJsonString());
                    }
                }
                if (Text(server["url"]) is { } url)
                {
                    item["url"] = SafeAddress(url);
                }
                item["argumentCount"] = (server["arguments"] as JsonArray)?.Count ?? 0;
                item["environmentNames"] = Names(server["environment"]);
                item["headerNames"] = Names(server["headers"]);
                safe.Add(item);
            }
            var json = new JsonObject { ["servers"] = safe }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            Add(zip, "mcp/servers.json", json);
            Count++;
            Servers = safe.Count;
            Kinds.Add("mcp");
        }

        /// <summary>A value of the allowed keys: a string, number, bool, or an object of strings (the raises); nothing nested deeper.</summary>
        private static bool Plain(JsonNode value) => value switch
        {
            JsonValue => true,
            JsonObject map => map.All(p => p.Value is JsonValue v && v.TryGetValue<string>(out _)),
            _ => false,
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static JsonArray Names(JsonNode? pairs)
        {
            var names = new JsonArray();
            foreach (var pair in pairs as JsonArray ?? new JsonArray())
            {
                if (Text(pair?["name"]) is { } name)
                {
                    names.Add(name);
                }
            }
            return names;
        }

        /// <summary>The address without a user and password, a query or a fragment, where tokens are often put; unparsable: left out.</summary>
        private static string SafeAddress(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
                : "";
    }

    private static IReadOnlyList<string> Agents(object store, string slug) =>
        store is IListsAgents lister ? lister.Agents() : new[] { FadenstackPaths.SafeName(slug) };

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string Readme(ExportCounts counts, PersonsFiles files)
    {
        var text = new StringBuilder()
            .AppendLine("Your Fadenstack data on this device")
            .AppendLine($"Exported {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC.")
            .AppendLine()
            .AppendLine("Included")
            .AppendLine($"- sessions/<agent>/<session>.json: {counts.Sessions} chat session(s) of {counts.Agents} agent(s), as this device stores them")
            .AppendLine("  (every entry of the transcript, decrypted).")
            .AppendLine($"- approvals/<agent>.json: {counts.Approvals} tool(s) you allowed always on this device.");
        foreach (var kind in files.Kinds)
        {
            text.AppendLine(kind switch
            {
                "preferences" => "- preferences/: your settings on this device, such as how long each agent may think and the skills you turned off.",
                "language" => "- language: the language you picked for your own AI.",
                "skills" => "- skills/: the skills you wrote or added, as their files.",
                "mcp" => string.Join(Environment.NewLine,
                    $"- mcp/servers.json: your {files.Servers} MCP server(s): the label, the program or address, the settings, and the names of their",
                    "  environment variables and headers. Argument values, environment values, header values, and a user, password, query or",
                    "  fragment in an address are left out: they can hold passwords or tokens. mcp/audit.jsonl: when you switched plain http",
                    "  on or off for a server."),
                "provenance" => "- provenance/: the records of where your own AI's text went, as this device keeps them, and their log.",
                _ => $"- {kind}/",
            });
        }
        text.AppendLine();
        if (counts.Unreadable > 0)
        {
            text.AppendLine($"{counts.Unreadable} session file(s) could not be read (damaged, or stored for another user) and are not included.")
                .AppendLine();
        }
        text.AppendLine("Not included")
            .AppendLine("- Sign-in tokens and other secrets: they give access to your account and are never exported.");
        if (files.KeyLeft)
        {
            text.AppendLine("- The key this device signs your provenance records with: a secret, kept on the device.");
        }
        if (files.Left.Count > 0)
        {
            text.AppendLine("- Kept on this device and not exported (not known to this version, or not readable just now):");
            foreach (var left in files.Left)
            {
                text.AppendLine("  " + left);
            }
        }
        return text
            .AppendLine("- Anything the server keeps (accounts, requests it served, audit and usage records): ask the operator of your")
            .AppendLine("  Fadenstack server for it.")
            .ToString();
    }
}
