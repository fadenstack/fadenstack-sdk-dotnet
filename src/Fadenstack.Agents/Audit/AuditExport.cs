using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Fadenstack.Agents.Audit;

/// <summary>The export of the log: a CSV or one self-contained HTML page, the verification result first. Content-free like the log itself.</summary>
internal static class AuditExport
{
    internal static readonly string[] Columns =
    {
        "seq", "time", "kind", "app", "agent", "chat", "turn", "tool", "class", "source", "server", "decision", "scope", "outcome",
        "arguments_sha256", "prev", "hash", "seal",
    };

    internal static string[] Cells(AuditEntry e) => new[]
    {
        e.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture), AuditChain.Iso(e.Time), e.Kind, e.App, e.Agent, e.Chat, e.Turn,
        e.Tool, e.Class, e.Source ?? "", e.Server ?? "", e.Decision, e.Scope ?? "", e.Outcome, e.ArgumentsSha256 ?? "", e.Prev, e.Hash, e.Seal,
    };

    internal static async Task WriteAsync(AuditExportFormat format, Stream destination, AuditVerification verification,
        IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = format == AuditExportFormat.Html ? Html(verification, entries) : Csv(verification, entries);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        await destination.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The verification as the first two lines of the CSV, a blank line, the column names, then the rows.</summary>
    internal static string Csv(AuditVerification v, IReadOnlyList<AuditEntry> entries)
    {
        var text = new StringBuilder();
        text.Append("verification,intact,entries,first_seq,first_time,last_time,broken_at_seq,problem\r\n");
        text.Append(Row(new[]
        {
            "verification", v.Intact ? "true" : "false", v.Entries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            v.FirstSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", v.FirstTime is { } f ? AuditChain.Iso(f) : "",
            v.LastTime is { } l ? AuditChain.Iso(l) : "", v.BrokenAtSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            v.Problem ?? "",
        }));
        text.Append("\r\n").Append(Row(Columns));
        foreach (var entry in entries)
        {
            text.Append(Row(Cells(entry)));
        }
        return text.ToString();
    }

    private static string Row(string[] cells) => string.Join(",", cells.Select(Cell)) + "\r\n";

    /// <summary>Where a spreadsheet could start a formula cell: the start of the field and right after a <c>;</c> or <c>,</c> inside it (Excel in a German, French or Spanish locale splits a double-clicked CSV at <c>;</c>), before <c>= + - @</c>, tab, CR or NUL.</summary>
    private static readonly Regex FormulaStart = new(@"(^|[;,])(?=[=+\-@\t\r\0])", RegexOptions.CultureInvariant);

    /// <summary>A CSV cell: quoted when it needs it (a <c>,</c>, <c>;</c>, quote, CR or LF), and never read as a formula by a spreadsheet: a <c>'</c> goes before <c>= + - @</c>, tab, CR or NUL at the start of the field and right after a <c>;</c> or <c>,</c> inside it. TypeScript: <c>csvCell</c> and the UI's <c>csvField</c> follow the same rule.</summary>
    internal static string Cell(string value)
    {
        value = FormulaStart.Replace(value, "$1'");
        return value.IndexOfAny(new[] { ';', ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    internal static string Html(AuditVerification v, IReadOnlyList<AuditEntry> entries)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var text = new StringBuilder();
        text.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>Record of tool calls</title>\n<style>\n")
            .Append("body{font-family:system-ui,sans-serif;margin:24px;color:#1b1b1b;background:#fff}\n")
            .Append("table{border-collapse:collapse;font-size:12px}\nth,td{border:1px solid #b0b0b0;padding:3px 6px;text-align:left;vertical-align:top}\n")
            .Append("th{background:#eee}\n.hash{font-family:ui-monospace,monospace;font-size:11px;word-break:break-all}\n")
            .Append(".ok{color:#0b6b2a;font-weight:600}.bad{color:#a00000;font-weight:600}\n")
            .Append("@media (prefers-color-scheme:dark){body{background:#161616;color:#e8e8e8}th{background:#2a2a2a}th,td{border-color:#555}")
            .Append(".ok{color:#6fd68c}.bad{color:#ff8a8a}}\n</style>\n</head>\n<body>\n<h1>Record of tool calls</h1>\n");
        text.Append("<p>").Append(v.Intact ? "<span class=\"ok\">Intact</span>" : "<span class=\"bad\">Damaged</span>")
            .Append(": ").Append(v.Entries).Append(" entries");
        if (v.FirstTime is { } first && v.LastTime is { } last)
        {
            text.Append(", from ").Append(E(AuditChain.Iso(first))).Append(" to ").Append(E(AuditChain.Iso(last)));
        }
        if (!v.Intact)
        {
            text.Append(". Problem: ").Append(E(v.Problem)).Append(" at entry ").Append(v.BrokenAtSeq);
        }
        text.Append(".</p>\n<table>\n<thead><tr>");
        foreach (var column in Columns)
        {
            text.Append("<th>").Append(E(column)).Append("</th>");
        }
        text.Append("</tr></thead>\n<tbody>\n");
        foreach (var entry in entries)
        {
            text.Append("<tr>");
            var cells = Cells(entry);
            for (var i = 0; i < cells.Length; i++)
            {
                text.Append(i >= 14 ? "<td class=\"hash\">" : "<td>").Append(E(cells[i])).Append("</td>");
            }
            text.Append("</tr>\n");
        }
        return text.Append("</tbody>\n</table>\n</body>\n</html>\n").ToString();
    }
}
