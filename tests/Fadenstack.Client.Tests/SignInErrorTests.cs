using System.Net;
using System.Security.Authentication;
using System.Text;
using Fadenstack.Auth;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>sdk/51 I15 step 1: the sign-in's typed errors, and the .NET password login (I-21).</summary>
public class SignInErrorTests
{
    private const string Login = "/api/auth/jwt/login";

    private static HttpClient Http(FakeServer server) => new(server);

    [Fact]
    public async Task The_password_login_gives_the_login_token_and_sends_the_form_once()
    {
        var server = new FakeServer().Json("POST", Login, new { access_token = "login-jwt", token_type = "bearer" });

        var token = await PasswordLogin.LoginAsync(new Uri("https://faden.test"), "ana@example.test", "s3cret", Http(server));

        Assert.Equal("login-jwt", token);
        var sent = Assert.Single(server.Requests);
        Assert.Equal("username=ana%40example.test&password=s3cret", sent.Body);
    }

    [Fact]
    public async Task A_wrong_password_is_its_own_error()
    {
        var server = new FakeServer().Json("POST", Login, new { detail = "LOGIN_BAD_CREDENTIALS" }, HttpStatusCode.BadRequest);

        var e = await Assert.ThrowsAsync<FadenstackBadCredentialsException>(() =>
            PasswordLogin.LoginAsync(new Uri("https://faden.test"), "ana@example.test", "wrong", Http(server)));

        Assert.Equal("LOGIN_BAD_CREDENTIALS", e.Code);
        Assert.DoesNotContain("wrong", e.Message);
    }

    [Fact]
    public async Task A_web_page_where_the_api_should_be_is_not_fadenstack()
    {
        var server = new FakeServer().On("POST", Login, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Welcome</html>", Encoding.UTF8, "text/html"),
        });

        var e = await Assert.ThrowsAsync<FadenstackApiException>(() =>
            PasswordLogin.LoginAsync(new Uri("https://faden.test"), "a@b.test", "p", Http(server)));

        Assert.Equal("not_fadenstack", e.Code);
    }

    [Theory]
    [InlineData("http://ai.example.test", false, true)]
    [InlineData("http://localhost:8000", false, false)]
    [InlineData("http://ai.example.test", true, false)]
    public async Task A_password_goes_only_over_https_or_to_this_machine(string api, bool insecure, bool refused)
    {
        var server = new FakeServer().Json("POST", Login, new { access_token = "t" });

        if (refused)
        {
            var e = await Assert.ThrowsAsync<FadenstackHttpsRequiredException>(() =>
                PasswordLogin.LoginAsync(new Uri(api), "a@b.test", "p", Http(server), insecure));
            Assert.IsAssignableFrom<ArgumentException>(e); // still an ArgumentException for the callers that catch that
            Assert.Empty(server.Requests);
        }
        else
        {
            Assert.Equal("t", await PasswordLogin.LoginAsync(new Uri(api), "a@b.test", "p", Http(server), insecure));
        }
    }

    [Fact]
    public async Task A_redirect_never_takes_the_password_along()
    {
        var server = new FakeServer().On("POST", Login, _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            r.Headers.Location = new Uri("https://evil.example/api/auth/jwt/login");
            return r;
        });

        var e = await Assert.ThrowsAsync<FadenstackApiException>(() =>
            PasswordLogin.LoginAsync(new Uri("https://faden.test"), "a@b.test", "p", Http(server)));

        Assert.Equal("redirect", e.Code);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Nothing_answering_is_unreachable_and_a_bad_certificate_says_so()
    {
        var down = new HttpClient(new Throwing(new HttpRequestException("No connection could be made")));
        var tls = new HttpClient(new Throwing(new HttpRequestException("SSL", new AuthenticationException("remote certificate is invalid"))));

        await Assert.ThrowsAsync<FadenstackUnreachableException>(() => PasswordLogin.LoginAsync(new Uri("https://faden.test"), "a", "p", down));
        await Assert.ThrowsAsync<ServerCertificateException>(() => PasswordLogin.LoginAsync(new Uri("https://faden.test"), "a", "p", tls));
    }

    [Fact]
    public async Task The_server_transport_types_its_failures_too()
    {
        using var down = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"), Agent = "a", Credentials = new StaticTokenProvider("t"),
            HttpClient = new HttpClient(new Throwing(new HttpRequestException("reset"))),
        });
        using var tls = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"), Agent = "a", Credentials = new StaticTokenProvider("t"),
            HttpClient = new HttpClient(new Throwing(new HttpRequestException("SSL", new AuthenticationException("untrusted")))),
        });

        await Assert.ThrowsAsync<FadenstackUnreachableException>(() => down.Agent!.GetProfileAsync());
        await Assert.ThrowsAsync<ServerCertificateException>(() => tls.Agent!.GetProfileAsync());
        Assert.Throws<FadenstackHttpsRequiredException>(() => new FadenstackClient(new FadenstackClientOptions { ServerUrl = new Uri("http://ai.example.test") }));
    }

    [Fact]
    public void An_endpoints_unreachable_is_also_the_servers_kind()
    {
        Assert.IsAssignableFrom<FadenstackUnreachableException>(new Endpoints.EndpointUnreachableException("x"));
    }

    private sealed class Throwing(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(error);
    }
}
