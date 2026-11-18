using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Fadenstack.Endpoints;

namespace Fadenstack.Mcp;

/// <summary>Why plain http to an MCP server was refused (sdk/51 F05). A person-facing text belongs to the app (five languages).</summary>
public enum McpPlainHttpRefusal
{
    /// <summary>The address is outside this computer, and the person has not switched on "Allow plain http on the local network".</summary>
    NoOptIn,

    /// <summary>The switch is on, but the address, or one the name resolves to, is not on the local network (or does not resolve).</summary>
    NotLocal,

    /// <summary>The server is sent a header value (a token lives there): never over plain http outside this computer.</summary>
    Secret,

    /// <summary>
    /// The agent's profile (<c>local_mcp.plain_http</c> not <c>allow</c>, or absent) or this computer's policy (<c>plainHttp:
    /// forbidden</c>) does not allow plain http on the local network: "Plain http is not allowed here".
    /// </summary>
    NotAllowed,
}

/// <summary>A connection to an MCP server over plain http was refused before anything was sent; <see cref="Reason"/> says why.</summary>
public sealed class McpPlainHttpRefusedException : InvalidOperationException
{
    public McpPlainHttpRefusedException(McpPlainHttpRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public McpPlainHttpRefusal Reason { get; }
}

/// <summary>
/// What a host knows when it connects a server: whether plain http on the local network is allowed here (the profile and the
/// machine policy). The connection says afterwards whether it used the local network, so the host can stop it when that changes.
/// </summary>
internal sealed class McpConnection
{
    public McpConnection(bool plainHttpPermitted, Func<string, Task<IPAddress[]>>? resolve = null)
    {
        PlainHttpPermitted = plainHttpPermitted;
        Resolve = resolve;
    }

    /// <summary>Plain http on the local network is allowed here (the person's switch is still needed per server).</summary>
    public bool PlainHttpPermitted { get; }

    /// <summary>The name resolver (DNS when null; tests give their own).</summary>
    public Func<string, Task<IPAddress[]>>? Resolve { get; }

    /// <summary>The module resolved the program of a stdio server for this start (<see cref="Program"/>, null when it was not found); the connection starts exactly that file.</summary>
    public bool ProgramResolved { get; set; }

    /// <summary>The absolute file the command of a stdio server resolved to for this start.</summary>
    public string? Program { get; set; }

    /// <summary>Set by the connection: it goes over plain http to an address on the local network (not this computer).</summary>
    public bool UsedLocalNetwork { get; set; }

