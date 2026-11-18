using System.Net;
using Fadenstack.Auth;
using Fadenstack.Chat;
using Fadenstack.Endpoints;
using Fadenstack.Http;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>
/// A <c>Location</c> header is resolved against the request's URI, the same on every OS. On Linux and macOS a relative
/// <c>/v1/models</c> read with <c>new Uri(text, UriKind.RelativeOrAbsolute)</c> is the absolute <c>file:///v1/models</c>, which
/// the same-origin check then refused (OWASP ASVS 5.0 V12).
/// </summary>
public class EndpointRedirectLocationTests
{
    private static readonly Uri Request = new("https://api.example.test/v1/chat/completions");

    [Theory]
    [InlineData("/x", "https://api.example.test/x")]
    [InlineData("x", "https://api.example.test/v1/chat/x")]
    [InlineData("../x", "https://api.example.test/v1/x")]
    [InlineData("//other.example/x", "https://other.example/x")] // protocol-relative: another host
    [InlineData("https://api.example.test/y", "https://api.example.test/y")]
    public void A_location_resolves_against_the_request_whatever_the_OS_reads_it_as(string location, string expected)
    {
        var to = EndpointUrl.ResolveLocation(Request, new Uri(location, UriKind.RelativeOrAbsolute));

        Assert.NotNull(to);
        Assert.Equal(expected, to!.AbsoluteUri);
        Assert.Equal(new Uri(expected).Scheme, to.Scheme); // never "file"
        Assert.Equal(new Uri(expected).Host, to.Host);
    }

    [Fact]
    public void A_protocol_relative_location_is_another_origin_and_a_path_is_the_same_one()
    {
        Assert.NotEqual(EndpointUrl.Origin(Request), EndpointUrl.Origin(EndpointUrl.ResolveLocation(Request, new Uri("//other.example/x", UriKind.RelativeOrAbsolute))!));
        Assert.Equal(EndpointUrl.Origin(Request), EndpointUrl.Origin(EndpointUrl.ResolveLocation(Request, new Uri("/x", UriKind.RelativeOrAbsolute))!));
        Assert.Null(EndpointUrl.ResolveLocation(Request, null));
    }

    [Fact]
    public void The_transports_refusal_names_where_a_relative_location_pointed()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location = new Uri("/elsewhere", UriKind.RelativeOrAbsolute);

        var e = FadenstackTransport.Redirected(response, new Uri("https://api.example.test/v1/models"));

        Assert.NotNull(e);
        Assert.Contains("https://api.example.test/elsewhere", e!.Message);
    }

    private static async Task Stream(DirectChatClient client)
    {
        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hi")]))
        {
        }
    }

    private static (DirectChatClient Client, OpenAiFake Fake) Make()
    {
        var fake = new OpenAiFake();
        return (new DirectChatClient(new ModelEndpoint
        {
            BaseUrl = new Uri("https://api.example.test/v1"), Model = "m", Key = new StaticTokenProvider("k-123"), Handler = fake,
        }), fake);
    }

    [Theory]
    [InlineData("/v2/chat/completions", "/v2/chat/completions")]
    [InlineData("done", "/v1/chat/done")]
    [InlineData("../v3/done", "/v1/v3/done")]
    public async Task A_relative_location_on_the_same_origin_is_followed(string location, string path)
    {
        var (client, fake) = Make();
        fake.Intercept = r => r.Url.AbsolutePath == "/v1/chat/completions" ? OpenAiFake.Redirect(307, location) : null;

        try
        {
            await Stream(client);
        }
        catch (FadenstackException)
        {
            // the fake has no chat API at some targets (404): the second request is what is asserted
        }

        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal("https", fake.Requests[1].Url.Scheme);
        Assert.Equal("api.example.test", fake.Requests[1].Url.Host);
        Assert.Equal(path, fake.Requests[1].Url.AbsolutePath);
    }

    [Fact]
    public async Task A_protocol_relative_location_is_refused_and_the_key_never_follows()
    {
        var (client, fake) = Make();
        fake.Intercept = r => OpenAiFake.Redirect(307, "//other.example/v1/chat/completions");

        var e = await Assert.ThrowsAsync<FadenstackApiException>(() => Stream(client));

        Assert.Equal("redirect", e.Code);
        Assert.Single(fake.Requests);
        Assert.DoesNotContain(fake.Requests, r => r.Url.Host == "other.example");
    }
}
