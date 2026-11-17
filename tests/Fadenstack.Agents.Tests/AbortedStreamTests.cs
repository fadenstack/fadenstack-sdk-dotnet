using Fadenstack.Client.Tests;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>A stop the person chose is the cancel, also when the transport reports it as an I/O error (live finding of the desktop session).</summary>
public class AbortedStreamTests
{
    [Fact]
    public async Task A_stop_whose_read_fails_with_an_IOException_ends_the_turn_as_cancelled_and_throws_the_cancel()
    {
        var gateway = new Gateway();
        gateway.Then(AbortedStream.Answer(Gateway.Text("Reading the page"), out var stream));
        using var agent = gateway.Builder().Build();
        var session = await agent.StartSessionAsync();

        var thrown = default(Exception);
        var task = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in session.StreamAsync("Go."))
                {
                }
            }
            catch (Exception e)
            {
                thrown = e;
            }
        });
        await stream.Started.Task;
        await Task.Delay(100); // the reader is waiting on the next line
        Assert.True(session.Stop());
        await task;

        var cancel = Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.IsType<IOException>(cancel.InnerException);
    }
}
