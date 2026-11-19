using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fadenstack.Auth;
using Fadenstack.Storage;

namespace Fadenstack.Agents;

/// <summary>
/// Which of the person's data on this device an export or a removal covers (owner, 16 Nov: "Remove my data…" removes only
/// the calling app's data; a separate "Remove all" removes everything; the export follows the same scope).
/// </summary>
public enum DataScope
{
    /// <summary>
    /// This app's own data: the chats and "Allow always" of its agents and their preferences. The person's shared data
    /// (skills, the MCP list, keys, provenance records, the person's other preferences) only when no other Fadenstack app on
    /// the device uses the person's data.
    /// </summary>
    ThisApp,

    /// <summary>All of the person's Fadenstack data on this device: every app's.</summary>
    AllApps,
}

/// <summary>
/// An app that uses the person's data on this device (<see cref="FadenstackAgent.MyDataAppsAsync"/>): its name (what the host
/// described, <see cref="AgentBuilder.DescribeApp"/>, or the agent's slug), its agents, when it last started, whether it is the
/// calling app, and whether it recorded itself (<see cref="Recorded"/> false: an app on an older SDK, known only by an agent's
/// data, named by that agent).
/// </summary>
public sealed record MyDataApp(string Name, IReadOnlyList<string> Agents, DateTimeOffset? LastSeen, bool IsThisApp, bool Recorded)
{
    /// <summary>
    /// The app's name in the panel's languages, when the app gave them (<see cref="AppDescription.DisplayNames"/>); only the
    /// panel's texts use it, through <see cref="AppNames.DisplayName"/>. Null when the app gave none, or is on an older SDK.
    /// </summary>
    public IReadOnlyDictionary<string, string>? DisplayNames { get; init; }
}

/// <summary>An app's name as the panel's texts say it: the display names per language (<see cref="AppDescription.DisplayNames"/>) and the fallback to the name.</summary>
public static class AppNames
{
    /// <summary>The languages the panel's texts come in, as the keys of <see cref="AppDescription.DisplayNames"/>.</summary>
    public static IReadOnlyList<string> Languages { get; } = new[] { "en", "de", "fr", "es", "zh-Hans" };

    /// <summary>The longest display name kept, in characters.</summary>
    public const int MaxDisplayName = 200;

    /// <summary>
    /// The display names with only the known languages and non-blank text (trimmed, cut to <see cref="MaxDisplayName"/>
    /// characters); null when none is left.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? CleanDisplayNames(IReadOnlyDictionary<string, string>? value)
    {
        if (value is null)
        {
            return null;
        }
        var clean = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var language in Languages)
        {
            if (value.TryGetValue(language, out var text) && !string.IsNullOrWhiteSpace(text))
            {
                var trimmed = text.Trim();
                if (trimmed.Length > MaxDisplayName)
                {
                    trimmed = trimmed.Substring(0, char.IsHighSurrogate(trimmed[MaxDisplayName - 1]) ? MaxDisplayName - 1 : MaxDisplayName);
                }
                clean[language] = trimmed;
            }
        }
        return clean.Count > 0 ? clean : null;
    }

    /// <summary>
    /// The app's name for the panel's texts in <paramref name="language"/> ("de", "zh-Hans"; a region such as "de-AT" counts as
    /// "de"): its display name in that language, else <paramref name="name"/>. The manifest, the records and the record of tool
    /// calls keep the name.
    /// </summary>
    public static string DisplayName(string name, IReadOnlyDictionary<string, string>? displayNames, string? language)
    {
        if (displayNames is null || string.IsNullOrWhiteSpace(language))
        {
            return name;
        }
        var wanted = language!.Trim();
        var key = Languages.FirstOrDefault(l => string.Equals(l, wanted, StringComparison.OrdinalIgnoreCase))
            ?? Languages.FirstOrDefault(l => wanted.StartsWith(l + "-", StringComparison.OrdinalIgnoreCase));
        return key is not null && displayNames.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text : name;
    }
}

/// <summary>What a removal of <see cref="DataScope.ThisApp"/> takes and leaves, worked out before anything is deleted.</summary>
internal sealed record MyDataPlan(IReadOnlyList<string> OwnAgents, IReadOnlyList<string> KeptAgents, IReadOnlyList<MyDataApp> OtherApps)
{
    /// <summary>
    /// No other app uses the person's data: the shared data goes too, and with it the person's whole folder. Never for a server
    /// that names no user (<see cref="NoUser"/>).
    /// </summary>
    public bool SharedGoes { get; init; } = OtherApps.Count == 0;

