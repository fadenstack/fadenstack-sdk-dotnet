using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Agents.Tests;
using Fadenstack.UI.Settings;
using Xunit;

namespace Fadenstack.UI.Tests.Settings;

/// <summary>The setting for old chats: the person's form, and what the organisation or the machine policy fixes.</summary>
public class ChatRetentionSettingsViewModelTests
{
    private const string Definition = """
        {"slug":"docs-assistant","name":"Docs assistant","version":{"instructions":"You are the docs assistant.","host_tool_contract":[],"ui":{}}}
        """;

    private static readonly UiStrings English = UiStrings.For(new System.Globalization.CultureInfo("en"));

    // Host stores: nothing is written to the person's folders; the choice lasts for the run.
    private static FadenstackAgent Standalone(MachinePolicy? policy = null)
    {
        var builder = FadenstackAgent.CreateBuilder().UseChatClient(new ScriptedChat())
            .UseLocalProfile(AgentDefinition.FromJson(Definition))
            .UseSessionStore(new MemorySessionStore()).UseApprovalStore(new MemoryApprovalStore());
        return (policy is null ? builder : builder.UseMachinePolicy(policy)).Build();
    }

    private static FadenstackAgent Governed(string? local = null) => new Gateway { LocalRetention = local }.Builder().Build();

    private static ChatRetentionSettingsViewModel View(FadenstackAgent agent) => new(agent, strings: () => English);

