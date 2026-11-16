using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fadenstack.Agents.Audit;

/// <summary>What an audit entry is about. Only <see cref="Tool"/> exists today; the field stays so later kinds fit. TS: <c>AuditKinds</c>.</summary>
public static class AuditKinds
{
    /// <summary>A tool call: who or what let it run (or stopped it) and what became of it.</summary>
    public const string Tool = "tool";
}

/// <summary>
/// Who or what let a tool call run, or stopped it (<see cref="AuditRecord.Decision"/>). The SDK writes exactly these; a later
/// version may add more, so a reader treats an unknown one as "other". TS: <c>AuditDecisions</c>.
/// </summary>
public static class AuditDecisions
{
    /// <summary>The person was asked and allowed it (the scope says for how long).</summary>
    public const string AskedApproved = "asked_approved";

    /// <summary>The person was asked and declined.</summary>
    public const string AskedDenied = "asked_denied";

    /// <summary>The person was asked and did not answer in time: the call did not run.</summary>
    public const string AskedTimeout = "asked_timeout";

    /// <summary>An approval the person gave earlier stood: "Allow always" on this device, or "for this chat".</summary>
    public const string SavedApproval = "saved_approval";

    /// <summary>Nobody was asked: the chat's tool mode lets this class of tool run.</summary>
    public const string Mode = "mode";

    /// <summary>The server's rule for host tools said allow, and nobody was asked.</summary>
    public const string RuleAllowed = "rule_allowed";

    /// <summary>A rule refused it: the server's deny rule, or the app's own hook.</summary>
    public const string RuleDenied = "rule_denied";

    /// <summary>A grant the server holds (a time grant, a stretch of work, or the chat's Bypass) let it run, and nobody was asked.</summary>
    public const string ServerGrant = "server_grant";

    /// <summary>
    /// The approval floor says the question is never skipped, and the app has no way to ask it (no approval handler): the call did
    /// not run. A question the person did answer is <see cref="AskedApproved"/> or <see cref="AskedDenied"/>.
    /// </summary>
    public const string Floor = "floor";

    /// <summary>The chat's tool mode (ReadOnly) hides this tool: the call was refused.</summary>
    public const string Hidden = "hidden";

    /// <summary>Nothing was decided: the tool does not exist, its arguments were not JSON, or the call stopped before a decision.</summary>
    public const string None = "none";
}

/// <summary>What became of a tool call (<see cref="AuditRecord.Outcome"/>). TS: <c>AuditOutcomes</c>.</summary>
public static class AuditOutcomes
{
    /// <summary>The tool ran and returned. For a gateway tool the server runs, the call was released to it.</summary>
    public const string Ran = "ran";

    /// <summary>The tool ran and failed.</summary>
    public const string Failed = "failed";

    /// <summary>Somebody or something refused it (the person, a rule, the mode).</summary>
    public const string Refused = "refused";

    /// <summary>The turn was stopped while the call waited or ran.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>It never ran and nobody refused it (unknown tool, arguments that were not JSON, a check that could not be made).</summary>
    public const string NotRun = "not_run";
}

/// <summary>What the person chose for how long an approval holds (<see cref="AuditRecord.Scope"/>): <c>once</c>, <c>chat</c>, <c>always</c>, <c>time</c>; null when none applies.</summary>
public static class AuditScopes
{
    public const string Once = "once";
    public const string Chat = "chat";
    public const string Always = "always";
    public const string Time = "time";
}

/// <summary>
/// One tool call decision and outcome, as an app hands it to <see cref="IAuditLog.AppendAsync"/>: content-free (no prompt, answer,
/// argument or result text). The log adds <c>seq</c>, <c>prev</c>, <c>hash</c> and <c>seal</c>. TS: <c>AuditRecord</c>.
/// </summary>
public sealed record AuditRecord
{
    /// <summary>When it happened; null: now. Kept to the millisecond, UTC.</summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary><see cref="AuditKinds"/>; <c>tool</c> by default.</summary>
    public string Kind { get; init; } = AuditKinds.Tool;

    /// <summary>The app's name, as the per-app records use it (<c>AppName</c>).</summary>
    public string App { get; init; } = "";

    /// <summary>The agent's slug.</summary>
    public string Agent { get; init; } = "";

    /// <summary>The chat's (session's) id.</summary>
    public string Chat { get; init; } = "";

