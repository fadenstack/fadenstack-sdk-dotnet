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
    /// <summary>No policy at all: everything the person may choose stays theirs.</summary>
    public static MachinePolicy None { get; } = new();

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

    /// <summary>The known keys, as a policy file names them.</summary>
    public static IReadOnlyList<string> Keys { get; } =
        ["server", "standalone", "endpoints", "plainHttp", "mcp", "skills", "webSearch", "timestamping"];

    /// <summary>Reads a policy file's JSON. Never throws on a value: what it does not understand narrows and is reported.</summary>
    public static MachinePolicy FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return From(doc.RootElement);
    }

    /// <summary>Reads a policy file; none there gives <see cref="None"/>. A file that is not JSON forbids what it could have allowed.</summary>
    public static MachinePolicy Load(string path)
    {
        if (!File.Exists(path))
        {
            return None;
        }
        try
        {
            return FromJson(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return Unreadable("The policy file is not valid JSON");
        }
    }

    /// <summary>An administrator's policy the SDK cannot read: fail closed, as for an unknown value.</summary>
    private static MachinePolicy Unreadable(string why) => new()
    {
        Standalone = PolicyAccess.Forbidden, PlainHttp = PolicyAccess.Forbidden, Mcp = PolicyRule.Forbidden,
        Skills = PolicyRule.Forbidden, WebSearch = PolicyAccess.Forbidden,
        Warnings = [why + ": standalone, local MCP, local skills, web search and plain http are forbidden."],
    };

    public static MachinePolicy From(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Unreadable("The policy is not a JSON object");
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
