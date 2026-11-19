using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fadenstack.Agents.Audit;
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
/// What <see cref="FadenstackAgent.DeleteMyDataAsync(DataScope, CancellationToken)"/> did: the folders it removed, and whether a store the host passed was
/// left to the host (it may hold other people's data, so the SDK does not empty it; the host deletes this person's part).
/// </summary>
public sealed record DataDeletion(int FoldersRemoved, bool HostSessionStoreLeft, bool HostApprovalStoreLeft)
{
    /// <summary>Whether the person's data may still be in a store the host passed.</summary>
    public bool HostStoresLeft => HostSessionStoreLeft || HostApprovalStoreLeft;

    /// <summary>What was removed: this app's data, or all of the person's.</summary>
    public DataScope Scope { get; init; } = DataScope.AllApps;

    /// <summary>
    /// For <see cref="DataScope.ThisApp"/>: the other apps that use the person's data on this device, for which the shared data
    /// (skills, the MCP list, keys, provenance records, the person's other preferences) stayed. Empty: it went too.
    /// </summary>
    public IReadOnlyList<string> OtherApps { get; init; } = Array.Empty<string>();

    /// <summary>Whether the shared data stayed because other apps use it.</summary>
    public bool SharedDataKept => OtherApps.Count > 0;

    /// <summary>This app's agents whose data stayed because another app uses the same agent.</summary>
    public IReadOnlyList<string> AgentsKept { get; init; } = Array.Empty<string>();

    /// <summary>
    /// How many log files the removal deleted (<see cref="Fadenstack.Logging.FadenstackFileLoggerProvider"/>'s: this app's own, or with
    /// <see cref="DataScope.AllApps"/> every file in the user's log folder). Files, not folders: it is not part of <see cref="FoldersRemoved"/>.
    /// </summary>
    public int LogFilesRemoved { get; init; }

    /// <summary>Log files that stayed because another process holds them open or the OS refused; the host may ask the person to close that app.</summary>
    public int LogFilesLeft { get; init; }
}

/// <summary>What an export wrote.</summary>
internal sealed record ExportCounts(int Agents, int Sessions, int Approvals, int Unreadable)
{
    /// <summary>The other files of the person's folder that went in (preferences, skills, the MCP list, provenance).</summary>
    public int Files { get; init; }

    /// <summary>How many entries of the record of tool calls went in (<c>audit/tool-calls.csv</c>); null when the record was not part of this export.</summary>
    public int? AuditEntries { get; init; }