    /// <summary>The turn's id.</summary>
    public string Turn { get; init; } = "";

    /// <summary>The tool's name; <c>(unknown)</c> for a name the model made up.</summary>
    public string Tool { get; init; } = "";

    /// <summary>The tool's class by its wire name (<c>read</c>, <c>write</c>, <c>destructive</c>, <c>unknown</c>).</summary>
    public string Class { get; init; } = "";

    /// <summary>Where the tool comes from (<c>host</c>, <c>mcp</c>, <c>skills</c>, <c>web</c>, <c>gateway</c>, ...), as the approvals key it.</summary>
    public string? Source { get; init; }

    /// <summary>The MCP server's label, for an MCP tool.</summary>
    public string? Server { get; init; }

    /// <summary><see cref="AuditDecisions"/>.</summary>
    public string Decision { get; init; } = AuditDecisions.None;

    /// <summary><see cref="AuditScopes"/>, or null.</summary>
    public string? Scope { get; init; }

    /// <summary><see cref="AuditOutcomes"/>.</summary>
    public string Outcome { get; init; } = AuditOutcomes.NotRun;

    /// <summary>SHA-256, lowercase hex, of the call's arguments JSON text (see <see cref="AuditChain.ArgumentsSha256"/>); null when the SDK never had the text.</summary>
    public string? ArgumentsSha256 { get; init; }
}

/// <summary>An entry of the log: the record, its place in the chain, its hash and its seal. TS: <c>AuditEntry</c>.</summary>
public sealed record AuditEntry
{
    public long Seq { get; init; }
    public DateTimeOffset Time { get; init; }
    public string Kind { get; init; } = AuditKinds.Tool;
    public string App { get; init; } = "";
    public string Agent { get; init; } = "";
    public string Chat { get; init; } = "";
    public string Turn { get; init; } = "";
    public string Tool { get; init; } = "";
    public string Class { get; init; } = "";
    public string? Source { get; init; }
    public string? Server { get; init; }
    public string Decision { get; init; } = AuditDecisions.None;
    public string? Scope { get; init; }
    public string Outcome { get; init; } = AuditOutcomes.NotRun;
    public string? ArgumentsSha256 { get; init; }

    /// <summary>The previous entry's <see cref="Hash"/>; 64 zeros for seq 1.</summary>
    public string Prev { get; init; } = AuditChain.Zeros;

    /// <summary>SHA-256, lowercase hex, of the canonical text (<see cref="AuditChain.CanonicalText"/>).</summary>
    public string Hash { get; init; } = "";

    /// <summary>Base64 HMAC-SHA256 of <see cref="Hash"/> under the log's own key.</summary>
    public string Seal { get; init; } = "";
}

/// <summary>
/// What <see cref="IAuditLog.VerifyAsync"/> found. A problem names its kind and the entry's seq, never content. TS:
/// <c>AuditVerification</c>.
/// </summary>
/// <param name="Intact">Every line reads, <c>seq</c> has no gap, every <c>prev</c> links, every hash and seal holds, and the first entry links to the anchor (or to zeros at seq 1).</param>
/// <param name="Entries">How many entries the log holds now.</param>
/// <param name="FirstSeq">The first entry's seq; null when there is none.</param>
/// <param name="FirstTime">The first entry's time.</param>
/// <param name="LastTime">The last entry's time.</param>
/// <param name="BrokenAtSeq">Where the chain breaks: the seq of the entry that does not hold (for a line that cannot be read or a gap, the seq that was expected).</param>
/// <param name="Problem">One of <see cref="AuditProblems"/>.</param>
public sealed record AuditVerification(bool Intact, int Entries, long? FirstSeq, DateTimeOffset? FirstTime, DateTimeOffset? LastTime,
    long? BrokenAtSeq = null, string? Problem = null);

/// <summary>The kinds of problem <see cref="AuditVerification.Problem"/> names. TS: <c>AuditProblems</c>.</summary>
public static class AuditProblems
{
    /// <summary>A line cannot be decrypted or parsed (damaged, or another person's).</summary>
    public const string Unreadable = "unreadable_line";

    /// <summary>A seq is missing, repeated or out of order (a deleted or swapped line).</summary>
    public const string SeqGap = "seq_gap";

