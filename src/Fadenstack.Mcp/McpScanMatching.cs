using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Fadenstack.Mcp;

/// <summary>
/// How a scan shows and compares addresses and programs (desktop-agent/32 §3): a short text without anything secret, and the
/// test for "the person already has this server".
/// </summary>
internal static class McpAddresses
{
    private const int LongSegment = 20;
    private const int MaxArguments = 6;
    private const int MaxArgument = 40;
    private static readonly Regex SecretFlag = new("token|key|secret|pass|auth|credential|bearer|cookie|session", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// An address as the scan sheet shows it: scheme, host, port and path, without a user, query or fragment (they carry tokens),
    /// and a long path segment (often a token) as an ellipsis.
    /// </summary>
    public static string Display(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "(address)";
        var path = string.Join("/", uri.AbsolutePath.Split('/').Select(s => s.Length > LongSegment ? "…" : s));
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped) + (path == "/" ? "" : path);
    }

    /// <summary>The program's name and a short, secret-free summary of its arguments, or the address.</summary>
    public static string Where(McpServerEntry entry)
    {
        if (!entry.IsProgram)
            return Display(entry.Url ?? "");
        var secrets = entry.Environment.Concat(entry.Headers).Select(p => p.Value).Where(v => v.Length >= 4).ToList();
        var text = new StringBuilder(Path.GetFileName(entry.Command!.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : entry.Command!);
        var hide = false;
        var shown = 0;
        foreach (var argument in entry.Arguments)
        {
            if (shown == MaxArguments)
            {
                text.Append(" …");
                break;
            }
            string part;
            if (hide || Hidden(argument, secrets))
                part = "…";
            else if (argument.StartsWith("-", StringComparison.Ordinal) && argument.IndexOf('=') is var eq and > 0)
                part = SecretFlag.IsMatch(argument[..eq]) ? argument[..(eq + 1)] + "…" : Cut(argument);
            else
                part = Cut(argument);
            hide = argument.StartsWith("-", StringComparison.Ordinal) && argument.IndexOf('=') < 0 && SecretFlag.IsMatch(argument);
            text.Append(' ').Append(Strip(part));
            shown++;
        }
        return text.ToString();
    }

    private static bool Hidden(string argument, List<string> secrets) =>
        secrets.Any(s => argument.Contains(s, StringComparison.Ordinal))
        || Regex.IsMatch(argument, @"://[^/@\s]*@", RegexOptions.CultureInvariant)
        || (argument.Contains("://", StringComparison.Ordinal) && argument.Contains('?'));

    private static string Cut(string argument) => argument.Length > MaxArgument ? argument[..MaxArgument] + "…" : argument;

    private static string Strip(string text) => new(text.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());

    /// <summary>The same address: scheme, host (every loopback name is one host), port, path without a trailing slash, query.</summary>
    public static bool SameAddress(string? a, string? b) => Key(a) is { } ka && ka == Key(b);

    private static string? Key(string? url)
    {
        if (url is null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        var host = uri.Host.Trim('[', ']');
        var loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme}|{(loopback ? "loopback" : host.ToLowerInvariant())}|{uri.Port}|{path}|{uri.Query}";
    }

    /// <summary>The same program with the same arguments; the program compared by its path, case-insensitive and without <c>.exe</c> on Windows.</summary>
    public static bool SameProgram(McpServerEntry a, McpServerEntry b, bool windows)
    {
        if (!a.IsProgram || !b.IsProgram || a.Arguments.Count != b.Arguments.Count)
            return false;
        return Program(a.Command!, windows) == Program(b.Command!, windows) && a.Arguments.SequenceEqual(b.Arguments, StringComparer.Ordinal);
    }

    private static string Program(string command, bool windows)
    {
        var path = command.Trim().Replace('\\', '/').TrimEnd('/');
        if (!windows)
            return path;
        path = path.ToLowerInvariant();
        foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            if (path.EndsWith(extension, StringComparison.Ordinal))
                return path[..^extension.Length];
        return path;
    }
}
