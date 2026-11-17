using System.Runtime.InteropServices;
using System.Text.Json;

namespace Fadenstack.Agents;

/// <summary>A machine policy's word on a feature: allowed, forbidden, or allowed only for the listed items.</summary>
public enum PolicyAccess
{
    Allowed,
    Forbidden,

    /// <summary>Allowed only for <see cref="PolicyRule.Items"/> (MCP sources, skill folders, endpoints).</summary>
    Listed,
}

/// <summary>Whether records signed on the device get an RFC 3161 time-stamp, and from whom (F09, F10).</summary>
public enum TimestampingMode
{
    /// <summary>From the SDK's default authority (DigiCert).</summary>
    On,

    /// <summary>None: nothing leaves the device, and a record keeps the device's clock.</summary>
    Off,

    /// <summary>From the organisation's own time-stamp server (<see cref="TimestampingPolicy.Server"/>).</summary>
    OwnServer,
}

/// <summary>The policy's <c>timestamping</c>: <c>on</c>, <c>off</c>, or the address of an own time-stamp server.</summary>
public sealed record TimestampingPolicy(TimestampingMode Mode, Uri? Server)
{
    public static TimestampingPolicy On { get; } = new(TimestampingMode.On, null);

    public static TimestampingPolicy Off { get; } = new(TimestampingMode.Off, null);
}

/// <summary>One key of a machine policy: its access and, for a list, the items.</summary>
public sealed record PolicyRule(PolicyAccess Access, IReadOnlyList<string> Items)
{
    public static PolicyRule Allowed { get; } = new(PolicyAccess.Allowed, Array.Empty<string>());

    public static PolicyRule Forbidden { get; } = new(PolicyAccess.Forbidden, Array.Empty<string>());

    /// <summary>Whether anything at all is allowed (a list counts when it names something).</summary>
    public bool AllowsAny => Access == PolicyAccess.Allowed || (Access == PolicyAccess.Listed && Items.Count > 0);