    /// <summary>An entry's <c>prev</c> is not the previous entry's hash.</summary>
    public const string PrevMismatch = "prev_mismatch";

    /// <summary>An entry's hash is not what its fields give (a changed field).</summary>
    public const string HashMismatch = "hash_mismatch";

    /// <summary>An entry's seal does not hold under this log's key (forged, or from another log).</summary>
    public const string SealMismatch = "seal_mismatch";

    /// <summary>The anchor left by a prune does not hold, or does not match the first entry.</summary>
    public const string AnchorMismatch = "anchor_mismatch";

    /// <summary>The log starts after seq 1 and no anchor says why.</summary>
    public const string AnchorMissing = "anchor_missing";

    /// <summary>The log has entries and the seal key is gone: no seal can be checked.</summary>
    public const string KeyMissing = "key_missing";

    /// <summary>The last entry is not the one the sealed head names: entries after the head's number are missing (the tail was cut), or the head names another hash, or the head was forged.</summary>
    public const string HeadMismatch = "head_mismatch";

    /// <summary>The log has entries and no head: the head was deleted (or the log is from before the head existed and has not been written to since).</summary>
    public const string HeadMissing = "head_missing";
}

/// <summary>The format of <see cref="IAuditLog.ExportAsync"/>. TS: <c>"csv" | "html"</c>.</summary>
public enum AuditExportFormat
{
    /// <summary>The verification result in the first two lines, then one row per entry.</summary>
    Csv,

    /// <summary>One self-contained page: a table, escaped text, inline CSS, no script and no outside resource.</summary>
    Html,
}

/// <summary>The sealed head: the last entry's number and hash, written with every append, so that cutting off the end of the log is found.</summary>
internal sealed record AuditHead(long Seq, string Hash, string Seal);

/// <summary>The sealed mark a prune leaves: the last removed entry's seq and hash, so the chain still verifies from there.</summary>
internal sealed record AuditAnchor(long Seq, string Hash, DateTimeOffset PrunedAt, string Seal);

/// <summary>
/// The format of the chain, in one place: the canonical text, the hash and the seal. The same bytes in both SDKs (shared test
/// vectors in both suites). ASVS 5.0 V16 (security logging: integrity). TS: <c>auditChain</c> functions in <c>audit.ts</c>.
/// </summary>
public static class AuditChain
{
    /// <summary>The first line of the canonical text.</summary>
    public const string Version = "fadenstack.audit.v1";

    /// <summary>The <c>prev</c> of seq 1: 64 zeros.</summary>
    public static readonly string Zeros = new('0', 64);

    internal const string AnchorVersion = "fadenstack.audit.anchor.v1";

    internal const string HeadVersion = "fadenstack.audit.head.v1";

    /// <summary>The profile's feature that lets the SDK send <c>arguments_sha256</c> in its events and answers (an older server refuses the batch).</summary>
    public const string ArgumentsFeature = "client_events.arguments_sha256";

    /// <summary>
    /// The canonical text of an entry: <see cref="Version"/>, then seq, time, kind, app, agent, chat, turn, tool, class, source,
    /// server, decision, scope, outcome, arguments_sha256 and prev, joined by a line feed; null is the empty string. Never a JSON
    /// serialization, so both SDKs agree byte for byte.
    /// </summary>
    public static string CanonicalText(AuditEntry e) => string.Join("\n", new[]
    {
        Version, e.Seq.ToString(CultureInfo.InvariantCulture), Iso(e.Time), e.Kind, e.App, e.Agent, e.Chat, e.Turn, e.Tool, e.Class,
        e.Source ?? "", e.Server ?? "", e.Decision, e.Scope ?? "", e.Outcome, e.ArgumentsSha256 ?? "", e.Prev,
    });

    /// <summary>The entry's hash: SHA-256 of <see cref="CanonicalText"/> (UTF-8), lowercase hex.</summary>
    public static string HashOf(AuditEntry e) => Hex(Sha256(Encoding.UTF8.GetBytes(CanonicalText(e))));

    /// <summary>The seal of a hash: base64 of HMAC-SHA256 under <paramref name="key"/>, over the hash text (ASCII).</summary>
    public static string SealOf(byte[] key, string hash)
    {
        using var mac = new HMACSHA256(key);
        return Convert.ToBase64String(mac.ComputeHash(Encoding.ASCII.GetBytes(hash)));
    }

