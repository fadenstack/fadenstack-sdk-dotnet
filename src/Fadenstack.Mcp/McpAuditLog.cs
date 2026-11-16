using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fadenstack.Mcp;

/// <summary>
/// One change the person made to an MCP server that the device keeps for audit (sdk/51 F05): when, what, the server's source
/// (its origin, never a path, query or token), on or off, the version of the warning the person confirmed, and who.
/// </summary>
public sealed record McpAuditEntry
{
    /// <summary>
    /// The event: <c>local_mcp.plain_http</c> (the switch "Allow plain http on the local network"); or, for a program (sdk/51 5.5),
    /// <c>local_mcp.program_changed</c> (it was not started: <see cref="State"/> is <c>hash</c>, <c>path</c> or <c>missing</c>),
    /// <c>local_mcp.program_confirmed</c> (the person chose "Use the new program": <c>confirmed</c>) or
    /// <c>local_mcp.program_pinned</c> (an entry kept before pins was pinned at its first start: <c>first_start</c>).
    /// </summary>
    [JsonPropertyName("event")]
    public string Event { get; init; } = "";

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; init; }

    /// <summary>The server's origin (<c>http://host[:port]</c>); for a program event the server's label, never a path or an argument.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    /// <summary><c>on</c> or <c>off</c> for the plain-http switch; the kind of change for a program event.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    /// <summary>The version of the warning the person confirmed before turning it on; null when turning it off.</summary>
    [JsonPropertyName("warning_version")]
    public string? WarningVersion { get; init; }

    /// <summary>The Fadenstack user, or <c>standalone</c> (the OS user's own list).</summary>
    [JsonPropertyName("person")]
    public string Person { get; init; } = "";
}

/// <summary>
/// The device's local audit log of the person's MCP switches (sdk/51 F05): <c>audit.jsonl</c> beside the server list, one JSON
/// line per change, that rotates as the provenance placement log does (1 MiB, three older files kept). Nothing in it leaves
/// the device. A list kept only in memory keeps its log in memory too.
/// </summary>
public sealed class McpAuditLog
{
    public const string FileName = "audit.jsonl";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly object _gate = new();
    private readonly string? _file;
    private readonly long _maxBytes;
    private readonly int _keep;
    private readonly List<McpAuditEntry> _memory = [];

    /// <param name="folder">The folder of the log; null keeps it in memory.</param>
    /// <param name="maxBytes">The size at which the log moves to <c>.1</c>.</param>
    /// <param name="keep">How many older files are kept.</param>
    public McpAuditLog(string? folder, long maxBytes = 1024 * 1024, int keep = 3)
    {
        _file = folder is null ? null : Path.Combine(folder, FileName);
        _maxBytes = Math.Max(1, maxBytes);
        _keep = Math.Max(0, keep);
    }

    /// <summary>The entries kept in memory (a list that lives in memory); empty for a log on disk.</summary>
    public IReadOnlyList<McpAuditEntry> InMemory
    {
        get
        {
            lock (_gate)
                return [.. _memory];
        }
    }

    public void Append(McpAuditEntry entry)
    {
        lock (_gate)
        {
            if (_file is null)
            {
                _memory.Add(entry);
                return;
            }
            var line = JsonSerializer.Serialize(entry, Json) + "\n";
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            if (File.Exists(_file) && new FileInfo(_file).Length + Encoding.UTF8.GetByteCount(line) > _maxBytes)
                Rotate();
            File.AppendAllText(_file, line, new UTF8Encoding(false));
        }
    }

    /// <summary>audit.jsonl becomes .1, .1 becomes .2, ...; the oldest beyond the ones kept goes.</summary>
    private void Rotate()
    {
        if (_keep == 0)
        {
            File.Delete(_file!);
            return;
        }
        var oldest = _file + "." + _keep;
        if (File.Exists(oldest))
            File.Delete(oldest);
        for (var i = _keep - 1; i >= 1; i--)
        {
            var from = _file + "." + i;
            if (File.Exists(from))
                File.Move(from, _file + "." + (i + 1));
        }
        File.Move(_file!, _file + ".1");
    }
}
