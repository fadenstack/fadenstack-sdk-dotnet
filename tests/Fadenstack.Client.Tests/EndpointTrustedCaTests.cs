using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fadenstack.Endpoints;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>
/// sdk/51 F15 (owner, 14 Nov): an organisation's own model server behind its own CA connects when the app passes that CA to the
/// endpoint, and says <see cref="EndpointProblem.CertificateNotTrusted"/> without it. A real HTTPS server on loopback with a
/// certificate a test CA issued; on net48 the same check goes through <c>FadenstackTransport.CreateHandler</c>.
/// </summary>
public class EndpointTrustedCaTests
{
    [Fact]
    public async Task With_the_apps_CA_the_endpoint_is_reachable_and_the_report_has_no_certificate_problem()
    {
        using var server = new TlsServer();

        var report = await Check(server, server.CaDer);

        Assert.True(report.Reachable);
        Assert.Equal(new[] { "m1" }, report.Models);
        Assert.DoesNotContain(EndpointProblem.CertificateNotTrusted, report.Problems);
        Assert.Contains("/v1/models", server.Paths);
    }

    [Fact]
    public async Task Without_the_CA_the_report_says_the_certificate_is_not_trusted()
    {
        using var server = new TlsServer();

        var report = await Check(server, null);

        Assert.False(report.Reachable);
        Assert.Contains(EndpointProblem.CertificateNotTrusted, report.Problems);
    }

    [Fact]
    public async Task Another_CA_does_not_make_the_certificate_trusted()
    {
        using var server = new TlsServer();
        using var other = new TlsServer();

        var report = await Check(server, other.CaDer);

        Assert.Contains(EndpointProblem.CertificateNotTrusted, report.Problems);
    }

    private static Task<EndpointReport> Check(TlsServer server, byte[]? ca) =>
        new EndpointCheck(new ModelEndpoint
        {
            BaseUrl = new Uri($"https://127.0.0.1:{server.Port}/v1"), Model = "m1", TrustedCaCertificate = ca,
            Timeout = TimeSpan.FromSeconds(20),
        }).RunAsync(null, CancellationToken.None);

    /// <summary>A small HTTPS server: <c>/v1/models</c> lists one model, anything else answers 404; one request per connection.</summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly List<string> _paths = new();

        public TlsServer()
        {
            using var caKey = RSA.Create(2048);
            var caRequest = new CertificateRequest("CN=Faden test CA " + Guid.NewGuid().ToString("N"), caKey, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            var now = DateTimeOffset.UtcNow;
            using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));
            CaDer = ca.Export(X509ContentType.Cert);

            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // server authentication
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            var serial = new byte[8];
            RandomNumberGenerator.Create().GetBytes(serial);
            serial[0] &= 0x7F;
            using var issued = request.Create(ca, now.AddDays(-1), now.AddDays(1), serial);
            using var withKey = issued.CopyWithPrivateKey(key);
            // SChannel (Windows) needs a key it can use: export as PFX and load it back.
#if NET9_0_OR_GREATER
            _certificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, "t"), "t", X509KeyStorageFlags.Exportable);
#else
            _certificate = new X509Certificate2(withKey.Export(X509ContentType.Pfx, "t"), "t", X509KeyStorageFlags.Exportable);
#endif

            _listener.Start();
            _ = AcceptAsync();
        }

        public byte[] CaDer { get; }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_paths)
                {
                    return _paths.ToList();
                }
            }
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception e) when (e is ObjectDisposedException or SocketException or InvalidOperationException)
                {
                    return;
                }
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var tls = new SslStream(client.GetStream(), false);
                    await tls.AuthenticateAsServerAsync(_certificate, false, SslProtocols.Tls12, false);
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await tls.ReadAsync(one, 0, 1) == 0)
                        {
                            return;
                        }
                        head.Append((char)one[0]);
                    }
                    var path = head.ToString().Split(' ')[1].Split('?')[0];
                    lock (_paths)
                    {
                        _paths.Add(path);
                    }
                    var (status, body) = path == "/v1/models"
                        ? ("200 OK", "{\"object\":\"list\",\"data\":[{\"id\":\"m1\",\"object\":\"model\"}]}")
                        : ("404 Not Found", "{}");
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await tls.WriteAsync(response, 0, response.Length);
                    await tls.WriteAsync(bytes, 0, bytes.Length);
                    await tls.FlushAsync();
                }
                catch (Exception e) when (e is IOException or AuthenticationException or SocketException or ObjectDisposedException)
                {
                    // A client that does not trust the certificate ends the handshake: that is the test.
                }
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _certificate.Dispose();
        }
    }
}