    /// <summary>
    /// The arguments hash: SHA-256 of the arguments' JSON text, UTF-8, lowercase hex. The text is the model's
    /// <c>function.arguments</c> string exactly as the stream carried it; where only parsed arguments exist (a standalone provider
    /// through Microsoft.Extensions.AI), the JSON text the SDK itself writes back as <c>function.arguments</c> in its next request.
    /// </summary>
    public static string ArgumentsSha256(string argumentsJson) => Hex(Sha256(Encoding.UTF8.GetBytes(argumentsJson)));

    /// <summary>A time as the chain writes it: UTC, ISO 8601, to the millisecond (<c>2026-11-16T09:30:00.123Z</c>).</summary>
    public static string Iso(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static bool TryParseTime(string text, out DateTimeOffset time) =>
        DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);

    internal static string AnchorSeal(byte[] key, long seq, string hash)
    {
        using var mac = new HMACSHA256(key);
        return Convert.ToBase64String(mac.ComputeHash(Encoding.UTF8.GetBytes(AnchorVersion + "\n" + seq.ToString(CultureInfo.InvariantCulture) + "\n" + hash)));
    }

    /// <summary>The head's seal: the same shape as the anchor's, under the head version.</summary>
    internal static string HeadSeal(byte[] key, long seq, string hash)
    {
        using var mac = new HMACSHA256(key);
        return Convert.ToBase64String(mac.ComputeHash(Encoding.UTF8.GetBytes(HeadVersion + "\n" + seq.ToString(CultureInfo.InvariantCulture) + "\n" + hash)));
    }

    /// <summary>Makes the entry after <paramref name="prev"/> (seq and hash of the one before; null: the first).</summary>
    internal static AuditEntry Next(AuditRecord record, (long Seq, string Hash)? prev, byte[] key, DateTimeOffset now)
    {
        var time = record.Time ?? now;
        var entry = new AuditEntry
        {
            Seq = (prev?.Seq ?? 0) + 1,
            Time = new DateTimeOffset(time.UtcDateTime.Ticks - time.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero),
            Kind = record.Kind, App = record.App, Agent = record.Agent, Chat = record.Chat, Turn = record.Turn, Tool = record.Tool,
            Class = record.Class, Source = Empty(record.Source), Server = Empty(record.Server), Decision = record.Decision,
            Scope = Empty(record.Scope), Outcome = record.Outcome, ArgumentsSha256 = Empty(record.ArgumentsSha256),
            Prev = prev?.Hash ?? Zeros,
        };
        var hash = HashOf(entry);
        return entry with { Hash = hash, Seal = SealOf(key, hash) };
    }