    [Fact]
    public async Task Standalone_shows_the_defaults_and_nothing_to_save()
    {
        using var agent = Standalone();
        var model = View(agent);
        await model.LoadAsync();

        Assert.True(model.AutoDelete);
        Assert.Equal(("90", "50"), (model.MaxAgeDaysText, model.MaxSessionsText));
        Assert.True(model.CanEdit && model.ShowControls && !model.ShowNote && model.LimitsEnabled);
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.False(model.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_change_can_be_saved_and_is_kept()
    {
        using var agent = Standalone();
        var model = View(agent);
        await model.LoadAsync();

        model.MaxAgeDaysText = "30";
        model.MaxSessionsText = " 20 ";
        Assert.True(model.IsDirty);
        Assert.True(model.SaveCommand.CanExecute(null));
        await model.SaveCommand.ExecuteAsync(null);

        Assert.Equal((true, 30, 20), (agent.ChatRetention.AutoDelete, agent.ChatRetention.MaxAgeDays, agent.ChatRetention.MaxSessions));
        Assert.Equal("Saved.", model.StatusText);
        Assert.False(model.IsDirty);
        model.MaxSessionsText = "21";
        Assert.Equal("", model.StatusText);
    }

    [Fact]
    public async Task Off_keeps_the_limits_and_saves_without_checking_them()
    {
        using var agent = Standalone();
        var model = View(agent);
        await model.LoadAsync();

        model.MaxAgeDaysText = "nonsense";
        Assert.True(model.HasDaysError);
        model.AutoDelete = false;
        Assert.False(model.LimitsEnabled);
        Assert.False(model.HasDaysError);
        Assert.True(model.SaveCommand.CanExecute(null));
        await model.SaveCommand.ExecuteAsync(null);

        Assert.Equal((false, 90, 50), (agent.ChatRetention.AutoDelete, agent.ChatRetention.MaxAgeDays, agent.ChatRetention.MaxSessions));
        Assert.Equal("90", model.MaxAgeDaysText);
    }

    [Theory]
    [InlineData("0", "50")]
    [InlineData("3651", "50")]
    [InlineData("-5", "50")]
    [InlineData("1.5", "50")]
    [InlineData("", "50")]
    [InlineData("ten", "50")]
    [InlineData("90", "0")]
    [InlineData("90", "10001")]
    public async Task Limits_that_are_not_whole_numbers_in_bounds_cannot_be_saved(string days, string chats)
    {
        using var agent = Standalone();
        var model = View(agent);
        await model.LoadAsync();

        model.MaxAgeDaysText = days;
        model.MaxSessionsText = chats;

        Assert.False(model.IsValid);
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.True(model.HasDaysError || model.HasChatsError);
        Assert.Contains(model.HasDaysError ? "days from 1 to 3650" : "chats from 1 to 10000", model.DaysError + model.ChatsError);
    }

    [Fact]
    public async Task Cancel_puts_back_what_is_kept()
    {
        using var agent = Standalone();
        var model = View(agent);
        await model.LoadAsync();
        model.AutoDelete = false;
        model.MaxAgeDaysText = "7";

        model.CancelCommand.Execute(null);

        Assert.True(model.AutoDelete);
        Assert.Equal("90", model.MaxAgeDaysText);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public async Task Governed_is_read_only_and_says_what_the_organisation_keeps()
    {
        using var agent = Governed("""{"max_sessions":30,"max_age_days":14}""");
        var model = View(agent);
        await model.LoadAsync();

        Assert.True(model.IsReadOnly && !model.CanEdit && !model.ShowControls && model.ShowNote);
        Assert.Equal("Your organisation keeps at most 30 chats for 14 days on this device.", model.Note);
        model.AutoDelete = false;
        model.MaxAgeDaysText = "5";
        Assert.True(model.AutoDelete);
        Assert.Equal("14", model.MaxAgeDaysText);
        Assert.False(model.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Governed_without_a_server_limit_states_the_limits_in_force()
    {
        using var agent = Governed();
        var model = View(agent);
        await model.LoadAsync();
        Assert.Equal("This device keeps at most 50 chats for 90 days.", model.Note);

        var german = new ChatRetentionSettingsViewModel(agent, strings: () => UiStrings.For(new System.Globalization.CultureInfo("de")));
        await german.LoadAsync();
        Assert.Equal("Dieses Gerät behält höchstens 50 Chats für 90 Tage.", german.Note);
    }

    [Fact]
    public async Task Governed_shows_the_stricter_of_the_servers_and_the_apps_limits()
    {
        // The server is stricter on chats (30 < 50), the app on days (20 < 90): the card shows what applies, not the server's raw values.
        using var agent = new Gateway { LocalRetention = """{"max_sessions":30,"max_age_days":200}""" }.Builder()
            .UseSessionRetention(new SessionRetention(MaxSessions: 50, MaxAgeDays: 20)).Build();
        var model = View(agent);
        await model.LoadAsync();
        Assert.Equal("Your organisation keeps at most 30 chats for 20 days on this device.", model.Note);

        var german = new ChatRetentionSettingsViewModel(agent, strings: () => UiStrings.For(new System.Globalization.CultureInfo("de")));
        await german.LoadAsync();
        Assert.Equal("Ihre Organisation behält auf diesem Gerät höchstens 30 Chats für 20 Tage.", german.Note);
    }

    [Fact]
    public async Task The_machine_policy_fixes_it_and_the_values_are_shown()
    {
        using var agent = Standalone(MachinePolicy.FromJson("""{"chatRetention":{"autoDelete":true,"maxAgeDays":30,"maxSessions":100}}"""));
        var model = View(agent);
        await model.LoadAsync();

        Assert.True(model.IsReadOnly && model.ShowControls && model.ShowNote);
        Assert.Equal("Set by your organisation's policy on this computer.", model.Note);
        Assert.Equal((true, "30", "100"), (model.AutoDelete, model.MaxAgeDaysText, model.MaxSessionsText));
        Assert.False(model.LimitsEnabled);
        model.AutoDelete = false;
        model.MaxSessionsText = "5";
        Assert.Equal((true, "100"), (model.AutoDelete, model.MaxSessionsText));
        Assert.False(model.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Retext_says_the_texts_again_in_the_new_language()
    {
        using var agent = Governed("""{"max_sessions":30,"max_age_days":14}""");
        var language = English;
        var model = new ChatRetentionSettingsViewModel(agent, strings: () => language);
        await model.LoadAsync();
        var told = new List<string?>();
        model.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        language = UiStrings.For(new System.Globalization.CultureInfo("de"));
        model.Retext();

        Assert.Contains(nameof(model.Strings), told);
        Assert.Contains(nameof(model.Note), told);
        Assert.Equal("Ihre Organisation behält auf diesem Gerät höchstens 30 Chats für 14 Tage.", model.Note);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void Every_text_of_the_setting_is_translated(string language)
    {
        var translated = UiStrings.For(new System.Globalization.CultureInfo(language));
        foreach (var text in new[]
                 {
                     translated.ChatRetentionTitle, translated.ChatRetentionAutoDelete, translated.ChatRetentionAutoDeleteHint, translated.ChatRetentionMaxAge,
                     translated.ChatRetentionMaxSessions, translated.ChatRetentionMaxAgeName, translated.ChatRetentionMaxSessionsName, translated.ChatRetentionSave,
                     translated.ChatRetentionCancel, translated.ChatRetentionSaveName, translated.ChatRetentionCancelName, translated.ChatRetentionSaved,
                     translated.ChatRetentionDeviceLimit(50, 90), translated.ChatRetentionPolicyFixed,
                     translated.ChatRetentionOrganisationLimit(30, 14), translated.ChatRetentionDaysError(1, 3650), translated.ChatRetentionChatsError(1, 10000),
                 })
        {
            Assert.NotEqual("", text);
            Assert.DoesNotContain("{", text);
            Assert.DoesNotContain("ChatRetention", text);
        }
        Assert.NotEqual(English.ChatRetentionAutoDelete, translated.ChatRetentionAutoDelete);
        Assert.NotEqual(English.ChatRetentionPolicyFixed, translated.ChatRetentionPolicyFixed);
    }
}
