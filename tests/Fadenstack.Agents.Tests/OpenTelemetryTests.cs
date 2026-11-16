using System.Diagnostics;
using Fadenstack.Tools;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// Opt-in OpenTelemetry (sdk/51 F07): the run, model and tool spans are seen by a listener; no content unless it is switched on; the
/// opt-in adds nothing when it is off. No exporter is involved: an <see cref="ActivityListener"/> stands in for the app's wiring.
/// </summary>
public class OpenTelemetryTests
{
    private const string Prompt = "MARBLE-PROMPT-71";
    private const string Arguments = "MARBLE-ARGUMENTS-72";
    private const string Result = "MARBLE-RESULT-73";
    private const string Answer = "MARBLE-ANSWER-74";

    private sealed class Tools
    {
        [AgentTool(ToolClass.Read)]
        public string ReadPage(int page, string note) => $"{Result} {note}";
    }

    /// <summary>What a listener saw: the source and name of each finished span, and every tag value and event as text.</summary>
    private sealed record Seen(string Source, string Name, IReadOnlyDictionary<string, string> Tags, string All);

    private static async Task<(List<Seen> Spans, AgentResult Result)> RunAsync(Action<AgentBuilder>? configure)
    {
        var spans = new List<Seen>();
        using var listener = new ActivityListener
        {
            // Every source of the SDK: the run, model and tool spans, and the client's HTTP spans.
            ShouldListenTo = source => source.Name.StartsWith("Fadenstack.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                var tags = a.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString() ?? "");
                var all = string.Join(" | ", tags.Select(t => $"{t.Key}={t.Value}")
                    .Concat(a.Events.SelectMany(e => new[] { e.Name }.Concat(e.Tags.Select(t => $"{t.Key}={t.Value}")))));
                lock (spans)
                {
                    spans.Add(new Seen(a.Source.Name, a.DisplayName, tags, all));
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var gateway = new Gateway().Calls(("call-1", "read_page", $$"""{"page":1,"note":"{{Arguments}}"}""")).Says(Answer);
        var builder = gateway.Builder().AddTools(new Tools());
        configure?.Invoke(builder);
        using var agent = builder.Build();
        var session = await agent.StartSessionAsync(ToolMode.Auto);
        var result = await session.SendAsync($"Please read {Prompt}");
        // Only this turn's spans: other tests may run an agent at the same time. The HTTP spans carry no conversation, so all
        // of them count: none may carry content, whoever made it.
        Seen[] all;
        lock (spans)
        {
            all = spans.ToArray();
        }
        var mine = all.Where(s => s.Tags.TryGetValue("gen_ai.conversation.id", out var c) && c == session.Id
            || s.Source.StartsWith("Fadenstack.Agents.", StringComparison.Ordinal) || s.Source == FadenstackTelemetry.ClientSource).ToList();
        return (mine, result);
    }

    [Fact]
    public async Task Without_the_opt_in_the_model_source_gives_nothing_and_no_span_carries_content_or_the_opt_in_attributes()
    {
        var (spans, result) = await RunAsync(null);

        Assert.Equal(Answer, result.Text);
        Assert.DoesNotContain(spans, s => s.Source == AgentOpenTelemetryOptions.DefaultModelSourceName);
        Assert.DoesNotContain(spans, s => s.Tags.ContainsKey("fadenstack.tool.policy_decision") || s.Tags.ContainsKey("gen_ai.tool.type"));
        Assert.DoesNotContain(spans, s => s.All.Contains("MARBLE"));
        Assert.Contains(spans, s => s.Name.StartsWith("invoke_agent")); // the SDK's own spans, as before
        Assert.Contains(spans, s => s.Source == FadenstackTelemetry.ClientSource && s.Tags["http.route"] == Gateway.Chat); // the route, never a query or an id
    }

    [Fact]
    public async Task With_the_opt_in_the_run_model_and_tool_spans_are_seen_and_carry_no_content()
    {
        var (spans, result) = await RunAsync(b => b.UseOpenTelemetry());

        Assert.Equal(Answer, result.Text);
        var run = Assert.Single(spans, s => s.Name.StartsWith("invoke_agent"));
        Assert.Equal(("Fadenstack.Agents", "invoke_agent", Gateway.Slug), (run.Source, run.Tags["gen_ai.operation.name"], run.Tags["gen_ai.agent.name"]));
        var tool = Assert.Single(spans, s => s.Name.StartsWith("execute_tool"));
        Assert.Equal(("execute_tool", "read_page", "call-1", "function", "runs"),
            (tool.Tags["gen_ai.operation.name"], tool.Tags["gen_ai.tool.name"], tool.Tags["gen_ai.tool.call.id"], tool.Tags["gen_ai.tool.type"],
            tool.Tags["fadenstack.tool.policy_decision"]));
        Assert.Equal(2, spans.Count(s => s.Source == AgentOpenTelemetryOptions.DefaultModelSourceName && s.Tags.TryGetValue("gen_ai.operation.name", out var op) && op == "chat"));
        Assert.DoesNotContain(spans, s => s.All.Contains("MARBLE")); // run, model, tool and HTTP spans (F07, owner 16 Nov)
        Assert.DoesNotContain(spans, s => s.Tags.ContainsKey("gen_ai.tool.call.arguments") || s.Tags.ContainsKey("gen_ai.tool.call.result"));
        Assert.Contains(spans, s => s.Source == FadenstackTelemetry.ClientSource);
    }

    [Fact]
    public async Task The_sensitive_data_option_puts_the_prompt_the_arguments_and_the_host_tools_result_on_the_spans()
    {
        var (spans, _) = await RunAsync(b => b.UseOpenTelemetry(o => o.EnableSensitiveData = true));

        var tool = Assert.Single(spans, s => s.Name.StartsWith("execute_tool"));
        Assert.Contains(Arguments, tool.Tags["gen_ai.tool.call.arguments"]);
        Assert.Contains(Result, tool.Tags["gen_ai.tool.call.result"]);
        Assert.Contains(spans, s => s.Source == AgentOpenTelemetryOptions.DefaultModelSourceName && s.All.Contains(Prompt));
    }

    [Fact]
    public async Task The_model_source_name_can_be_chosen_and_the_hooks_of_the_app_run_beside_the_telemetry()
    {
        var log = new List<string>();
        var (spans, _) = await RunAsync(b => b.UseOpenTelemetry(o => o.ModelSourceName = "Fadenstack.Agents.Mine").AddHooks(new Marker(log)));

        Assert.Contains(spans, s => s.Source == "Fadenstack.Agents.Mine");
        Assert.DoesNotContain(spans, s => s.Source == AgentOpenTelemetryOptions.DefaultModelSourceName);
        Assert.Contains("tool", log);
    }

    private sealed class Marker : AgentHooks
    {
        private readonly List<string> _log;

        public Marker(List<string> log) => _log = log;

        public override Task AfterToolCallAsync(ToolCallResult result, CancellationToken cancellationToken)
        {
            _log.Add("tool");
            return Task.CompletedTask;
        }
    }
}