    /// <summary>
    /// A server that names no user: the shared folders of an older SDK hold no app records, and every agent on the device uses
    /// them. This app's part is the agent's own data (its chats, "Allow always" and thinking level); the rest stays, for the
    /// removal and the export alike.
    /// </summary>
    public static MyDataPlan NoUser(string agent) =>
        new(new[] { agent }, Array.Empty<string>(), Array.Empty<MyDataApp>()) { SharedGoes = false };
}

/// <summary>
/// The apps that use a person's data on this device. Each agent start writes a small record per app in the person's folder
/// (<c>apps/&lt;app&gt;.json</c>: the app's name, its agents, when it was first and last seen; no content). An app on an older
/// SDK writes none: it is known by the data of an agent no record names.
/// </summary>
internal static class MyDataApps
{
    internal const string Folder = "apps";

    private sealed class AppRecord
    {
        public string App { get; set; } = "";
        public List<string> Agents { get; set; } = new();
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }

        /// <summary>The app's names in the panel's languages (the latest start's); an older SDK reading the file ignores it.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? DisplayNames { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>The app's name as the records and the removal use it: what the host described, else the agent's slug.</summary>
    internal static string NameOf(AppDescription? app, string slug) => string.IsNullOrWhiteSpace(app?.Name) ? slug : app!.Name.Trim();

