using System.Net;
using System.Text;
using System.Text.Json;
using Fadenstack.Auth;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>
/// Two processes keep the same agent sign-in (two Office applications, a desktop agent beside an add-in): the refresh token
/// changes on every use, and the server ends the whole sign-in when an old one comes back. So one process renews at a
/// time, and the other takes the renewed sign-in from the store (sdk/51 I00 #2, N1).
/// </summary>
public sealed class TokenRenewalAcrossProcessesTests : IDisposable
{
    private static readonly Uri Server = new("https://faden.test");
    private const string Agent = "docs-assistant";
    private static readonly string Key = AgentTokenProvider.KeyOf(Server, Agent);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "faden-renewal-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    /// <summary>A sign-in whose access token has expired and whose refresh token is still good.</summary>
    private Task KeepExpiredSignInAsync() => new FileTokenStore(_directory).SaveAsync(Key, new TokenSet
    {
        AccessToken = "access-1", AccessExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        RefreshToken = "refresh-1", RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        DeviceId = "device-1", AgentId = "agent-1", Agent = Agent,
    });

    /// <summary>A process: its own store object over the shared folder, its own provider.</summary>
    private AgentTokenProvider Process(RotatingServer server) =>
        new(Server, Agent, new FileTokenStore(_directory), new HttpClient(server, disposeHandler: false));

    [Fact]
    public async Task Two_processes_renewing_at_the_same_moment_renew_once_and_both_keep_the_sign_in()
    {
        await KeepExpiredSignInAsync();
        var server = new RotatingServer(TimeSpan.FromMilliseconds(300));
        var first = Process(server);
        var second = Process(server);

        var tokens = await Task.WhenAll(Task.Run(() => first.GetAccessTokenAsync()), Task.Run(() => second.GetAccessTokenAsync()));

        Assert.Equal(1, server.Renewals);
        Assert.Equal(new[] { "access-2", "access-2" }, tokens);
        Assert.Equal("refresh-2", (await new FileTokenStore(_directory).LoadAsync(Key))!.RefreshToken);
    }

    [Fact]
    public async Task A_process_whose_kept_token_another_process_renewed_takes_the_renewed_one()
    {
        await KeepExpiredSignInAsync();
        var server = new RotatingServer(TimeSpan.Zero);
        var first = Process(server);
        var second = Process(server);
        Assert.True(await second.IsSignedInAsync()); // the second process has read the old sign-in

        Assert.Equal("access-2", await first.GetAccessTokenAsync());
        Assert.Equal("access-2", await second.GetAccessTokenAsync());

        Assert.Equal(1, server.Renewals);
        Assert.Equal(0, server.Reused);
    }

    [Fact]
    public async Task The_store_lets_one_holder_at_a_time_renew_a_sign_in()
    {
        var store = new FileTokenStore(_directory);
        var other = new FileTokenStore(_directory); // another process's

        var held = await store.LockAsync(Key);
        var waiting = other.LockAsync(Key);
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);

        held.Dispose();
        (await waiting).Dispose();
        using var unrelated = await other.LockAsync(AgentTokenProvider.KeyOf(Server, "another-agent")); // per sign-in
    }

    [Fact]
    public async Task The_memory_store_lets_one_holder_at_a_time_renew_a_sign_in()
    {
        var store = new MemoryTokenStore();

        var held = await store.LockAsync(Key);
        var waiting = store.LockAsync(Key);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        held.Dispose();
        (await waiting).Dispose();
    }

    /// <summary>The backend's refresh route: each refresh token works once; one that comes back ends the sign-in.</summary>
    private sealed class RotatingServer : HttpMessageHandler
    {
        private readonly TimeSpan _takes;
        private readonly object _gate = new();
        private string? _valid = "refresh-1";
        private int _issued = 1;
        private int _renewals;
        private int _reused;

        public RotatingServer(TimeSpan takes) => _takes = takes;

        public int Renewals => Volatile.Read(ref _renewals);

        public int Reused => Volatile.Read(ref _reused);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/agent-tokens/refresh", request.RequestUri!.AbsolutePath);
            var presented = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))
                .RootElement.GetProperty("refresh_token").GetString();
            await Task.Delay(_takes, cancellationToken); // long enough for the other process to try meanwhile
            lock (_gate)
            {
                if (presented is null || presented != _valid)
                {
                    _reused++;
                    _valid = null; // reuse detection: the whole sign-in ends
                    return Answer(HttpStatusCode.Unauthorized, new { detail = new { code = "refresh_reused", message = "Signed out." } });
                }
                _renewals++;
                _issued++;
                _valid = $"refresh-{_issued}";
                return Answer(HttpStatusCode.OK, new
                {
                    access_token = $"access-{_issued}", expires_in = 600, refresh_token = _valid, refresh_expires_in = 86400,
                    device_id = "device-1", agent_id = "agent-1", agent = Agent,
                });
            }
        }

        private static HttpResponseMessage Answer(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
