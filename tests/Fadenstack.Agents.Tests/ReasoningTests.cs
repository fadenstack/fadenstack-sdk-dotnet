using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Auth;
using Fadenstack.Client.Tests;
using Fadenstack.Storage;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>How long the model thinks: the levels an agent offers, the person's pick, and what a request carries.</summary>
[Collection(PathsCollection.Name)]
public class ReasoningTests : IDisposable
{
    private const string Principal = """{"tenant_id":"t-1","user_id":"u-1","agent_id":"a-1","device_id":null}""";
    private const string OtherPrincipal = """{"tenant_id":"t-1","user_id":"u-2","agent_id":"a-1","device_id":null}""";
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "faden-reasoning-" + Guid.NewGuid());

    public ReasoningTests()
    {
        FadenstackPaths.RootForTests = _temporary;
        SecretProtection.ResetForTests();
        SecretProtection.Use(new FakeEncryptingProtector());
    }

    public void Dispose()
    {
        SecretProtection.ResetForTests();
        FadenstackPaths.RootForTests = null;
        if (Directory.Exists(_temporary))
        {
            Directory.Delete(_temporary, true);
        }
    }

    private static string Ui(string reasoning) => "{\"reasoning\":" + reasoning + "}";

    private static AgentBuilder Plain(Gateway gateway) => FadenstackAgent.CreateBuilder()
        .UseServer(new Uri("https://faden.test"), new StaticTokenProvider("agent-token"), o => o.HttpClient = new HttpClient(gateway.Server))
        .UseAgent(Gateway.Slug);

    private static IReadOnlyDictionary<string, JsonElement> Parsed(string ui) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ui)!;

    // -- the levels and the server's maximum --------------------------------------------------------

    private static readonly ReasoningLevel[] Ladder = { ReasoningLevel.Auto, ReasoningLevel.Off, ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High };

    [Fact]
    public void Without_a_setting_all_five_levels_are_offered_and_there_is_no_maximum()
    {
        var choices = ReasoningChoices.FromUi(null);
        Assert.Equal(Ladder, choices.Levels);
        Assert.Null(choices.Max);
    }

    [Theory]
    [InlineData("none", new[] { ReasoningLevel.Auto, ReasoningLevel.Off })]
    [InlineData("low", new[] { ReasoningLevel.Auto, ReasoningLevel.Off, ReasoningLevel.Low })]
    [InlineData("medium", new[] { ReasoningLevel.Auto, ReasoningLevel.Off, ReasoningLevel.Low, ReasoningLevel.Medium })]
    [InlineData("high", new[] { ReasoningLevel.Auto, ReasoningLevel.Off, ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High })]
    [InlineData("xhigh", new[] { ReasoningLevel.Auto, ReasoningLevel.Off, ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High })]
    public void Levels_above_the_maximum_are_hidden_and_off_stays(string max, ReasoningLevel[] expected)
    {
        var choices = ReasoningChoices.FromUi(Parsed(Ui("{\"max\":\"" + max + "\"}")));
        Assert.Equal(expected, choices.Levels);
        Assert.NotNull(choices.Max);
    }

    [Theory]
    [InlineData("""{"ui":null}""")]
    [InlineData("""{"reasoning":"high"}""")]
    [InlineData("""{"reasoning":[1,2]}""")]
    [InlineData("""{"reasoning":{"max":7}}""")]
    [InlineData("""{"reasoning":{"max":"extreme"}}""")]
    [InlineData("""{"reasoning":{"max":null}}""")]
    [InlineData("""{"reasoning":{"levels":["none"],"default":"high"}}""")]
    public void Anything_unreadable_is_ignored_and_the_old_levels_and_default_do_nothing(string ui)
    {
        var choices = ReasoningChoices.FromUi(Parsed(ui));
        Assert.Equal(Ladder, choices.Levels);
        Assert.Null(choices.Max);
    }

    [Fact]
    public async Task The_agent_reads_the_maximum_from_its_profile_ui()
    {
        using var agent = new Gateway(ui: Ui("""{"max":"low"}""")).Builder().Build();
        await agent.StartAsync();
        Assert.Equal(ReasoningEffort.Low, agent.ReasoningChoices.Max);
        Assert.DoesNotContain(ReasoningLevel.High, agent.ReasoningChoices.Levels);
    }

    // -- precedence ----------------------------------------------------------------------------------

    [Fact]
    public async Task With_nothing_set_the_level_is_auto()
    {
        using var agent = new Gateway().Builder().Build();
        await agent.StartAsync();
        Assert.Equal(ReasoningLevel.Auto, agent.Reasoning);
        Assert.Null(agent.ReasoningChoice);
    }

    [Fact]
    public async Task The_apps_setting_beats_auto()
    {
        using var agent = new Gateway().Builder().UseReasoning(ReasoningEffort.Medium).Build();
        await agent.StartAsync();
        Assert.Equal(ReasoningLevel.Medium, agent.Reasoning);
    }

    [Fact]
    public async Task The_persons_choice_beats_the_apps_setting()
    {
        using var agent = new Gateway().Builder().UseReasoning(ReasoningEffort.High).Build();
        Assert.Equal(ReasoningLevel.Low, await agent.SetReasoningAsync(ReasoningLevel.Low));
        Assert.Equal(ReasoningLevel.Low, agent.ReasoningChoice);
        Assert.Equal(ReasoningLevel.Auto, await agent.SetReasoningAsync(ReasoningLevel.Auto));
    }

    [Fact]
    public async Task A_choice_above_the_maximum_is_not_taken()
    {
        using var agent = new Gateway(ui: Ui("""{"max":"low"}""")).Builder().Build();
        Assert.Equal(ReasoningLevel.Auto, await agent.SetReasoningAsync(ReasoningLevel.High));
        Assert.Null(agent.ReasoningChoice);
    }

    [Fact]
    public async Task The_apps_setting_is_lowered_to_the_maximum()
    {
        using var agent = new Gateway(ui: Ui("""{"max":"low"}""")).Builder().UseReasoning(ReasoningEffort.High).Build();
        await agent.StartAsync();
        Assert.Equal(ReasoningLevel.Low, agent.Reasoning);
    }

    [Fact]
    public async Task Auto_means_the_maximum_when_there_is_one()
    {
        var gateway = new Gateway(ui: Ui("""{"max":"medium"}""")).Says("Hello.");
        using var agent = gateway.Builder().Build();
        var session = await agent.StartSessionAsync();
        Assert.Equal(ReasoningLevel.Auto, session.Reasoning);
        await session.SendAsync("Hi");
        Assert.Equal("medium", gateway.ChatRequests.Single().Json.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task A_stored_choice_above_a_lowered_maximum_shows_and_sends_the_maximum()
    {
        using (var first = Plain(new Gateway { Principal = Principal }).Build())
        {
            await first.SetReasoningAsync(ReasoningLevel.High);
        }
        var gateway = new Gateway(ui: Ui("""{"max":"low"}""")) { Principal = Principal }.Says("Hello.");
        using var later = Plain(gateway).Build();
        await later.StartAsync();
        Assert.Equal(ReasoningLevel.High, later.ReasoningChoice); // kept as it was picked
        Assert.Equal(ReasoningLevel.Low, later.Reasoning);
        var session = await later.StartSessionAsync();
        Assert.Equal(ReasoningLevel.Low, session.Reasoning);
        await session.SendAsync("Hi");
        Assert.Equal("low", gateway.ChatRequests.Single().Json.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Off_always_goes_out_as_none_even_with_a_maximum()
    {
        var gateway = new Gateway(ui: Ui("""{"max":"low"}""")).Says("Hello.");
        using var agent = gateway.Builder().Build();
        await agent.SetReasoningAsync(ReasoningLevel.Off);
        var session = await agent.StartSessionAsync();
        await session.SendAsync("Hi");
        Assert.Equal("none", gateway.ChatRequests.Single().Json.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Forgetting_the_choice_goes_back_to_the_apps_setting()
    {
        using var agent = new Gateway().Builder().UseReasoning(ReasoningEffort.Medium).Build();
        await agent.SetReasoningAsync(ReasoningLevel.High);
        Assert.Equal(ReasoningLevel.Medium, await agent.SetReasoningAsync(null));
    }

    // -- what a request carries ----------------------------------------------------------------------

    [Fact]
    public async Task Auto_sends_no_field()
    {
        var gateway = new Gateway().Says("Hello.");
        using var agent = gateway.Builder().Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("Hi");
        Assert.False(gateway.ChatRequests.Single().Json.TryGetProperty("reasoning_effort", out _));
    }

    [Theory]
    [InlineData(ReasoningLevel.Off, "none")]
    [InlineData(ReasoningLevel.Low, "low")]
    [InlineData(ReasoningLevel.Medium, "medium")]
    [InlineData(ReasoningLevel.High, "high")]
    public async Task Each_level_has_its_wire_value(ReasoningLevel level, string wire)
    {
        var gateway = new Gateway().Says("Hello.");
        using var agent = gateway.Builder().Build();
        var session = await agent.StartSessionAsync();
        session.Reasoning = level;
        await session.SendAsync("Hi");
        Assert.Equal(wire, gateway.ChatRequests.Single().Json.GetProperty("reasoning_effort").GetString());
        Assert.Equal(wire, level.ToWire());
    }

    [Fact]
    public async Task The_apps_extra_high_still_goes_out_as_it_was_set()
    {
        var gateway = new Gateway().Says("Hello.");
        using var agent = gateway.Builder().UseReasoning(ReasoningEffort.ExtraHigh).Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("Hi");
        Assert.Equal("xhigh", gateway.ChatRequests.Single().Json.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task A_new_session_starts_from_the_persons_choice_and_a_change_applies_from_the_next_message()
    {
        var gateway = new Gateway().Says("One.").Says("Two.");
        using var agent = gateway.Builder().Build();
        await agent.SetReasoningAsync(ReasoningLevel.High);
        var session = await agent.StartSessionAsync();
        Assert.Equal(ReasoningLevel.High, session.Reasoning);
        await session.SendAsync("First");
        await agent.SetReasoningAsync(ReasoningLevel.Off);
        session.Reasoning = ReasoningLevel.Off;
        await session.SendAsync("Second");
        Assert.Equal(new[] { "high", "none" }, gateway.ChatRequests.Select(r => r.Json.GetProperty("reasoning_effort").GetString()));
    }

    [Fact]
    public async Task A_session_that_follows_the_agent_picks_up_a_change_at_its_next_message()
    {
        var gateway = new Gateway().Says("One.").Says("Two.");
        using var agent = gateway.Builder().Build();
        var session = await agent.StartSessionAsync();
        await session.SendAsync("First");
        await agent.SetReasoningAsync(ReasoningLevel.Low);
        await session.SendAsync("Second");
        Assert.False(gateway.ChatRequests[0].Json.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("low", gateway.ChatRequests[1].Json.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task The_turn_start_entry_records_the_effort_and_no_client_event_carries_it()
    {
        var gateway = new Gateway().Says("Hello.");
        using var agent = gateway.Builder().Build();
        var session = await agent.StartSessionAsync();
        session.Reasoning = ReasoningLevel.Medium;
        await session.SendAsync("Hi");
        await agent.FlushAsync();
        var start = session.Transcript.Single(e => e.Type == EntryTypes.TurnStart);
        Assert.Equal("medium", start.Data.GetProperty("reasoning_effort").GetString());
        Assert.DoesNotContain("reasoning", string.Concat(gateway.Reports.Select(r => r.Body)));
    }

    // -- kept per person and per agent --------------------------------------------------------------

    [Fact]
    public async Task The_choice_survives_a_new_agent_instance()
    {
        using (var first = Plain(new Gateway { Principal = Principal }).Build())
        {
            await first.SetReasoningAsync(ReasoningLevel.High);
        }
        using var second = Plain(new Gateway { Principal = Principal }).Build();
        await second.StartAsync();
        Assert.Equal(ReasoningLevel.High, second.ReasoningChoice);
        Assert.True(File.Exists(Path.Combine(_temporary, "data", "users", "u-1", "preferences", "reasoning.json")));
        Assert.Equal(ReasoningLevel.High, (await second.StartSessionAsync()).Reasoning);
    }

    [Fact]
    public async Task Another_person_on_the_device_does_not_get_it()
    {
        using (var first = Plain(new Gateway { Principal = Principal }).Build())
        {
            await first.SetReasoningAsync(ReasoningLevel.High);
        }
        using var other = Plain(new Gateway { Principal = OtherPrincipal }).Build();
        await other.StartAsync();
        Assert.Null(other.ReasoningChoice);
        Assert.Equal(ReasoningLevel.Auto, other.Reasoning);
    }

    [Fact]
    public async Task Each_agent_keeps_its_own_choice()
    {
        var file = Path.Combine(_temporary, "data", "users", "u-1", "preferences", "reasoning.json");
        using (var first = Plain(new Gateway { Principal = Principal }).Build())
        {
            await first.SetReasoningAsync(ReasoningLevel.High);
        }
        // Another agent's entry in the same file is not touched, nor read for this one.
        File.WriteAllText(file, File.ReadAllText(file).TrimEnd('}') + ",\"other-agent\":\"none\"}");
        using var again = Plain(new Gateway { Principal = Principal }).Build();
        await again.StartAsync();
        Assert.Equal(ReasoningLevel.High, again.ReasoningChoice);
        await again.SetReasoningAsync(ReasoningLevel.Low);
        var kept = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
        Assert.Equal("none", kept["other-agent"]);
        Assert.Equal("low", kept[Gateway.Slug]);
    }

    [Fact]
    public async Task Without_a_principal_it_is_kept_in_the_shared_folder()
    {
        using var agent = Plain(new Gateway()).Build();
        await agent.SetReasoningAsync(ReasoningLevel.Low);
        Assert.True(File.Exists(Path.Combine(_temporary, "data", "preferences", "reasoning.json")));
    }

    [Fact]
    public async Task A_damaged_file_means_no_choice()
    {
        var folder = FadenstackPaths.User("u-1", "preferences");
        File.WriteAllText(Path.Combine(folder, "reasoning.json"), "{not json");
        using var agent = Plain(new Gateway { Principal = Principal }).Build();
        await agent.StartAsync();
        Assert.Null(agent.ReasoningChoice);
    }

    [Fact]
    public async Task Deleting_the_persons_data_takes_the_choice_along()
    {
        using var agent = Plain(new Gateway { Principal = Principal }).Build();
        await agent.SetReasoningAsync(ReasoningLevel.High);
        await agent.DeleteMyDataAsync();
        Assert.False(File.Exists(Path.Combine(_temporary, "data", "users", "u-1", "preferences", "reasoning.json")));
    }
}
