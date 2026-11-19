using System.Text.RegularExpressions;

namespace Fadenstack.Agents;

/// <summary>
/// The address a report goes to: one plain address, nothing a mail app could read as another recipient, a bcc or a body
/// (OWASP ASVS 5.0 V1). The rule is the same in the TypeScript SDK.
/// </summary>
public static class SupportAddress
{
    /// <summary>What a standalone agent writes to when neither the machine policy nor the host named an address.</summary>
    public const string Default = "support@fadenstack.com";

    /// <summary>The longest address (RFC 5321's path limit).</summary>
    public const int MaxLength = 254;

    private static readonly Regex Plain = new(@"^[A-Za-z0-9._+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="address"/> is one plain address: <c>^[A-Za-z0-9._+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$</c>, at most
    /// <see cref="MaxLength"/> characters. A <c>?</c>, <c>&amp;</c>, <c>%</c>, <c>,</c>, a space or a line break anywhere refuses it; so
    /// does a second address. Nothing is trimmed.
    /// </summary>
    public static bool IsValid(string? address) =>
        address is { Length: > 0 and <= MaxLength } && Plain.IsMatch(address);
}
