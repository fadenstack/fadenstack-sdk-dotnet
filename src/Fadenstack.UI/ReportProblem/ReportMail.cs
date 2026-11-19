using System.Globalization;
using System.Text;
using Fadenstack.Agents;

namespace Fadenstack.UI;

/// <summary>The words of the mail's labels and subjects, in the panel's language (<see cref="UiStrings.ReportLabels"/>). The same table in the TypeScript SDK.</summary>
public sealed record ReportLabels
{
    /// <summary>"Problem with {0}": {0} is the app's name and version.</summary>
    public string Subject { get; init; } = "Problem with {0}";

    /// <summary>"Problem with Fadenstack SDK {0}": {0} is the SDK's version (the host described no app).</summary>
    public string SubjectNoApp { get; init; } = "Problem with Fadenstack SDK {0}";

    public string WhatHappened { get; init; } = "What happened";
    public string App { get; init; } = "App";
    public string Sdk { get; init; } = "SDK";
    public string System { get; init; } = "System";
    public string Mode { get; init; } = "Mode";
    public string Agent { get; init; } = "Agent";
    public string Server { get; init; } = "Server";
    public string Endpoint { get; init; } = "Endpoint";
    public string Time { get; init; } = "Time";
    public string Error { get; init; } = "Error";
    public string To { get; init; } = "To";

    /// <summary>"Subject": the label of the copy's subject line.</summary>
    public string SubjectLabel { get; init; } = "Subject";

    /// <summary>"Recent log": the heading of the log entries a copy may carry (the TypeScript SDK's browser log).</summary>
    public string RecentLog { get; init; } = "Recent log";
}

/// <summary>A failed turn's error: its code, and the server's request id where the error carries one.</summary>
public sealed record ReportedError(string? Code, string? RequestId = null);

/// <summary>The agent as the mail names it: its name, its version or draft, its model.</summary>
public sealed record ReportAgent(string? Name, int? Version, bool Draft, string? Model);

/// <summary>
/// What the report says, each value as the mail shows it; an unknown one is null and its line is left out. Nothing else can reach the mail: no
/// prompt, answer, reasoning, attachment, file or page name, tool name or argument, key, token, person, user id or endpoint address.
/// </summary>
public sealed record ReportFacts
{
    /// <summary>The app's name and version as the host described it ("Word add-in 1.2.0"); null: the host described none.</summary>
    public string? App { get; init; }

    /// <summary>The SDK's version, for the subject ("0.3.1").</summary>
    public string SdkVersion { get; init; } = "";

    /// <summary>The SDK line: "Fadenstack SDK 0.3.1 (.NET)".</summary>
    public string Sdk { get; init; } = "";

    public string? System { get; init; }

    /// <summary>The mode, in the panel's word for it.</summary>
    public string? Mode { get; init; }

    public string? Agent { get; init; }

    public string? Server { get; init; }

    /// <summary>"ollama, on this computer": never the endpoint's address.</summary>
    public string? Endpoint { get; init; }

    /// <summary>"2026-11-19 10:42 UTC".</summary>
    public string Time { get; init; } = "";

    public ReportedError? Error { get; init; }
}

/// <summary>A report as it is mailed, shown and copied.</summary>
public sealed class ReportDraft
{
    internal ReportDraft(string? address, string subject, string body, string mailBody, int dropped, string? mailto, string copyText)
    {
        Address = address;
        Subject = subject;
        Body = body;
        MailBody = mailBody;
        Dropped = dropped;
        Mailto = mailto;
        CopyText = copyText;
    }

    /// <summary>The address, or null where there is none (governed, no <c>supportEmail</c>).</summary>
    public string? Address { get; }

    public string Subject { get; }

    /// <summary>The body in full, with CRLF line breaks.</summary>
    public string Body { get; }

    /// <summary>The body as the <c>mailto:</c> link carries it (CRLF): <see cref="Body"/> without the lines the 2,000 limit dropped.</summary>
    public string MailBody { get; }

