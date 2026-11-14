using System.Net;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The client's half of the lost-decline release gate (14 Nov): an answer to a gateway's approval that gets no word back is
/// sent once more with the same body, which a gateway with <c>approvals.idempotent</c> (core 0.7.1 rc17) takes once.
/// </summary>
public class ApprovalAnswerRetryTests
{
    private const string Answer = "/v1/agents/docs-assistant/approvals/ap-1";

    private static Gateway Waiting(string features = """["agents"]""")
    {
        var gateway = new Gateway { Features = features };
        gateway.Then(
            Gateway.Custom("faden.approval_required", new
            {
                approvalId = "ap-1", toolCallId = "gw-1", toolCallName = "mcp.github.comment", message = "The tool mode asks.",
                timeoutS = 300, toolClass = "write", rule = true,
            }),
            Gateway.Text("No comment then."), Gateway.Finish("stop"), FakeServer.Done);
        return gateway;
    }

    /// <summary>The agent over a handler that does what <paramref name="first"/> says to the first answer, then passes on.</summary>
    private static FadenstackAgent Build(Gateway gateway, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>?> first)
    {
        var agent = FadenstackAgent.CreateBuilder()
            .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"),
                o => o.HttpClient = new HttpClient(new FirstAnswer(gateway.Server, first)))
            .UseAgent(Gateway.Slug)
            .UseSessionStore(new Sessions.MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore())
            .UseApprovals((_, _) => Task.FromResult(ApprovalDecision.Deny()))
            .Build();
        agent.ApprovalAnswerTimeout = TimeSpan.FromMilliseconds(300);
        return agent;
    }

    /// <summary>A first answer that never gets a word back (the lost decline): cancelled only by the SDK's short wait.</summary>
    private static Task<HttpResponseMessage>? Hang(HttpRequestMessage request, CancellationToken cancellationToken) => HangAsync(cancellationToken);

    private static async Task<HttpResponseMessage> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }

    private static List<Recorded> Answers(Gateway gateway) => gateway.Server.Requests.Where(r => r.Path == Answer).ToList();

    [Fact]
    public async Task A_lost_answer_is_sent_once_more_with_the_same_body()
    {
        var gateway = Waiting("""["agents","approvals.idempotent"]""");
        gateway.Server.Json("POST", Answer, new { approval_id = "ap-1", approved = false, scope = (string?)null });
        using var agent = Build(gateway, Hang);

        var result = await (await agent.StartSessionAsync()).SendAsync("Comment.");

        Assert.Equal("No comment then.", result.Text);
        var answers = Answers(gateway);
        Assert.Single(answers); // the hung first try never reached the fake; the retry did
        Assert.False(answers[0].Json.GetProperty("approved").GetBoolean());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task No_word_back_is_retried_and_the_body_is_the_same(int status)
    {
        var gateway = Waiting("""["agents","approvals.idempotent"]""");
        gateway.Server.Json("POST", Answer, new { approval_id = "ap-1", approved = false, scope = (string?)null });
        string? firstBody = null;
        using var agent = Build(gateway, async (request, _) =>
        {
            firstBody = await request.Content!.ReadAsStringAsync();
            return status == 0 ? throw new HttpRequestException("connection reset") : new HttpResponseMessage((HttpStatusCode)status);
        });

        var result = await (await agent.StartSessionAsync()).SendAsync("Comment.");

        Assert.Equal("No comment then.", result.Text);
        Assert.Equal(firstBody, Assert.Single(Answers(gateway)).Body);
    }

    [Fact]
    public async Task An_older_gateway_answers_the_repeat_of_a_taken_answer_404_which_counts_as_taken()
    {
        var gateway = Waiting(); // no approvals.idempotent
        gateway.Server.Json("POST", Answer, new { error = new { message = "No such approval", code = "approval_not_found" } }, HttpStatusCode.NotFound);
        using var agent = Build(gateway, Hang);

        var result = await (await agent.StartSessionAsync()).SendAsync("Comment.");

        Assert.Equal("No comment then.", result.Text);
    }

    [Fact]
    public async Task With_idempotent_answers_a_404_on_the_repeat_is_an_error()
    {
        var gateway = Waiting("""["agents","approvals.idempotent"]""");
        gateway.Server.Json("POST", Answer, new { error = new { message = "No such approval", code = "approval_not_found" } }, HttpStatusCode.NotFound);
        using var agent = Build(gateway, Hang);

        var e = await Assert.ThrowsAsync<FadenstackApiException>(async () => await (await agent.StartSessionAsync()).SendAsync("Comment."));

        Assert.Equal(HttpStatusCode.NotFound, e.Status);
    }

    [Fact]
    public async Task A_409_is_never_sent_again()
    {
        var gateway = Waiting("""["agents","approvals.idempotent"]""");
        gateway.Server.Json("POST", Answer, new { error = new { message = "Already answered", code = "approval_already_answered" } }, HttpStatusCode.Conflict);
        using var agent = Build(gateway, (_, _) => null);

        var e = await Assert.ThrowsAsync<FadenstackApiException>(async () => await (await agent.StartSessionAsync()).SendAsync("Comment."));

        Assert.Equal("approval_already_answered", e.Code);
        Assert.Single(Answers(gateway));
    }

    [Fact]
    public async Task An_answer_that_is_taken_is_sent_once()
    {
        var gateway = Waiting();
        gateway.Server.Json("POST", Answer, new { approval_id = "ap-1", approved = false, scope = (string?)null });
        using var agent = Build(gateway, (_, _) => null);

        await (await agent.StartSessionAsync()).SendAsync("Comment.");

        Assert.Single(Answers(gateway));
    }

    [Fact]
    public async Task Two_tries_without_a_word_end_the_turn_with_the_error()
    {
        var gateway = Waiting("""["agents","approvals.idempotent"]""");
        using var agent = Build(gateway, (_, _) => throw new HttpRequestException("down"));
        gateway.Server.On("POST", Answer, _ => throw new HttpRequestException("still down"));

        await Assert.ThrowsAsync<FadenstackException>(async () => await (await agent.StartSessionAsync()).SendAsync("Comment."));
    }

    /// <summary>Does what a test says to the first approval answer; everything else, and later answers, go to the fake.</summary>
    private sealed class FirstAnswer(HttpMessageHandler inner, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>?> first)
        : DelegatingHandler(inner)
    {
        private int _answers;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == Answer && Interlocked.Increment(ref _answers) == 1
                && first(request, cancellationToken) is { } handled)
            {
                return await handled;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