    /// <summary>Whether <paramref name="item"/> is allowed: everything when allowed, only a listed one when listed.</summary>
    public bool Allows(string item) => Access == PolicyAccess.Allowed
        || (Access == PolicyAccess.Listed && Items.Contains(item, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// What IT set for this machine (sdk/51 F09, D6, R3-8): one vocabulary for every app, read from the app's managed location
/// (Office's <c>%PROGRAMDATA%\Fadenstack\Office\policy.json</c>, the desktop's, Chrome's managed storage). It wins over the
/// person's settings and is enforced in the SDK (<see cref="AgentBuilder.UseMachinePolicy"/>), not only in an app's UI, so a
/// third-party app cannot bypass it.
/// </summary>
/// <remarks>
/// Fail closed: an unknown key is ignored and reported (<see cref="Warnings"/>); an unknown value narrows, to forbidden.
/// Where a <see cref="Server"/> is set, standalone, local MCP, local skills and web search are forbidden unless the policy
/// allows them (R3-8): an organisation that runs a server must not find "Use my own AI" in its users' agents.
/// </remarks>
public sealed record MachinePolicy
{
    /// <summary>The known keys, as a policy file names them (declared first: the policies below read it).</summary>
    public static IReadOnlyList<string> Keys { get; } =
        ["server", "standalone", "endpoints", "plainHttp", "mcp", "skills", "webSearch", "timestamping", "chatRetention", "contentLogging", "auditRetentionDays"];

    /// <summary>No policy at all: everything the person may choose stays theirs.</summary>
    public static MachinePolicy None { get; } = new();

    /// <summary>
    /// For a host whose policy source failed (a policy file or registry key it could not open, a managed store that threw):
    /// use it instead of <see cref="None"/>, which allows everything. It forbids what the person could add or turn on
    /// (standalone, local MCP, local skills, web search, plain http), turns time-stamps off (less leaves the device) and locks
    /// those keys; <c>server</c> stays open, so the person can still sign in to their organisation's server by hand.
    /// </summary>
    public static MachinePolicy Unreadable { get; } = Closed("The machine policy could not be read");

    /// <summary>The organisation's server: the app enrols, and the person signs in at the next start.</summary>
    public Uri? Server { get; init; }

    /// <summary>"Use my own AI": forbidden by default where <see cref="Server"/> is set, allowed otherwise.</summary>
    public PolicyAccess Standalone { get; init; } = PolicyAccess.Allowed;

    /// <summary>The endpoints a standalone agent may use (base URLs or origins); null: any.</summary>
    public IReadOnlyList<string>? Endpoints { get; init; }

    /// <summary>Plain http in the local network (D3's opt-in): forbidden here removes the opt-in. Loopback stays.</summary>
    public PolicyAccess PlainHttp { get; init; } = PolicyAccess.Allowed;

    /// <summary>Local MCP servers: allowed, forbidden, or the listed sources only.</summary>
    public PolicyRule Mcp { get; init; } = PolicyRule.Allowed;

    /// <summary>Local skills: allowed, forbidden, or the listed folders only.</summary>
    public PolicyRule Skills { get; init; } = PolicyRule.Allowed;

    /// <summary>The <c>web_search</c> tool.</summary>
    public PolicyAccess WebSearch { get; init; } = PolicyAccess.Allowed;

    /// <summary>
    /// RFC 3161 time-stamps for records signed on the device (only a hash leaves the device; F10): <c>on</c> (DigiCert, the
    /// default), <c>off</c>, or the address of the organisation's own time-stamp server.
    /// </summary>
    public TimestampingPolicy Timestamping { get; init; } = TimestampingPolicy.On;

    /// <summary>
    /// How long a standalone agent keeps old chats, fixed by IT (<c>chatRetention</c>: <c>autoDelete</c>, <c>maxAgeDays</c>,
    /// <c>maxSessions</c>); null when the policy leaves it to the person. A governed agent follows its server instead.
    /// </summary>
    public ChatRetentionChoice? ChatRetention { get; init; }

    /// <summary>
    /// Content logging (<c>contentLogging</c>, 8.1): <c>forbidden</c> means no app can switch prompts and answers into its logs, even for a
    /// time (<see cref="FadenstackAgent.StartContentLogging"/> throws). Absent: allowed, and the app and the person decide.
    /// </summary>
    public PolicyAccess ContentLogging { get; init; } = PolicyAccess.Allowed;

    /// <summary>
    /// How many days entries of the person's record of tool calls are kept (<c>auditRetentionDays</c>, 30 to 3650), fixed by IT; null:
    /// 180 days. A value out of bounds or not a whole number is reported and becomes 180. TS: <c>auditRetentionDays</c>.
    /// </summary>
    public int? AuditRetentionDays { get; init; }

    /// <summary>The keys the policy set itself (the app locks them in its UI and never writes them into the person's settings).</summary>
    public IReadOnlyCollection<string> Locked { get; init; } = Array.Empty<string>();

    /// <summary>What was ignored or narrowed while reading: unknown keys, unknown values (never a value itself).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Whether the policy allows a standalone agent on <paramref name="baseUrl"/>.</summary>
    public bool AllowsEndpoint(Uri baseUrl)
    {
        if (Standalone != PolicyAccess.Allowed)
        {
            return false;
        }
        if (Endpoints is null)
        {
            return true;
        }
        var origin = baseUrl.GetLeftPart(UriPartial.Authority);
        var base_ = baseUrl.AbsoluteUri.TrimEnd('/');
        return Endpoints.Any(e => Uri.TryCreate(e, UriKind.Absolute, out var allowed) && (
            string.Equals(allowed.AbsoluteUri.TrimEnd('/'), base_, StringComparison.OrdinalIgnoreCase)
            || (allowed.AbsolutePath == "/" && string.Equals(allowed.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Reads a policy file's JSON. Never throws on a value: what it does not understand narrows and is reported.</summary>
    public static MachinePolicy FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return From(doc.RootElement);
    }

    /// <summary>
    /// Reads a policy file; none there gives <see cref="None"/>. A file that is not JSON, or that exists but cannot be read
    /// (no access, locked), forbids what it could have allowed. On Windows a file that is not IT's (<see cref="PolicyFileTrust"/>:
    /// another user of the computer could have written it into <c>%ProgramData%</c>) is not read at all, as if there were none:
    /// nobody but IT can narrow or redirect the app, and a policy that forbids would let that user switch this person's own AI off.
    /// </summary>
    public static MachinePolicy Load(string path) => Load(path, null, out _);

    /// <summary>As <see cref="Load(string)"/>; <paramref name="ignored"/> says why a file was not read, for the app's log.</summary>
    /// <param name="path">The policy file.</param>
    /// <param name="untrusted">Why a file is not IT's, or null when it is; on Windows <see cref="PolicyFileTrust.Check"/> below
    /// <c>%ProgramData%</c> when null (a test passes its own).</param>
    /// <param name="ignored">Why the file was not read (it is not IT's); null when there was none or it was read.</param>
    public static MachinePolicy Load(string path, Func<string, string?>? untrusted, out string? ignored)
    {
        ignored = null;
        if (!File.Exists(path))
        {
            return None;
        }
        if ((untrusted ?? NotIts)(path) is { } why)
        {
            ignored = why;
            return None; // not IT's: read as if there were no file
        }
        try
        {
            return FromJson(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return Closed("The policy file is not valid JSON");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Closed("The policy file could not be read");
        }
    }

    /// <summary>This computer's check for <see cref="MachinePolicy.Load(string)"/>: below <c>%ProgramData%</c> on Windows, none elsewhere.</summary>
    private static string? NotIts(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null; // Linux and macOS: the app's policy folder (/etc/...) is root's alone
        }
        try
        {
            return PolicyFileTrust.Check(path, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Security.Principal.IdentityNotMappedException or PlatformNotSupportedException or SystemException)
        {
            return $"{path}: its owner and permissions could not be read ({e.GetType().Name})"; // ACLs that cannot be read: not IT's
        }
    }

    /// <summary>An administrator's policy the SDK cannot read: fail closed, as for an unknown value.</summary>
    private static MachinePolicy Closed(string why) => new()
    {
        Standalone = PolicyAccess.Forbidden, PlainHttp = PolicyAccess.Forbidden, Mcp = PolicyRule.Forbidden,
        Skills = PolicyRule.Forbidden, WebSearch = PolicyAccess.Forbidden, Timestamping = TimestampingPolicy.Off,
        ContentLogging = PolicyAccess.Forbidden,
        ChatRetention = ChatRetentionChoice.Default, // old chats are deleted as by default; the person cannot turn it off
        AuditRetentionDays = Audit.AuditRetention.DefaultDays,
        Locked = Keys.Where(k => k != "server").ToList(),
        Warnings = [why + ": standalone, local MCP, local skills, web search and plain http are forbidden; time-stamps are off."],
    };

    public static MachinePolicy From(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Closed("The policy is not a JSON object");
        }
        var warnings = new List<string>();
        var locked = new List<string>();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (Keys.Contains(property.Name, StringComparer.Ordinal))
            {
                values[property.Name] = property.Value;
                locked.Add(property.Name);
            }
            else
            {
                warnings.Add($"Unknown key '{property.Name}' ignored.");
            }
        }
        Uri? server = null;
        if (values.TryGetValue("server", out var s))
        {
            if (s.ValueKind == JsonValueKind.String && Uri.TryCreate(s.GetString(), UriKind.Absolute, out var url)
                && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))
            {
                server = url;
            }
            else
            {
                warnings.Add("'server' is not an http(s) address: ignored.");
            }
        }
        // R3-8: with a server, what the person could add on their own is forbidden unless the policy says otherwise.
        var byDefault = server is null ? PolicyAccess.Allowed : PolicyAccess.Forbidden;
        var byDefaultRule = server is null ? PolicyRule.Allowed : PolicyRule.Forbidden;
        return new MachinePolicy
        {
            Server = server,
            Standalone = Access(values, "standalone", byDefault, warnings),
            Endpoints = values.TryGetValue("endpoints", out var e) ? Strings(e, "endpoints", warnings) : null,
            PlainHttp = Access(values, "plainHttp", PolicyAccess.Allowed, warnings),
            Mcp = Rule(values, "mcp", byDefaultRule, warnings),
            Skills = Rule(values, "skills", byDefaultRule, warnings),
            WebSearch = Access(values, "webSearch", byDefault, warnings),
            Timestamping = Timestamps(values, warnings),
            ChatRetention = values.TryGetValue("chatRetention", out var retention) ? Retention(retention, warnings) : null,
            ContentLogging = Access(values, "contentLogging", PolicyAccess.Allowed, warnings),
            AuditRetentionDays = values.TryGetValue("auditRetentionDays", out var audit) ? AuditDays(audit, warnings) : null,
            Locked = locked,
            Warnings = warnings,
        };
    }

    private static PolicyAccess Access(Dictionary<string, JsonElement> values, string key, PolicyAccess fallback, List<string> warnings)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }
        switch (value.ValueKind == JsonValueKind.String ? value.GetString() : null)
        {
            case "allowed":
                return PolicyAccess.Allowed;
            case "forbidden":
                return PolicyAccess.Forbidden;
            default:
                warnings.Add($"'{key}' has a value the SDK does not know: forbidden.");
                return PolicyAccess.Forbidden;
        }
    }

    /// <summary><c>on</c>, <c>off</c>, or an http(s) address; anything else narrows to <c>off</c> (less leaves the device).</summary>
    private static TimestampingPolicy Timestamps(Dictionary<string, JsonElement> values, List<string> warnings)
    {
        if (!values.TryGetValue("timestamping", out var value))
        {
            return TimestampingPolicy.On;
        }
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        switch (text)
        {
            case "on":
                return TimestampingPolicy.On;
            case "off":
                return TimestampingPolicy.Off;
        }
        if (Uri.TryCreate(text, UriKind.Absolute, out var server) && (server.Scheme == Uri.UriSchemeHttps || server.Scheme == Uri.UriSchemeHttp)
            && server.UserInfo.Length == 0)
        {
            return new TimestampingPolicy(TimestampingMode.OwnServer, server);
        }
        warnings.Add("'timestamping' is neither on, off nor an http(s) address: off.");
        return TimestampingPolicy.Off;
    }

    /// <summary>
    /// <c>{ "autoDelete": true, "maxAgeDays": 30, "maxSessions": 100 }</c>. What it leaves out is the default (on, 90 days, 50 chats); a
    /// value that is not understood or out of bounds is reported and also becomes the default, so chats are not kept longer than that.
    /// </summary>
    private static ChatRetentionChoice Retention(JsonElement value, List<string> warnings)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("'chatRetention' is not an object: the defaults (on, 90 days, 50 chats) apply.");
            return ChatRetentionChoice.Default;
        }
        var autoDelete = true;
        int days = 90, chats = 50;
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "autoDelete":
                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        autoDelete = property.Value.GetBoolean();
                    }
                    else
                    {
                        warnings.Add("'chatRetention.autoDelete' is not true or false: on.");
                    }
                    break;
                case "maxAgeDays":
                    days = Limit(property.Value, "maxAgeDays", ChatRetentionChoice.IsValidDays, days, warnings);
                    break;
                case "maxSessions":
                    chats = Limit(property.Value, "maxSessions", ChatRetentionChoice.IsValidChats, chats, warnings);
                    break;
                default:
                    warnings.Add($"Unknown key 'chatRetention.{property.Name}' ignored.");
                    break;
            }
        }
        return new ChatRetentionChoice(autoDelete, days, chats);
    }

    private static int AuditDays(JsonElement value, List<string> warnings)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var days) && Audit.AuditRetention.IsValid(days))
        {
            return days;
        }
        warnings.Add($"'auditRetentionDays' is not a whole number from {Audit.AuditRetention.MinDays} to {Audit.AuditRetention.MaxDays}: the default ({Audit.AuditRetention.DefaultDays}).");
        return Audit.AuditRetention.DefaultDays;
    }

    private static int Limit(JsonElement value, string name, Func<int, bool> valid, int fallback, List<string> warnings)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && valid(number))
        {
            return number;
        }
        warnings.Add($"'chatRetention.{name}' is not a whole number within its bounds: the default ({fallback}).");
        return fallback;
    }

    private static PolicyRule Rule(Dictionary<string, JsonElement> values, string key, PolicyRule fallback, List<string> warnings)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            return new PolicyRule(PolicyAccess.Listed, Strings(value, key, warnings) ?? []);
        }
        return Access(values, key, PolicyAccess.Forbidden, warnings) == PolicyAccess.Allowed ? PolicyRule.Allowed : PolicyRule.Forbidden;
    }

    private static List<string>? Strings(JsonElement value, string key, List<string> warnings)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            warnings.Add($"'{key}' is not a list: nothing is allowed.");
            return [];
        }
        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                items.Add(text.Trim());
            }
            else
            {
                warnings.Add($"'{key}' holds an entry that is not text: left out.");
            }
        }
        return items;
    }
}

/// <summary>The machine policy refused what the app asked for (nothing was built).</summary>
public sealed class MachinePolicyException : FadenstackException
{
    public MachinePolicyException(string key, string message) : base(message)
    {
        Key = key;
    }

    /// <summary>The policy key that refused it (<c>standalone</c>, <c>endpoints</c>, ...).</summary>
    public string Key { get; }
}