    /// <summary>How many lines the limit dropped from the end.</summary>
    public int Dropped { get; }

    /// <summary>What the sheet shows: <see cref="MailBody"/> with LF line breaks (without an address nothing is dropped).</summary>
    public string Preview => MailBody.Replace("\r\n", "\n");

    /// <summary><c>mailto:&lt;address&gt;?subject=&amp;body=</c>, at most <see cref="ReportMailBuilder.MaxLinkLength"/> characters unless nothing is left to drop; null without an address.</summary>
    public string? Mailto { get; }

    /// <summary>The text "Copy details" copies (LF line breaks): every line.</summary>
    public string CopyText { get; }
}

/// <summary>
/// Builds the "Report a problem" mail: a pure function of the address, the labels and the facts, so that the .NET and TypeScript SDKs give the
/// same bytes (tests/Fadenstack.UI.Tests/ReportProblem/cases.json is the table both run).
/// </summary>
public static class ReportMailBuilder
{
    /// <summary>The longest <c>mailto:</c> link, in characters.</summary>
    public const int MaxLinkLength = 2000;

    private const string Crlf = "\r\n";

    /// <param name="address">One plain address (<see cref="SupportAddress.IsValid"/>), or null for no mail.</param>
    /// <param name="labels">The panel's words.</param>
    /// <param name="facts">What the report says.</param>
    /// <param name="log">Log entries, one per line, for "Copy details" only (never the mail); null for none.</param>
    /// <exception cref="ArgumentException"><paramref name="address"/> is not one plain mail address: it is never written into a link.</exception>
    public static ReportDraft Build(string? address, ReportLabels labels, ReportFacts facts, IReadOnlyList<string>? log = null)
    {
        if (labels is null)
        {
            throw new ArgumentNullException(nameof(labels));
        }
        if (facts is null)
        {
            throw new ArgumentNullException(nameof(facts));
        }
        if (address is not null && !SupportAddress.IsValid(address))
        {
            throw new ArgumentException("The support address must be one plain mail address.", nameof(address));
        }
        var app = OneLine(facts.App);
        var version = OneLine(facts.SdkVersion);
        var subject = app.Length > 0
            ? string.Format(CultureInfo.InvariantCulture, labels.Subject, app) + " (Fadenstack SDK " + version + ")"
            : string.Format(CultureInfo.InvariantCulture, labels.SubjectNoApp, version);

        var items = new List<string>();
        void Add(string label, string? value)
        {
            if (OneLine(value) is { Length: > 0 } text)
            {
                items.Add(label + ": " + text);
            }
        }
        Add(labels.App, app);
        Add(labels.Sdk, facts.Sdk);
        Add(labels.System, facts.System);
        Add(labels.Mode, facts.Mode);
        Add(labels.Agent, facts.Agent);
        Add(labels.Server, facts.Server);
        Add(labels.Endpoint, facts.Endpoint);
        Add(labels.Time, facts.Time);
        if (OneLine(facts.Error?.Code) is { Length: > 0 } code)
        {
            items.Add(labels.Error + ": " + code + (OneLine(facts.Error?.RequestId) is { Length: > 0 } id ? " (" + id + ")" : ""));
        }

        var full = BodyOf(labels, items, items.Count);
        var mailBody = full;
        string? mailto = null;
        var kept = items.Count;
        if (address is not null)
        {
            string Link(string body) => "mailto:" + address + "?subject=" + Encode(subject) + "&body=" + Encode(body);
            mailto = Link(full);
            // The last lines go first (the error, then the time, ...); never the subject or the first line. With nothing left to drop the link stays as it is.
            while (mailto.Length > MaxLinkLength && kept > 0)
            {
                kept--;
                mailBody = BodyOf(labels, items, kept);
                mailto = Link(mailBody);
            }
        }
        var entries = (log ?? Array.Empty<string>()).Select(OneLine).Where(e => e.Length > 0).ToList();
        var copy = (address is null ? "" : labels.To + ": " + address + "\n") + labels.SubjectLabel + ": " + subject + "\n\n" + Lf(full)
            + (entries.Count > 0 ? "\n-- \n" + labels.RecentLog + ":\n" + string.Join("\n", entries) : "");
        return new ReportDraft(address, subject, full, mailBody, items.Count - kept, mailto, copy);
    }

