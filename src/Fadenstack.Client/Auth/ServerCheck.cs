using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Fadenstack.Http;

namespace Fadenstack.Auth;

/// <summary>
/// Whether an address answers like a Fadenstack server, before a pairing is started on it (sdk/51 I15 §2, the wizard's
/// "Checking"): <c>GET {server}/api/health</c>, 200 and JSON within 15 s. It shows only that something answers like the
/// platform: the anonymous health answer says nothing more on purpose, and "server too old" is found at the pairing's start.
/// </summary>
public static class ServerCheck
{
    /// <summary>How long the server may take.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Checks <paramref name="server"/>. Typed failures: plain http to another machine (<see cref="FadenstackHttpsRequiredException"/>),
    /// no answer (<see cref="FadenstackUnreachableException"/>), a certificate that is not trusted (<see cref="ServerCertificateException"/>),
    /// and an answer that is not the platform's, a redirect too (<see cref="FadenstackApiException"/> with code
    /// <c>not_fadenstack</c> or <c>redirect</c>).
    /// </summary>
    /// <param name="server">The server's address (where <c>/api</c> is).</param>
    /// <param name="http">The client to use (tests, a host's proxy); one that follows no redirect when not given.</param>
    /// <param name="allowInsecureHttp">Allow plain http to another machine (a machine policy's http server, a development server).</param>
    /// <param name="trustedCaCertificate">A CA of the organisation's own, from the machine policy or the app's settings; never from a click.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static async Task CheckAsync(Uri server, HttpClient? http = null, bool allowInsecureHttp = false,
        byte[]? trustedCaCertificate = null, CancellationToken cancellationToken = default)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }
        if (server.Scheme != Uri.UriSchemeHttps && !(server.Scheme == Uri.UriSchemeHttp && (allowInsecureHttp || server.IsLoopback)))
        {
            throw new FadenstackHttpsRequiredException("The server's address must be https (or this machine).", nameof(server));
        }
        var url = new Uri(new Uri(server.AbsoluteUri.TrimEnd('/') + "/"), "api/health");
        var own = http is null;
        http ??= new HttpClient(FadenstackTransport.CreateHandler(new FadenstackClientOptions
        {
            ServerUrl = server, AllowInsecureHttp = true, TrustedCaCertificate = trustedCaCertificate,
        }));
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response;
            string text;
            try
            {
                response = await http.SendAsync(request, limit.Token).ConfigureAwait(false);
                text = await FadenstackTransport.TextOf(response).ConfigureAwait(false);
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FadenstackUnreachableException($"The server did not answer within {Timeout.TotalSeconds:0} s.", e);
            }
            catch (HttpRequestException e)
            {
                throw FadenstackUnreachableException.Of(e, $"The server could not be reached: {e.Message}");
            }
            using (response)
            {
                if (FadenstackTransport.Redirected(response, url) is { } redirected)
                {
                    throw redirected;
                }
                if (!response.IsSuccessStatusCode || !IsJsonObject(text))
                {
                    throw new FadenstackApiException(response.StatusCode, "This address does not answer like a Fadenstack server.",
                        "not_fadenstack");
                }
            }
        }
        finally
        {
            if (own)
            {
                http.Dispose();
            }
        }
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