    private static string? Empty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>The entry as its line keeps it (JSON, fixed order, nulls kept).</summary>
    internal static byte[] ToJson(AuditEntry e)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", e.Seq);
            w.WriteString("time", Iso(e.Time));
            w.WriteString("kind", e.Kind);
            w.WriteString("app", e.App);
            w.WriteString("agent", e.Agent);
            w.WriteString("chat", e.Chat);
            w.WriteString("turn", e.Turn);
            w.WriteString("tool", e.Tool);
            w.WriteString("class", e.Class);
            w.WriteString("source", e.Source);
            w.WriteString("server", e.Server);
            w.WriteString("decision", e.Decision);
            w.WriteString("scope", e.Scope);
            w.WriteString("outcome", e.Outcome);
            w.WriteString("arguments_sha256", e.ArgumentsSha256);
            w.WriteString("prev", e.Prev);
            w.WriteString("hash", e.Hash);
            w.WriteString("seal", e.Seal);
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Reads a line's JSON; null when it is not an entry of the format (a missing or mistyped field, a time of another shape).</summary>
    internal static AuditEntry? FromJson(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            string? Str(string name, bool required)
            {
                if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
                {
                    return required ? throw new FormatException() : null;
                }
                return v.ValueKind == JsonValueKind.String ? v.GetString() : throw new FormatException();
            }
            if (!root.TryGetProperty("seq", out var seq) || seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt64(out var number) || number < 1
                || !TryParseTime(Str("time", true)!, out var time))
            {
                return null;
            }
            return new AuditEntry
            {
                Seq = number, Time = time, Kind = Str("kind", true)!, App = Str("app", true)!, Agent = Str("agent", true)!, Chat = Str("chat", true)!,
                Turn = Str("turn", true)!, Tool = Str("tool", true)!, Class = Str("class", true)!, Source = Str("source", false),
                Server = Str("server", false), Decision = Str("decision", true)!, Scope = Str("scope", false), Outcome = Str("outcome", true)!,
                ArgumentsSha256 = Str("arguments_sha256", false), Prev = Str("prev", true)!, Hash = Str("hash", true)!, Seal = Str("seal", true)!,
            };
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks entries in order (null: a line that could not be read). <paramref name="key"/> null: no seal can be checked. The first
    /// entry links to <paramref name="anchor"/> when the log starts after seq 1.
    /// </summary>
    internal static AuditVerification Verify(IEnumerable<AuditEntry?> entries, byte[]? key, AuditAnchor? anchor, AuditHead? head = null)
    {
        var count = 0;
        AuditEntry? first = null, last = null;
        AuditVerification Broken(long seq, string problem) =>
            new(false, count, first?.Seq, first?.Time, last?.Time, seq, problem);
        if (anchor is not null && (key is null || !FixedEquals(anchor.Seal, AnchorSeal(key, anchor.Seq, anchor.Hash))))
        {
            return Broken(anchor.Seq, AuditProblems.AnchorMismatch);
        }
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                return Broken((last?.Seq ?? anchor?.Seq ?? 0) + 1, AuditProblems.Unreadable);
            }
            long expectedSeq;
            string expectedPrev;
            if (last is not null)
            {
                (expectedSeq, expectedPrev) = (last.Seq + 1, last.Hash);
            }
            else if (entry.Seq == 1)
            {
                (expectedSeq, expectedPrev) = (1, Zeros);
            }
            else if (anchor is null)
            {
                return Broken(entry.Seq, AuditProblems.AnchorMissing);
            }
            else
            {
                (expectedSeq, expectedPrev) = (anchor.Seq + 1, anchor.Hash);
            }
            if (entry.Seq != expectedSeq)
            {
                return Broken(expectedSeq, AuditProblems.SeqGap);
            }
            if (!FixedEquals(entry.Prev, expectedPrev))
            {
                return Broken(entry.Seq, AuditProblems.PrevMismatch);
            }
            if (!FixedEquals(entry.Hash, HashOf(entry)))
            {
                return Broken(entry.Seq, AuditProblems.HashMismatch);
            }
            if (key is null)
            {
                return Broken(entry.Seq, AuditProblems.KeyMissing);
            }
            if (!FixedEquals(entry.Seal, SealOf(key, entry.Hash)))
            {
                return Broken(entry.Seq, AuditProblems.SealMismatch);
            }
            if (anchor is not null && entry.Seq == anchor.Seq && !FixedEquals(entry.Hash, anchor.Hash))
            {
                return Broken(entry.Seq, AuditProblems.AnchorMismatch); // a prune that did not finish: the anchor must match the entry it names
            }
            first ??= entry;
            last = entry;
            count++;
        }
        // The sealed head: the log must end where it says. Entries gone from the end, a head that names another entry, a forged head or a deleted one are found.
        if (last is not null || anchor is not null || head is not null)
        {
            var endSeq = last?.Seq ?? anchor?.Seq ?? 0;
            var endHash = last?.Hash ?? anchor?.Hash ?? "";
            if (head is null)
            {
                return Broken(endSeq, AuditProblems.HeadMissing);
            }
            if (key is null || !FixedEquals(head.Seal, HeadSeal(key, head.Seq, head.Hash)))
            {
                return Broken(head.Seq, AuditProblems.HeadMismatch);
            }
            if (head.Seq != endSeq || !FixedEquals(head.Hash, endHash))
            {
                return Broken(head.Seq > endSeq ? endSeq + 1 : head.Seq, AuditProblems.HeadMismatch);
            }
        }
        return new AuditVerification(true, count, first?.Seq, first?.Time, last?.Time);
    }

    /// <summary>Compares two texts without stopping at the first difference.</summary>
    internal static bool FixedEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        if (x.Length != y.Length)
        {
            return false;
        }
        var diff = 0;
        for (var i = 0; i < x.Length; i++)
        {
            diff |= x[i] ^ y[i];
        }
        return diff == 0;
    }

    private static byte[] Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(bytes);
    }

    private static string Hex(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }
}