    private static string BodyOf(ReportLabels labels, IReadOnlyList<string> items, int count)
    {
        var body = labels.WhatHappened + ":" + Crlf + Crlf;
        return count == 0 ? body : body + Crlf + "-- " + Crlf + string.Join(Crlf, items.Take(count));
    }

    /// <summary>
    /// A value as one line: control characters and line breaks (and U+2028, U+2029) become one space for a run of them, a lone surrogate
    /// becomes U+FFFD, and the ends are trimmed.
    /// </summary>
    internal static string OneLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        var text = value!;
        var builder = new StringBuilder(text.Length);
        var inRun = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c <= '\u001f' || (c >= '\u007f' && c <= '\u009f') || c == '\u2028' || c == '\u2029')
            {
                if (!inRun)
                {
                    builder.Append(' ');
                }
                inRun = true;
                continue;
            }
            inRun = false;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[++i]);
            }
            else
            {
                builder.Append(char.IsSurrogate(c) ? '\ufffd' : c);
            }
        }
        return builder.ToString().Trim().Trim('\ufeff', ' ');
    }

    private static string Lf(string text) => text.Replace(Crlf, "\n");

    /// <summary>UTF-8 percent-encoding of everything outside RFC 3986's unreserved characters (<c>A-Z a-z 0-9 - . _ ~</c>), as RFC 6068 has it.</summary>
    internal static string Encode(string text)
    {
        var builder = new StringBuilder(text.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (b < 0x80 && (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~'))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append("0123456789ABCDEF"[b >> 4]).Append("0123456789ABCDEF"[b & 15]);
            }
        }
        return builder.ToString();
    }
}

/// <summary>Where a model endpoint's address points, as the mail says it (never the address itself).</summary>
public enum ReportPlace
{
    ThisComputer,
    LocalNetwork,
    Internet,
}

/// <summary>The values of <see cref="ReportFacts"/> that are made of several parts, in the panel's language.</summary>
public static class ReportFactsText
{
    /// <summary>"Docs, Version 3, Model: qwen3-32b" (or "Docs, Draft, ..."); null when there is nothing to say.</summary>
    public static string? Agent(ReportAgent? agent, UiStrings strings)
    {
        if (agent is null)
        {
            return null;
        }
        var parts = new List<string>();
        if (ReportMailBuilder.OneLine(agent.Name) is { Length: > 0 } name)
        {
            parts.Add(name);
        }
        if (agent.Draft)
        {
            parts.Add(strings.AboutDraft);
        }
        else if (agent.Version is { } version)
        {
            parts.Add(strings.AboutVersion(version));
        }
        if (ReportMailBuilder.OneLine(agent.Model) is { Length: > 0 } model)
        {
            parts.Add(strings.AboutModel(model));
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>"ollama, on this computer": only when both the dialect's name and where it points are known; never the address.</summary>
    public static string? Endpoint(string? dialect, ReportPlace? place, UiStrings strings)
    {
        if (string.IsNullOrWhiteSpace(dialect) || place is null)
        {
            return null;
        }
        var where = place switch
        {
            ReportPlace.ThisComputer => strings.ReportPlaceThisComputer,
            ReportPlace.LocalNetwork => strings.ReportPlaceLocalNetwork,
            _ => strings.ReportPlaceInternet,
        };
        return dialect + ", " + where;
    }

    /// <summary>"2026-11-19 10:42 UTC".</summary>
    public static string Time(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
