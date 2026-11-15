using System.Globalization;
using Fadenstack.Agents;
using Fadenstack.Tools;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>sdk/51 F08 §3: the card's "Say why" field; Deny sends the person's reason, or none.</summary>
public class ApprovalReasonCardTests
{
    private static (ApprovalPart Card, PendingApproval Pending) Card(string culture = "en")
    {
        var pending = new PendingApproval(new ApprovalRequest { ToolName = "add_comment", ToolClass = ToolClass.Write, CallId = "call-1" }, CancellationToken.None);
        return (new ApprovalPart(pending, 200, new UiStrings(CultureInfo.GetCultureInfo(culture))), pending);
    }

    [Fact]
    public async Task Say_why_opens_the_field_and_deny_sends_the_trimmed_reason()
    {
        var (card, pending) = Card();
        Assert.False(card.IsReasonOpen);
        card.ToggleReasonCommand.Execute(null);
        Assert.True(card.IsReasonOpen);
        card.DenyReason = "  Wrong page.  ";

        card.DenyCommand.Execute(null);

        var decision = await pending.Decision;
        Assert.False(decision.Approved);
        Assert.Equal("Wrong page.", decision.Reason);
        Assert.Equal(("Your reason: Wrong page.", true), (card.DeclinedReasonText, card.IsReasonShown));
        Assert.False(card.IsReasonOpen); // closed once decided, and no longer editable
        Assert.False(card.ToggleReasonCommand.CanExecute(null));
        card.ToggleReasonCommand.Execute(null);
        Assert.False(card.IsReasonOpen);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task An_empty_reason_sends_none_and_shows_none(string typed)
    {
        var (card, pending) = Card();
        card.ToggleReasonCommand.Execute(null);
        card.DenyReason = typed;

        card.DenyCommand.Execute(null);

        Assert.Null((await pending.Decision).Reason);
        Assert.Equal(("", false), (card.DeclinedReasonText, card.IsReasonShown));
    }

    [Fact]
    public void Esc_toggling_the_field_closes_it_without_deciding_and_a_closed_field_still_denies_with_the_text()
    {
        var (card, pending) = Card();
        card.ToggleReasonCommand.Execute(null);
        card.ToggleReasonCommand.Execute(null); // Esc in the box runs the same command
        Assert.False(card.IsReasonOpen);
        Assert.Equal(ApprovalState.Pending, card.State);
        Assert.False(pending.IsResolved);
    }

    [Fact]
    public async Task A_second_answer_changes_nothing()
    {
        var (card, pending) = Card();
        card.DenyReason = "First.";
        card.DenyCommand.Execute(null);
        card.DenyReason = "Second.";
        card.AllowCommand.Execute(null);
        card.DenyCommand.Execute(null);

        var decision = await pending.Decision;
        Assert.Equal("First.", decision.Reason);
        Assert.Equal(ApprovalState.Denied, card.State);
        Assert.Equal("Your reason: First.", card.DeclinedReasonText);
    }

    [Fact]
    public void An_allowed_card_shows_no_reason_and_the_line_follows_the_language()
    {
        var (card, _) = Card("de");
        card.DenyReason = "Nein.";
        card.AllowCommand.Execute(null);
        Assert.False(card.IsReasonShown);

        var (denied, _) = Card("de");
        denied.DenyReason = "Falsche Seite";
        denied.DenyCommand.Execute(null);
        Assert.Equal("Ihre Begründung: Falsche Seite", denied.DeclinedReasonText);
    }
}
