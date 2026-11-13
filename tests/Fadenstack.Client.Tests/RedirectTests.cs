using System.Net;
using Fadenstack.Auth;
using Fadenstack.Http;
using Fadenstack.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>
/// The SDK follows no redirect on API and token requests: a 307 or 308 would send the body again (chat text, a refresh
/// token, a pairing code) to wherever it points. A redirect is a typed error that names where it pointed, and nothing
/// reaches the other address (sdk/51 I00 #7, item 14 and N4). Over real sockets: another port is another origin.
/// </summary>
public sealed class RedirectTests : IDisposable
{
    private const string Agent = "docs-assistant";
    private readonly LoopbackServer _elsewhere = new((_, _) => LoopbackServer.Json("{}"));
    private readonly LoopbackServer _server;

    public RedirectTests() =>
        _server = new LoopbackServer((_, path) => LoopbackServer.Redirect(307, new Uri(_elsewhere.Url, path.TrimStart('/'))));

    public void Dispose()
    {
        _server.Dispose();
        _elsewhere.Dispose();
    }

    private FadenstackClient Client() => new(new FadenstackClientOptions
    {
        ServerUrl = _server.Url, Agent = Agent, Credentials = new StaticTokenProvider("SECRET-TOKEN"), AllowInsecureHttp = true,
    });

    private void AssertRefused(FadenstackApiException error)
    {
        Assert.Equal(HttpStatusCode.TemporaryRedirect, error.Status);
        Assert.Equal("redirect", error.Code);
        Assert.Contains(_elsewhere.Url.Authority, error.Message); // where it pointed
        Assert.Single(_server.Requests);
        Assert.Empty(_elsewhere.Requests); // nothing was sent there
    }

    [Fact]
    public void The_sdks_own_handler_follows_no_redirect() =>
        Assert.False(((HttpClientHandler)FadenstackTransport.CreateHandler(new FadenstackClientOptions())).AllowAutoRedirect);

    [Fact]
    public async Task A_redirected_api_call_sends_nothing_elsewhere_and_names_where_it_pointed()
    {
        using var client = Client();

        AssertRefused(await Assert.ThrowsAsync<FadenstackApiException>(() =>
            client.Agent!.CreateGrantAsync(new GrantRequest { Kind = GrantKinds.Work })));
    }

    [Fact]
    public async Task A_redirected_chat_never_sends_the_text_elsewhere()
    {
        using var client = Client();

        AssertRefused(await Assert.ThrowsAsync<FadenstackApiException>(async () =>
        {
            await foreach (var _ in client.Chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "SECRET plan")]))
            {
            }
        }));
    }

    [Fact]
    public async Task A_redirected_renewal_never_sends_the_refresh_token_elsewhere_and_keeps_the_sign_in()
    {
        var store = new MemoryTokenStore();
        var key = AgentTokenProvider.KeyOf(_server.Url, Agent);
        await store.SaveAsync(key, new TokenSet
        {
            AccessToken = "a", AccessExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), RefreshToken = "SECRET-REFRESH",
            RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1), DeviceId = "d", AgentId = "a-1", Agent = Agent,
        });
        var tokens = new AgentTokenProvider(_server.Url, Agent, store); // no HttpClient given: the provider's own

        AssertRefused(await Assert.ThrowsAsync<FadenstackApiException>(() => tokens.GetAccessTokenAsync()));
        Assert.Equal("SECRET-REFRESH", (await store.LoadAsync(key))!.RefreshToken); // a redirect does not end the sign-in
    }

    [Fact]
    public async Task A_redirected_pairing_never_sends_the_request_elsewhere()
    {
        using var http = new HttpClient(FadenstackTransport.CreateHandler(new FadenstackClientOptions()));
        var pairing = new AgentPairing(_server.Url, http, new MemoryTokenStore());

        AssertRefused(await Assert.ThrowsAsync<FadenstackApiException>(() =>
            pairing.StartAsync(Agent, "PC", new PairingClient("Desktop assistant", "1.2.3", "Windows 11"))));
    }
}
