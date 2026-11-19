using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>The machine policy's <c>supportEmail</c> (SDK 0.3.1, "Report a problem"): one plain address, or it counts as absent and is reported.</summary>
public class SupportEmailPolicyTests
{
    [Theory]
    [InlineData("a@b.example")]
    [InlineData("it-help+reports@mail.example.org")]
    public void One_plain_address_is_read_and_the_key_is_locked(string address)
    {
        var policy = MachinePolicy.FromJson($$"""{"supportEmail":"{{address}}"}""");

        Assert.Equal(address, policy.SupportEmail);
        Assert.Equal(new[] { "supportEmail" }, policy.Locked);
        Assert.Empty(policy.Warnings);
        Assert.Contains("supportEmail", MachinePolicy.Keys);
    }

    [Fact]
    public void Without_the_key_there_is_no_address_and_a_server_does_not_change_that()
    {
        Assert.Null(MachinePolicy.None.SupportEmail);
        Assert.Null(MachinePolicy.Unreadable.SupportEmail);
        Assert.Null(MachinePolicy.FromJson("{}").SupportEmail);
        Assert.Null(MachinePolicy.FromJson("""{"server":"https://ai.example.test"}""").SupportEmail);
    }

    [Theory]
    [InlineData("""{"supportEmail":"a@b.example?bcc=x@y.example"}""")]
    [InlineData("""{"supportEmail":"a@b.example?subject=Hi&body=x"}""")]
    [InlineData("""{"supportEmail":"a@b.example,c@d.example"}""")]
    [InlineData("""{"supportEmail":"a b@c.example"}""")]
    [InlineData("""{"supportEmail":"a@b.example\r\nBcc: x@y.example"}""")]
    [InlineData("""{"supportEmail":"a%40b.example"}""")]
    [InlineData("""{"supportEmail":"https://helpdesk.example/new"}""")]
    [InlineData("""{"supportEmail":""}""")]
    [InlineData("""{"supportEmail":null}""")]
    [InlineData("""{"supportEmail":42}""")]
    [InlineData("""{"supportEmail":["a@b.example"]}""")]
    public void A_value_that_is_not_one_plain_address_counts_as_absent_and_is_reported_without_the_value(string json)
    {
        var policy = MachinePolicy.FromJson(json);

        Assert.Null(policy.SupportEmail);
        var warning = Assert.Single(policy.Warnings);
        Assert.Equal("'supportEmail' is not one plain mail address: ignored.", warning);
        Assert.Equal(new[] { "supportEmail" }, policy.Locked);
    }

    [Fact]
    public void A_longer_address_than_254_characters_is_refused()
    {
        var longest = new string('x', 254 - "@b.example".Length) + "@b.example";

        Assert.Equal(longest, MachinePolicy.FromJson($$"""{"supportEmail":"{{longest}}"}""").SupportEmail);
        Assert.Null(MachinePolicy.FromJson($$"""{"supportEmail":"x{{longest}}"}""").SupportEmail);
    }
}
