using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Agents.Tests;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Provenance.Tests;

/// <summary>The receipts a turn's answers carry: by id in the transcript, whole in the result.</summary>
public class ReceiptTests
{
    [Fact]
    public async Task Each_answers_entry_keeps_its_receipts_ids_and_the_result_the_turns_receipts()
    {
        var gateway = new Gateway(contract: WritingTools.Contract, userLanguage: "en")
            .SaysAndCalls("Noting it.", Gateway.Receipt("rc-1"), ("call-1", "add_crm_note", JsonSerializer.Serialize(new { note = TestAgents.LongText })))
            .Then(Gateway.Text("Done."), Gateway.Finish("stop"), Gateway.Unsigned, Fadenstack.Client.Tests.FakeServer.Done);
        using var agent = TestAgents.Build(gateway, new WritingTools());

        var session = await agent.StartSessionAsync(ToolMode.Auto);
        var result = await session.SendAsync("Note it.");

        Assert.Equal(new[] { ("rc-1", true), ((string?)null, false) }, result.Receipts.Select(r => (r.ReceiptId, r.Signed is not null)));
        var answers = session.Transcript.Where(e => e.Type == EntryTypes.AssistantMessage).Select(e => e.Data).ToList();
        var first = Assert.Single(answers[0].GetProperty("receipts").EnumerateArray());
        Assert.Equal(("rc-1", "signed", false), (first.GetProperty("receipt_id").GetString(), first.GetProperty("status").GetString(),
            first.TryGetProperty("reason", out _)));
        var second = Assert.Single(answers[1].GetProperty("receipts").EnumerateArray());
        Assert.Equal(("unsigned", "server-unavailable", false), (second.GetProperty("status").GetString(), second.GetProperty("reason").GetString(),
            second.TryGetProperty("receipt_id", out _)));
        Assert.DoesNotContain("output_hash", answers[0].GetRawText()); // the id, never the receipt
    }
}
