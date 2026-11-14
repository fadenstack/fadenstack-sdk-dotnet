using System.Net;
using System.Security.Authentication;
using System.Text;
using Fadenstack.Auth;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>sdk/51 I15 §2 "Checking": <c>GET {server}/api/health</c>, 200 and a JSON object, and the typed failures.</summary>
public class ServerCheckTests
{
    private static readonly Uri Server = new("https://x.test");

    private static HttpResponseMessage Text(HttpStatusCode status, string body, string type) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static Task Check(FakeServer server, Uri? address = null, bool allowInsecureHttp = false) =>
        ServerCheck.CheckAsync(address ?? Server, new HttpClient(server), allowInsecureHttp);

    [Fact]
    public async Task A_200_with_a_json_object_passes()
    {
        var server = new FakeServer().Json("GET", "/api/health", new { status = "ok", environment = "prod" });

        await Check(server);

        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task The_request_is_a_GET_of_api_health_asking_for_json_and_nothing_else()
    {
        var server = new FakeServer().Json("GET", "/api/health", new { status = "ok" });

        await Check(server);

        var sent = Assert.Single(server.Requests);
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api/health", sent.Path);
        Assert.Equal("", sent.Query);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Null(sent.Header("Authorization"));
        Assert.Equal("", sent.Body);
    }

    [Theory]
    [InlineData("https://x.test/faden/", "/faden/api/health")]
    [InlineData("https://x.test/faden", "/faden/api/health")]
    [InlineData("https://x.test/a/b/", "/a/b/api/health")]
    [InlineData("https://x.test/", "/api/health")]
    public async Task A_path_the_server_sits_under_is_kept(string address, string asked)
    {
        var server = new FakeServer().Json("GET", asked, new { status = "ok" });

        await Check(server, new Uri(address));

        Assert.Equal(asked, Assert.Single(server.Requests).Path);
    }

    [Fact]
    public async Task A_200_with_html_is_not_fadenstack()
    {
        var server = new FakeServer().On("GET", "/api/health", _ => Text(HttpStatusCode.OK, "<html><body>Welcome</body></html>", "text/html"));

        var e = await Assert.ThrowsAsync<FadenstackApiException>(() => Check(server));

        Assert.Equal("not_fadenstack", e.Code);
    }

    [Fact]
    public async Task A_200_with_a_json_array_is_not_fadenstack()
    {
        var server = new FakeServer().Json("GET", "/api/health", "[1, 2, 3]");

        var e = await Assert.ThrowsAsync<FadenstackApiException>(() => Check(server));

        Assert.Equal("not_fadenstack", e.Code);
    }

    [Theory]
    [InlineData("\"ok\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("")]
    public async Task A_200_with_json_that_is_not_an_object_or_not_json_is_not_fadenstack(string body)
    {
        var server = new FakeServer().Json("GET", "/api/health", body);

        var e = await Assert.ThrowsAsync<FadenstackApiException>(() => Check(server));

        Assert.Equal("not_fadenstack", e.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_status_that_is_not_a_success_is_not_fadenstack_even_with_a_json_object(HttpStatusCode status)
    {
        var server = new FakeServer().Json("GET", "/api/health", new { status = "ok" }, status);

        var e = await Assert.ThrowsAsync<FadenstackApiException>(() => Check(server));

        Assert.Equal("not_fadenstack", e.Code);
        Assert.Equal(status, e.Status);
    }

    [Fact]
    public async Task A_404_is_not_fadenstack()
    {
        var server = new FakeServer(); // no route: the fake answers 404

        var e = await Assert.ThrowsAsync<FadenstackApiException>(() => Check(server));

        Assert.Equal("not_fadenstack", e.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Moved)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData((HttpStatusCode)308)]
    public async Task A_redirect_is_code_redirect_and_is_not_followed(HttpStatusCode status)
    {
        var server = new FakeServer().On("GET", "/api/health", _ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.TryAddWithoutValidation("Location", "https://elsewhere.test/api/health");
            return response;
        }).Json("GET", "/elsewhere", new { status = "ok" });

        var e = await Assert.ThrowsAsync<FadenstackApiException>(() => Check(server));

        Assert.Equal("redirect", e.Code);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Plain_http_to_another_machine_is_refused_and_nothing_is_sent()
    {
        var server = new FakeServer().Json("GET", "/api/health", new { status = "ok" });

        var e = await Assert.ThrowsAsync<FadenstackHttpsRequiredException>(() => Check(server, new Uri("http://ai.example.test")));

        Assert.Equal("server", e.ParamName);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("http://localhost:8000")]
    [InlineData("http://127.0.0.1:8000/faden/")]
    [InlineData("http://[::1]:8000")]
    public async Task Plain_http_to_this_machine_goes(string address)
    {
        var uri = new Uri(address);
        var server = new FakeServer().Json("GET", uri.AbsolutePath.TrimEnd('/') + "/api/health", new { status = "ok" });

        await Check(server, uri);

        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task AllowInsecureHttp_lets_plain_http_to_another_machine_go()
    {
        var server = new FakeServer().Json("GET", "/api/health", new { status = "ok" });

        await Check(server, new Uri("http://corp.internal:8080"), allowInsecureHttp: true);

        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_missing_server_address_throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => ServerCheck.CheckAsync(null!, new HttpClient(new FakeServer())));
    }

    [Fact]
    public async Task A_request_that_fails_is_unreachable()
    {
        var server = new FakeServer().On("GET", "/api/health", _ => throw new HttpRequestException("connection refused"));

        var e = await Assert.ThrowsAsync<FadenstackUnreachableException>(() => Check(server));

        Assert.IsType<HttpRequestException>(e.InnerException);
    }

    [Fact]
    public async Task A_failed_tls_handshake_is_a_certificate_error()
    {
        var server = new FakeServer().On("GET", "/api/health",
            _ => throw new HttpRequestException("ssl", new AuthenticationException("The remote certificate is invalid.")));

        var e = await Assert.ThrowsAsync<ServerCertificateException>(() => Check(server));

        Assert.IsType<HttpRequestException>(e.InnerException);
        Assert.Contains("Ask IT", e.Message);
    }

    [Fact]
    public async Task An_authentication_error_deeper_in_the_chain_is_a_certificate_error_too()
    {
        var server = new FakeServer().On("GET", "/api/health", _ => throw new HttpRequestException("ssl",
            new IOException("handshake", new AuthenticationException("The remote certificate is invalid."))));

        await Assert.ThrowsAsync<ServerCertificateException>(() => Check(server));
    }

    [Fact]
    public async Task The_client_the_caller_gave_is_not_disposed()
    {
        var server = new FakeServer().Json("GET", "/api/health", new { status = "ok" });
        using var http = new HttpClient(server);

        await ServerCheck.CheckAsync(Server, http);
        await ServerCheck.CheckAsync(Server, http);

        Assert.Equal(2, server.Requests.Count);
    }
}
