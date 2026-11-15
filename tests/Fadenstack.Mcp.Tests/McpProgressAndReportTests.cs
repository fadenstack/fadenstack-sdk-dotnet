using Fadenstack.Mcp;

namespace Fadenstack.Mcp.Tests;

/// <summary>A call's progress reaches the host in order, and the server is told an address without what can hold a token.</summary>
public sealed class McpProgressAndReportTests
{
    private static McpProgress At(float value) => new("s", "t", "notes-db", "export", $"page {value}", value, 3f);

    [Fact]
    public void A_progress_older_than_the_last_one_forwarded_is_dropped()
    {
        var gate = new McpProgressGate();
        var seen = new List<float>();

        foreach (var value in new[] { 2f, 1f, 3f, 3f, 2.5f })
            gate.Forward(At(value), p => seen.Add(p.Progress));

        Assert.Equal(new[] { 2f, 3f, 3f }, seen); // the same value again may carry a new message; an older one never shows
    }

    [Fact]
    public void Values_forwarded_from_many_threads_reach_the_host_in_order()
    {
        var gate = new McpProgressGate();
        var seen = new List<float>();
        var next = 0;
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < 200; i++)
                gate.Forward(At(Interlocked.Increment(ref next)), p => seen.Add(p.Progress)); // the host is called under the gate
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(seen.OrderBy(v => v), seen);
        Assert.Equal(1600f, seen[seen.Count - 1]);
    }

    [Theory]
    [InlineData("https://mcp.example.com/mcp?token=s3cret#frag", "https://mcp.example.com/mcp")]
    [InlineData("https://anna:s3cret@mcp.example.com:8443/v1/mcp", "https://mcp.example.com:8443/v1/mcp")]
    [InlineData("http://127.0.0.1:3000/", "http://127.0.0.1:3000/")]
    [InlineData("http://[::1]:8080/mcp?x=1", "http://[::1]:8080/mcp")]
    public void The_reported_address_has_no_user_password_query_or_fragment(string url, string reported)
    {
        Assert.Equal(reported, McpHttp.ReportedAddress(new Uri(url)));
    }
}