    /// <summary>
    /// Records that <paramref name="app"/> ran <paramref name="agent"/> for the person. Never fails the start: a record that
    /// cannot be written leaves the app to be inferred from its data, which keeps the shared data (the safe side).
    /// </summary>
    internal static void TryRecord(string personFolder, string app, string agent, DateTimeOffset now,
        IReadOnlyDictionary<string, string>? displayNames = null)
    {
        try
        {
            var path = PathOf(personFolder, app);
            var record = Read(path) ?? new AppRecord { App = app, FirstSeen = now };
            record.App = app;
            if (!record.Agents.Contains(agent, StringComparer.Ordinal))
            {
                record.Agents.Add(agent);
            }
            record.LastSeen = now;
            var names = AppNames.CleanDisplayNames(displayNames); // the latest start says what the app calls itself now
            record.DisplayNames = names is null ? null : names.ToDictionary(n => n.Key, n => n.Value, StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SecretProtection.WriteOwnerOnly(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // inferred from its data instead
        }
    }

    internal static string PathOf(string personFolder, string app) =>
        Path.Combine(personFolder, Folder, FadenstackPaths.SafeName(app) + ".json");

    /// <summary>The apps that use the person's data, this app first: the records, then each agent with data that no record names.</summary>
    internal static IReadOnlyList<MyDataApp> List(string personFolder, string thisApp, string thisAgent,
        IReadOnlyDictionary<string, string>? thisDisplayNames = null)
    {
        var records = Records(personFolder);
        var thisKey = FadenstackPaths.SafeName(thisApp);
        var apps = new List<MyDataApp>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in records.OrderBy(r => r.Key == thisKey ? 0 : 1).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            var (key, record) = (pair.Key, pair.Value); // netstandard2.0 has no KeyValuePair.Deconstruct
            var agents = key == thisKey ? record.Agents.Union(new[] { thisAgent }, StringComparer.Ordinal).ToList() : record.Agents;
            named.UnionWith(agents.Select(FadenstackPaths.SafeName));
            var displayNames = AppNames.CleanDisplayNames(key == thisKey ? AppNames.CleanDisplayNames(thisDisplayNames) ?? record.DisplayNames : record.DisplayNames); // this app's own, current
            apps.Add(new MyDataApp(record.App, agents, record.LastSeen, key == thisKey, Recorded: true) { DisplayNames = displayNames });
        }
        if (!records.ContainsKey(thisKey))
        {
            named.Add(FadenstackPaths.SafeName(thisAgent));
            apps.Insert(0, new MyDataApp(thisApp, new[] { thisAgent }, null, IsThisApp: true, Recorded: false) { DisplayNames = AppNames.CleanDisplayNames(thisDisplayNames) });
        }
        foreach (var agent in AgentsWithData(personFolder).Where(a => !named.Contains(a)).OrderBy(a => a, StringComparer.Ordinal))
        {
            apps.Add(new MyDataApp(agent, new[] { agent }, LastWrite(personFolder, agent), IsThisApp: false, Recorded: false));
        }
        return apps;
    }

    /// <summary>
    /// What a removal of this app's data takes: its agents' data, except an agent another app's record also names (that one
    /// stays); the shared data only when no other app -- recorded, or inferred from an agent's data -- uses the person's data.
    /// </summary>
    internal static MyDataPlan Plan(string personFolder, string thisApp, string thisAgent)
    {
        var apps = List(personFolder, thisApp, thisAgent);
        var mine = apps.Where(a => a.IsThisApp).SelectMany(a => a.Agents).Distinct(StringComparer.Ordinal).ToList();
        var others = apps.Where(a => !a.IsThisApp).ToList();
        var theirs = new HashSet<string>(others.SelectMany(a => a.Agents).Select(FadenstackPaths.SafeName), StringComparer.Ordinal);
        var own = mine.Where(a => !theirs.Contains(FadenstackPaths.SafeName(a))).ToList();
        var kept = mine.Where(a => theirs.Contains(FadenstackPaths.SafeName(a))).ToList();
        return new MyDataPlan(own, kept, others);
    }

    /// <summary>
    /// Removes what <paramref name="plan"/> takes from the person's folder and says how many items went: each own agent's
    /// sessions and "Allow always", its thinking level, this app's record; with <see cref="MyDataPlan.SharedGoes"/>, the whole
    /// folder. The approvals' seal key goes once no approvals are left.
    /// </summary>
    internal static int Remove(string personFolder, string thisApp, MyDataPlan plan)
    {
        if (plan.SharedGoes)
        {
            return FadenstackPaths.DeleteUnderData(personFolder) ? 1 : 0;
        }
        var removed = 0;
        foreach (var agent in plan.OwnAgents)
        {
            var safe = FadenstackPaths.SafeName(agent);
            var sessions = Path.Combine(personFolder, "sessions", safe);
            if (Directory.Exists(sessions))
            {
                FadenstackPaths.DeleteUnderData(sessions);
                removed++;
            }
            removed += DeleteFile(Path.Combine(personFolder, "approvals", safe + ".json"));
        }
        removed += ForgetReasoning(Path.Combine(personFolder, "preferences", "reasoning.json"), plan.OwnAgents);
        removed += DeleteFile(PathOf(personFolder, thisApp));
        var approvals = Path.Combine(personFolder, "approvals");
        if (Directory.Exists(approvals) && !Directory.EnumerateFiles(approvals, "*.json").Any())
        {
            removed += DeleteFile(Path.Combine(approvals, FileApprovalStore.SealKeyName));
        }
        return removed;
    }

    /// <summary>The agents with data in the person's folder, by their file names (<see cref="FadenstackPaths.SafeName"/>).</summary>
    internal static IReadOnlyCollection<string> AgentsWithData(string personFolder)
    {
        var agents = new SortedSet<string>(StringComparer.Ordinal);
        var sessions = Path.Combine(personFolder, "sessions");
        if (Directory.Exists(sessions))
        {
            foreach (var folder in Directory.EnumerateDirectories(sessions))
            {
                if (Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    agents.Add(Path.GetFileName(folder));
                }
            }
        }
        var approvals = Path.Combine(personFolder, "approvals");
        if (Directory.Exists(approvals))
        {
            foreach (var file in Directory.EnumerateFiles(approvals, "*.json"))
            {
                agents.Add(Path.GetFileNameWithoutExtension(file));
            }
        }
        foreach (var agent in Reasoning(Path.Combine(personFolder, "preferences", "reasoning.json")).Keys)
        {
            agents.Add(FadenstackPaths.SafeName(agent));
        }
        return agents;
    }

    /// <summary>The records in the person's folder, by file name; a damaged record still names its app (by the file), with no agents.</summary>
    private static Dictionary<string, AppRecord> Records(string personFolder)
    {
        var records = new Dictionary<string, AppRecord>(StringComparer.Ordinal);
        var folder = Path.Combine(personFolder, Folder);
        if (!Directory.Exists(folder))
        {
            return records;
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            records[key] = Read(file) ?? new AppRecord { App = key };
        }
        return records;
    }

    private static AppRecord? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppRecord>(SecretProtection.ReadShared(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Reasoning(string path)
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } all
                ? all : new Dictionary<string, string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Removes the agents' thinking levels from the file; the file goes when nothing is left in it.</summary>
    internal static int ForgetReasoning(string path, IReadOnlyList<string> agents)
    {
        var all = Reasoning(path);
        var gone = all.Keys.Where(k => agents.Any(a => FadenstackPaths.SafeName(a) == FadenstackPaths.SafeName(k))).ToList();
        if (gone.Count == 0)
        {
            return 0;
        }
        foreach (var key in gone)
        {
            all.Remove(key);
        }
        if (all.Count == 0)
        {
            return DeleteFile(path);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(all));
        return gone.Count;
    }

    private static DateTimeOffset? LastWrite(string personFolder, string agent)
    {
        var sessions = Path.Combine(personFolder, "sessions", agent);
        return Directory.Exists(sessions) ? new DateTimeOffset(Directory.GetLastWriteTimeUtc(sessions), TimeSpan.Zero) : null;
    }

    private static int DeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }
        File.Delete(path);
        return 1;
    }
}