    public static McpConnection Default => new(false);
}

/// <summary>
/// The HTTP connection to an MCP server (sdk/51 F05; OWASP ASVS 5.0 V12.1, V12.3): https goes anywhere; plain http goes to this
/// computer, and to the local network only with the person's switch, where this computer's policy and the agent allow it, and
/// never with a header value. A name is resolved once: every address it stands for must be on this computer (or, with the
/// switch, on the local network: RFC 1918, link-local, fc00::/7), and every request goes to one of those checked addresses,
/// so a name that resolves elsewhere later (DNS rebinding) reaches nothing new. No redirect is followed, and a request to
/// another origin than the server's (a server that names an endpoint elsewhere) is refused, so headers never travel on.
/// Plain http goes direct, never through a proxy.
/// </summary>
internal static class McpHttp
{
    /// <summary>A server's origin, <c>http://host[:port]</c> (the host in its ASCII form, the port only when not the default).</summary>
    public static string Origin(Uri url)
    {
        var host = url.HostNameType == UriHostNameType.IPv6 ? "[" + url.IdnHost.Trim('[', ']') + "]" : url.IdnHost;
        return url.Scheme + "://" + host + (url.IsDefaultPort ? "" : ":" + url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A server's address as a governed server is told it (<c>local_mcp.attached</c>): the origin and the path, without a user,
    /// password, query or fragment, where tokens are often put. The TypeScript SDK sends the same.
    /// </summary>
    public static string ReportedAddress(Uri url) => Origin(url) + url.GetComponents(UriComponents.Path | UriComponents.KeepDelimiter, UriFormat.UriEscaped);

    public static async Task<HttpClient> CreateClientAsync(Uri url, bool hasSecrets, bool optedIn, McpConnection connection,
        CancellationToken cancellationToken)
    {
        if (url.Scheme == Uri.UriSchemeHttps)
        {
            return new HttpClient(new SameOrigin(url, null, new HttpClientHandler { AllowAutoRedirect = false }), disposeHandler: true);
        }
        if (url.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("An MCP server's address is http or https.", nameof(url));
        }
        var host = url.IdnHost.Trim('[', ']');
        var addresses = await NetworkAddress.ResolveAsync(host, connection.Resolve, cancellationToken).ConfigureAwait(false);
        var local = Check(addresses, hasSecrets, optedIn, connection.PlainHttpPermitted);
        connection.UsedLocalNetwork = local;
        var pinned = IPAddress.TryParse(host, out _) ? null : addresses;
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        return new HttpClient(new SameOrigin(url, pinned, handler), disposeHandler: true);
    }

    /// <summary>
    /// Whether plain http may go to these addresses: true when it goes to the local network, false when only to this computer;
    /// throws <see cref="McpPlainHttpRefusedException"/> otherwise.
    /// </summary>
    internal static bool Check(IReadOnlyList<IPAddress> addresses, bool hasSecrets, bool optedIn, bool permitted)
    {
        if (addresses.Count > 0 && addresses.All(a => NetworkAddress.Of(a) == AddressScope.Loopback))
        {
            return false;
        }
        if (hasSecrets)
        {
            throw new McpPlainHttpRefusedException(McpPlainHttpRefusal.Secret,
                "A server with a header value is reached over plain http only on this computer; use https.");
        }
        if (!optedIn)
        {
            throw new McpPlainHttpRefusedException(McpPlainHttpRefusal.NoOptIn,
                "Plain http goes only to this computer unless \"Allow plain http on the local network\" is on for this server.");
        }
        if (!permitted)
        {
            throw new McpPlainHttpRefusedException(McpPlainHttpRefusal.NotAllowed, "Plain http is not allowed here.");
        }
        if (addresses.Count == 0 || addresses.Any(a => NetworkAddress.Of(a) == AddressScope.Public))
        {
            throw new McpPlainHttpRefusedException(McpPlainHttpRefusal.NotLocal,
                addresses.Count == 0 ? "The server's name does not resolve." : "Plain http goes only to the local network; this address is outside it.");
        }
        return true;
    }

    /// <summary>
    /// Sends only to the server's own origin; for a name over plain http, to one of the checked addresses (the request's host
    /// becomes the address, its Host header stays the name). A connection that fails before anything was sent tries the next
    /// checked address (<c>localhost</c> is often ::1 first while the server listens on 127.0.0.1); the one that works is kept.
    /// </summary>
    private sealed class SameOrigin : DelegatingHandler
    {
        private readonly Uri _server;
        private readonly IPAddress[]? _addresses;
        private int _working;

        public SameOrigin(Uri server, IPAddress[]? addresses, HttpMessageHandler inner)
            : base(inner)
        {
            _server = server;
            _addresses = addresses;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("A request without an address.");
            if (!string.Equals(uri.Scheme, _server.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.IdnHost, _server.IdnHost, StringComparison.OrdinalIgnoreCase) || uri.Port != _server.Port)
            {
                throw new McpPlainHttpRefusedException(McpPlainHttpRefusal.NotLocal,
                    "The server named an address of another origin; nothing is sent there.");
            }
            if (_addresses is null)
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            var authority = uri.IsDefaultPort ? uri.IdnHost : uri.IdnHost + ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var first = Volatile.Read(ref _working);
            for (var i = 0; ; i++)
            {
                var at = (first + i) % _addresses.Length;
                request.RequestUri = new UriBuilder(uri) { Host = _addresses[at].ToString() }.Uri;
                request.Headers.Host = authority;
                try
                {
                    var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _working, at);
                    return response;
                }
                catch (HttpRequestException e) when (i + 1 < _addresses.Length && NotConnected(e))
                {
                    // nothing was sent: the next checked address
                }
                finally
                {
                    request.RequestUri = uri;
                }
            }
        }

        /// <summary>The connection was never made (on .NET Framework the socket's error sits under a WebException).</summary>
        private static bool NotConnected(HttpRequestException e)
        {
            for (Exception? inner = e.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (inner is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable or SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported })
                {
                    return true;
                }
                if (inner is WebException { Status: WebExceptionStatus.ConnectFailure })
                {
                    return true;
                }
            }
            return false;
        }
    }
}
