using System.Text.RegularExpressions;

namespace Fadenstack.Mcp;

/// <summary>What is wrong with a server entry; a view model turns it into a text in the person's language.</summary>
public enum McpProblem
{
    None,
    LabelMissing,
    LabelInvalid,
    LabelTaken,
    CommandMissing,
    CommandInvalid,
    FolderNotAbsolute,
    NameInvalid,
    ValueInvalid,
    AddressMissing,
    AddressInvalid,

    /// <summary>Not on this computer: "Network servers come in a later version." (desktop-agent/31 decision 1).</summary>
    AddressNotLoopback,
    TimeoutOutOfRange,
}

/// <summary>The first problem of an entry, and the field it is in.</summary>
public sealed record McpValidation(McpProblem Problem, string? Field = null)
{
    public static McpValidation Ok { get; } = new(McpProblem.None);

    public bool IsValid => Problem == McpProblem.None;
}

/// <summary>
/// The rules for a server entry (desktop-agent/31 §4.4): the label, a program with a list of arguments, or an address on this
/// computer (loopback only in round 1), names of environment variables and headers that cannot smuggle a line break, and a call
/// timeout the SDK accepts. Applied when a server is typed in, edited or imported.
/// </summary>
public static class McpServerValidator
{
    /// <summary>The longest label.</summary>
    public const int MaxLabel = 32;

    public const int MinTimeoutSeconds = 5;

    public const int MaxTimeoutSeconds = 600;

    private static readonly Regex LabelPattern = new("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant);
    private static readonly Regex EnvironmentName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex HeaderName = new("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="label"/> is a valid label (not whether it is free).</summary>
    public static bool IsLabel(string? label) => label is not null && LabelPattern.IsMatch(label);

    /// <param name="entry">The entry to check.</param>
    /// <param name="taken">The labels the person has already (the entry's own label, when editing, is not in it).</param>
    public static McpValidation Validate(McpServerEntry entry, IEnumerable<string> taken)
    {
        if (string.IsNullOrEmpty(entry.Label))
            return new(McpProblem.LabelMissing, "label");
        if (!IsLabel(entry.Label))
            return new(McpProblem.LabelInvalid, "label");
        if (taken.Contains(entry.Label, StringComparer.Ordinal))
            return new(McpProblem.LabelTaken, "label");
        if (entry.CallTimeoutSeconds < MinTimeoutSeconds || entry.CallTimeoutSeconds > MaxTimeoutSeconds)
            return new(McpProblem.TimeoutOutOfRange, "timeout");
        return entry.IsProgram ? ValidateProgram(entry) : ValidateAddress(entry);
    }

    private static McpValidation ValidateProgram(McpServerEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Command))
            return new(McpProblem.CommandMissing, "command");
        if (HasControl(entry.Command!))
            return new(McpProblem.CommandInvalid, "command");
        if (entry.Arguments.Any(a => a is null || a.Contains('\0')))
            return new(McpProblem.ValueInvalid, "arguments");
        if (!string.IsNullOrWhiteSpace(entry.WorkingFolder) && (!Path.IsPathRooted(entry.WorkingFolder) || HasControl(entry.WorkingFolder!)))
            return new(McpProblem.FolderNotAbsolute, "folder");
        foreach (var pair in entry.Environment)
        {
            if (!EnvironmentName.IsMatch(pair.Name))
                return new(McpProblem.NameInvalid, "environment");
            if (pair.Value.Contains('\0'))
                return new(McpProblem.ValueInvalid, "environment");
        }
        return McpValidation.Ok;
    }

    private static McpValidation ValidateAddress(McpServerEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Url))
            return new(McpProblem.AddressMissing, "address");
        if (!Uri.TryCreate(entry.Url!.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0)
            return new(McpProblem.AddressInvalid, "address");
        if (!LocalMcpServer.IsLoopback(url))
            return new(McpProblem.AddressNotLoopback, "address");
        foreach (var pair in entry.Headers)
        {
            if (!HeaderName.IsMatch(pair.Name))
                return new(McpProblem.NameInvalid, "headers");
            if (HasControl(pair.Value))
                return new(McpProblem.ValueInvalid, "headers");
        }
        return McpValidation.Ok;
    }

    // A line break in a header value or a command would split a request or hide an argument.
    private static bool HasControl(string text) => text.Any(c => char.IsControl(c));

    /// <summary>
    /// A label made from a name found in another program's file: lower case, anything else a hyphen, cut to 32 characters;
    /// "server" when nothing is left.
    /// </summary>
    public static string LabelFrom(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray();
        var label = Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        if (label.Length > MaxLabel)
            label = label[..MaxLabel].TrimEnd('-');
        return label.Length == 0 ? "server" : label;
    }

    /// <summary>The first free label starting from <paramref name="wanted"/>: itself, then <c>-2</c>, <c>-3</c>... within 32 characters.</summary>
    public static string FreeLabel(string wanted, ICollection<string> taken)
    {
        if (!taken.Contains(wanted))
            return wanted;
        for (var n = 2; ; n++)
        {
            var suffix = "-" + n;
            var label = (wanted.Length + suffix.Length > MaxLabel ? wanted[..(MaxLabel - suffix.Length)].TrimEnd('-') : wanted) + suffix;
            if (!taken.Contains(label))
                return label;
        }
    }
}
