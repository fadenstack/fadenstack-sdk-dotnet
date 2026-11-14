using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Fadenstack.Http;

namespace Fadenstack.Auth;

/// <summary>
/// The password sign-in of a server without pairing (sdk/51 I15, I-21; TypeScript <c>loginWithPassword</c>): the person's
/// e-mail and password for a login token, which <see cref="AgentTokenProvider.SignInAsync"/> then trades for the agent's
/// device sign-in. The password goes once, to the server's own address, and is kept nowhere; nothing logs it.
/// </summary>
public static class PasswordLogin
{
    /// <summary>
    /// <c>POST {apiUrl}/api/auth/jwt/login</c> with the e-mail and password; the login token. Typed failures: a wrong e-mail
    /// or password (<see cref="FadenstackBadCredentialsException"/>), plain http to another machine
    /// (<see cref="FadenstackHttpsRequiredException"/>), no answer (<see cref="FadenstackUnreachableException"/>, or
    /// <see cref="ServerCertificateException"/>), a redirect (code <c>redirect</c>), and an answer that is no Fadenstack
    /// server's (code <c>not_fadenstack</c>).
    /// </summary>
    /// <param name="apiUrl">The server's backend (its address, where <c>/api</c> is).</param>
    /// <param name="email">The person's e-mail.</param>
    /// <param name="password">The person's password: sent once, kept nowhere.</param>
    /// <param name="http">The client to use (tests, a host's proxy); one that follows no redirect when not given.</param>
    /// <param name="allowInsecureHttp">Allow plain http to another machine (a development server only).</param>
    /// <param name="cancellationToken">Cancels the sign-in.</param>
    public static async Task<string> LoginAsync(Uri apiUrl, string email, string password, HttpClient? http = null,
        bool allowInsecureHttp = false, CancellationToken cancellationToken = default)
    {
        if (apiUrl is null)
        {
            throw new ArgumentNullException(nameof(apiUrl));
        }
        if (apiUrl.Scheme != Uri.UriSchemeHttps && !(apiUrl.Scheme == Uri.UriSchemeHttp && (allowInsecureHttp || apiUrl.IsLoopback)))
        {
            throw new FadenstackHttpsRequiredException(
                "A password goes only over https (or to this machine): use the server's https address.", nameof(apiUrl));
        }
        var url = new Uri(new Uri(apiUrl.AbsoluteUri.TrimEnd('/') + "/"), "api/auth/jwt/login");
        var own = http is null;
        http ??= new HttpClient(FadenstackTransport.CreateHandler(new FadenstackClientOptions { ServerUrl = apiUrl, AllowInsecureHttp = true }))
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", email ?? ""),
                    new KeyValuePair<string, string>("password", password ?? ""),
                }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw FadenstackUnreachableException.Of(e, $"Fadenstack could not be reached: {e.Message}");
            }
            using (response)
            {
                if (FadenstackTransport.Redirected(response, url) is { } redirected)
                {
                    throw redirected; // the password is never sent on
                }
                var text = await FadenstackTransport.TextOf(response).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.BadRequest && text.Contains("LOGIN_BAD_CREDENTIALS"))
                    {
                        throw new FadenstackBadCredentialsException(response.StatusCode, "The e-mail or password is not right.");
                    }
                    throw ErrorReader.ToException(response, text);
                }
                string? token;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    token = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("access_token", out var t)
                        && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                }
                catch (JsonException)
                {
                    // A web page where the API should be: another server, or a path in front of Fadenstack's.
                    throw new FadenstackApiException(response.StatusCode, "This address does not answer like a Fadenstack server.",
                        "not_fadenstack");
                }
                return token is { Length: > 0 } ? token : throw new FadenstackException("The server gave no login token.");
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
}