    /// <summary>Whether the record of tool calls was intact when it went in.</summary>
    public bool AuditIntact { get; init; } = true;
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
        CancellationToken cancellationToken, string? personFolder = null, string? sharedPreferences = null, AppScope? scope = null,
        IAuditLog? audit = null)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        // This app only, while other apps use the person's data: exactly what its removal takes (owner, 16 Nov).
        var only = scope is { Plan.SharedGoes: false } ? new HashSet<string>(scope.Plan.OwnAgents.Select(FadenstackPaths.SafeName), StringComparer.Ordinal) : null;
        var sessionAgents = Agents(sessions, slug).Where(a => only is null || only.Contains(a)).ToList();
        var approvalAgents = Agents(approvals, slug).Where(a => only is null || only.Contains(a)).ToList();
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
        if (personFolder is not null && only is not null)
        {
            files.AddAppsOwn(zip, personFolder, scope!);
        }
        else if (personFolder is not null)
        {
            files.AddFolder(zip, personFolder);
        }
        else if (sharedPreferences is not null && only is not null)
        {
            files.AddOwnReasoning(zip, Path.Combine(sharedPreferences, "reasoning.json"), scope!.Plan.OwnAgents);
        }
        else if (sharedPreferences is not null)
        {
            files.AddPreferences(zip, sharedPreferences);
        }
        // The record of tool calls is the person's shared data, like the provenance records: in this app's export only when no other
        // Fadenstack app uses the device (the same rule as its removal). Read through the log, so decrypted, with its verification on top.
        int? auditEntries = null;
        var auditIntact = true;
        if (audit is not null && only is null)
        {
            using var csv = new MemoryStream();
            await audit.ExportAsync(AuditExportFormat.Csv, csv, cancellationToken).ConfigureAwait(false);
            var verification = await audit.VerifyAsync(cancellationToken).ConfigureAwait(false);
            (auditEntries, auditIntact) = (verification.Entries, verification.Intact);
            using (var entry = zip.CreateEntry("audit/tool-calls.csv").Open())
            {
                entry.Write(csv.GetBuffer(), 0, (int)csv.Length);
            }
        }
        var counts = new ExportCounts(sessionAgents.Union(approvalAgents).Count(), sessionCount, approvalCount, unreadable)
        {
            Files = files.Count, AuditEntries = auditEntries, AuditIntact = auditIntact,
        };
        Add(zip, "README.txt", Readme(counts, files, scope));
        return counts;
    }

    /// <summary>The calling app and what its removal takes, for an export of <see cref="DataScope.ThisApp"/>.</summary>
    internal sealed record AppScope(string App, MyDataPlan Plan);

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
            "sessions" or "approvals" or "audit" => Kind.Store, // the audit log's lines are encrypted: it goes in through the log (audit/tool-calls.csv)
            "keys" => Kind.Secret,
            "language" when parts.Length == 1 => Kind.Copy,
            "apps" when parts.Length == 2 && name.EndsWith(".json", StringComparison.Ordinal) => Kind.Copy,
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

        /// <summary>
        /// This app only: its record and its agents' thinking levels (the rest of the person's files stays, other apps use it).
        /// </summary>
        public void AddAppsOwn(ZipArchive zip, string folder, AppScope scope)
        {
            var record = MyDataApps.PathOf(folder, scope.App);
            if (File.Exists(record))
            {
                Copy(zip, record, "apps/" + Path.GetFileName(record));
            }
            AddOwnReasoning(zip, Path.Combine(folder, "preferences", "reasoning.json"), scope.Plan.OwnAgents);
        }

        /// <summary>The agents' thinking levels from <paramref name="reasoning"/>, without the other agents'.</summary>
        public void AddOwnReasoning(ZipArchive zip, string reasoning, IReadOnlyList<string> agents)
        {
            try
            {
                if (File.Exists(reasoning)
                    && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(reasoning)) is { } all)
                {
                    var own = all.Where(p => agents.Any(a => FadenstackPaths.SafeName(a) == FadenstackPaths.SafeName(p.Key)))
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    if (own.Count > 0)
                    {
                        Add(zip, "preferences/reasoning.json", JsonSerializer.Serialize(own, Readable));
                        Count++;
                        Kinds.Add("preferences");
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                Left.Add("preferences/reasoning.json");
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

    private static string Readme(ExportCounts counts, PersonsFiles files, AppScope? scope)
    {
        var text = new StringBuilder()
            .AppendLine("Your Fadenstack data on this device")
            .AppendLine($"Exported {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC.")
            .AppendLine(scope is null ? "Scope: all of your Fadenstack data on this device, every app's."
                : scope.Plan.SharedGoes ? $"Scope: the data of {scope.App}, the only Fadenstack app that uses your data on this device: all of it."
                : $"Scope: the data of {scope.App} only: its chats, its \"Allow always\" and its settings.")
            .AppendLine()
            .AppendLine("Included")
            .AppendLine($"- sessions/<agent>/<session>.json: {counts.Sessions} chat session(s) of {counts.Agents} agent(s), as this device stores them")
            .AppendLine("  (every entry of the transcript, decrypted).")
            .AppendLine($"- approvals/<agent>.json: {counts.Approvals} tool(s) you allowed always on this device.");
        if (counts.AuditEntries is { } entries)
        {
            text.AppendLine($"- audit/tool-calls.csv: the record of tool calls this device keeps for you, {entries} entr{(entries == 1 ? "y" : "ies")}: what was run,")
                .AppendLine("  who or what let it run or stopped it, and what became of it. No prompt, answer, argument or result text is in it: only")
                .AppendLine("  a hash of each call's arguments. The first two lines say whether the record is " + (counts.AuditIntact ? "intact (it is)." : "intact (it is NOT: it is damaged)."));
        }
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
        if (scope is { Plan.SharedGoes: false, Plan.OtherApps.Count: 0 })
        {
            // A server that names no user: the data is the device's, shared by every agent on it.
            text.AppendLine("- The other agents' chats and the settings this device shares between Fadenstack apps. Export all of your")
                .AppendLine("  Fadenstack data to get them.");
        }
        else if (scope is { Plan.SharedGoes: false } plan)
        {
            text.AppendLine($"- What other Fadenstack apps on this device use ({string.Join(", ", plan.Plan.OtherApps.Select(a => a.Name))}): your skills,")
                .AppendLine("  your MCP servers, your provenance records, your record of tool calls and your other settings. Export all of your Fadenstack data to get them.");
            if (plan.Plan.KeptAgents.Count > 0)
            {
                text.AppendLine($"- The chats of {string.Join(", ", plan.Plan.KeptAgents)}, which another app uses too.");
            }
        }
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
