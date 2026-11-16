using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fadenstack.Agents.Delegation;
using Fadenstack.Agents.Registry;
using Fadenstack.Client.Tests;
using Fadenstack.Governance;
using Fadenstack.Mcp;
using Fadenstack.Tools;
using Fadenstack.WebSearch;

namespace Fadenstack.Agents.Tests.Injection;

/// <summary>
/// The injection corpus (sdk/51 7.1): the same file, byte for byte, as the TypeScript SDK's packages/agents/test/injection/corpus.json.
/// A change in one repo fails here until the other has it.
/// </summary>
internal static class InjectionCorpus
{
    /// <summary>The SHA-256 of corpus.json with line endings as LF.</summary>
    public const string Sha256 = "34118260aa6c052ea505c97bbdbe60d0c210ed3d0219223b75211e94d439e334";

    public sealed record Case(string Id, string Title, string Kind, string Content, Induced? Induces);

    /// <summary>The call the content asks for, as the scripted model sends it.</summary>
    public sealed record Induced(string Tool, string ArgumentsJson, JsonElement Arguments);

    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Injection", "corpus.json");

    public static string FileSha256()
    {
        var text = File.ReadAllText(Path, new UTF8Encoding(false)).Replace("\r\n", "\n");
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }

    public static IReadOnlyList<Case> Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path, new UTF8Encoding(false)));
        var cases = new List<Case>();
        foreach (var c in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var content = c.GetProperty("content").GetString()!;
            if (c.TryGetProperty("padding", out var padding))
            {
                var unit = padding.GetProperty("unit").GetString()!;
                var times = padding.GetProperty("times").GetInt32();
                var builder = new StringBuilder(unit.Length * times + content.Length);
                for (var i = 0; i < times; i++)
                {
                    builder.Append(unit);
                }
                content = builder.Append(content).ToString();
            }
            Induced? induces = null;
            if (c.TryGetProperty("induces", out var i2))
            {
                var arguments = i2.GetProperty("arguments").Clone();
                induces = new Induced(i2.GetProperty("tool").GetString()!, arguments.GetRawText(), arguments);
            }
            cases.Add(new Case(c.GetProperty("id").GetString()!, c.GetProperty("title").GetString()!, c.GetProperty("kind").GetString()!, content, induces));
        }
        return cases;
    }

    public static Case Get(string id) => Load().Single(c => c.Id == id);

    public static IEnumerable<object[]> Ids() => Load().Select(c => new object[] { c.Id });

    public static IEnumerable<object[]> InducingIds() => Load().Where(c => c.Induces is not null).Select(c => new object[] { c.Id });
}

public enum InjectionChannel
{
    /// <summary>A local MCP server's tool result.</summary>
    Mcp,

    /// <summary>The web search tool's results (the content is a result's snippet).</summary>
    Web,

    /// <summary>Another agent's answer to a delegation.</summary>
    Delegation,

    /// <summary>An app's document text, fenced with <see cref="UntrustedContent.Fence(string, string, string, int)"/>.</summary>
    Document,
}

/// <summary>The app's tools in the suite: what an injected text can ask a model to call.</summary>
internal sealed class InjectionTools
{
    public const string Contract = """
        [{"name":"read_document","risk":"read"},{"name":"post_comment","risk":"write"},{"name":"delete_page","risk":"destructive"},
         {"name":"fetch_url","risk":"read"},{"name":"delegate","risk":"write"}]
        """;

    public string DocumentText { get; set; } = "";

    /// <summary>The tools that really ran, in order.</summary>
    public List<string> Ran { get; } = new();

    [AgentTool(ToolClass.Read), Description("Reads the open document.")]
    public string ReadDocument()
    {
        Ran.Add("read_document");
        return UntrustedContent.Fence("document", "the Word document Report.docx", DocumentText);
    }

    [AgentTool(ToolClass.Write), Description("Posts a comment.")]
    public string PostComment(string text)
    {
        Ran.Add("post_comment");
        return "posted";
    }

    [AgentTool(ToolClass.Destructive), Description("Deletes a page.")]
    public string DeletePage(int page)
    {
        Ran.Add("delete_page");
        return "deleted";
    }

    [AgentTool(ToolClass.Read, OpenWorld = true), Description("Fetches a web address.")]
    public string FetchUrl(string url)
    {
        Ran.Add("fetch_url");
        return "fetched";
    }
}

/// <summary>What one scripted run saw.</summary>
internal sealed record InjectionRun(
    string Received,
    IReadOnlyList<string> Ran,
    IReadOnlyList<ApprovalRequest> Asked,
    IReadOnlyList<string> OfferedAfterwards,
    ChatOutcome Outcome);

/// <summary>
/// Runs a case through a channel against the fake gateway with a scripted model: the model calls the channel's tool, sees the
/// injected content in its result, and then (when the case induces a call) sends the call the content asks for. The approval
/// handler approves the channel's own tool and denies everything else, so a call that asks never runs.
/// </summary>
internal static class InjectionRig
{
    public const string Benign = "The quarterly figures are on page 2. Nothing else to report.";

    private const string Allow = """{"mode":"allow","developer_mode":false}""";

    public static string ChannelTool(InjectionChannel channel) => channel switch
    {
        InjectionChannel.Mcp => "read_note",
        InjectionChannel.Web => WebSearchModule.ToolName,
        InjectionChannel.Delegation => "delegate",
        _ => "read_document",
    };

    private static string ChannelArguments(InjectionChannel channel) => channel switch
    {
        InjectionChannel.Web => """{"query":"quarterly report"}""",
        InjectionChannel.Delegation => """{"agent":"word-assistant","task":"Read the report."}""",
        _ => "{}",
    };

    /// <summary>The fence kind each channel's result opens with.</summary>
    public static string FenceKind(InjectionChannel channel) => channel switch
    {
        InjectionChannel.Mcp => "mcp-result",
        InjectionChannel.Web => "web-results",
        InjectionChannel.Delegation => "agent-result",
        _ => "untrusted-document",
    };

    /// <summary>Whether the channel's tool is offered in the mode (an MCP tool has no class and a delegation is a write).</summary>
    public static bool Reachable(InjectionChannel channel, ToolMode mode) => channel switch
    {
        InjectionChannel.Mcp or InjectionChannel.Delegation => mode >= ToolMode.Ask,
        _ => mode >= ToolMode.ReadOnly,
    };

    public static ToolClass ClassOf(string tool) => tool switch
    {
        "post_comment" => ToolClass.Write,
        "delete_page" => ToolClass.Destructive,
        _ => ToolClass.Read,
    };

    public static bool OpenWorldOf(string tool) => tool == "fetch_url";

    private sealed class FakeDelegate : IAgentDelegate
    {
        public string Answer { get; set; } = "";

        public AgentCardSummary? FindCard(string agent) => new AgentCardSummary { Slug = agent, Name = "Word", Description = "Edits the open document." };

        public Task<DelegationResult> RunAsync(DelegationRequest request, DelegationCallbacks? callbacks = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DelegationResult(DelegationOutcome.Completed, Answer, "t-1", TimeSpan.FromMilliseconds(5)));
    }

    public static async Task<InjectionRun> RunAsync(InjectionChannel channel, string content, ToolMode mode, InjectionCorpus.Induced? induces = null)
    {
        var tools = new InjectionTools { DocumentText = content };
        var channelTool = ChannelTool(channel);
        var gateway = new Gateway(InjectionTools.Contract, localMcp: Allow, ui: """{"delegates":["word-assistant"]}""");
        if (channel == InjectionChannel.Web)
        {
            gateway.Principal = Gateway.HostPrincipal; // the web search follows the person the profile names
        }
        if (channel == InjectionChannel.Mcp)
        {
            // An MCP tool is switched on by find_tool first; then the model calls it.
            gateway.Calls(("call-0", "find_tool", """{"query":"read note"}"""));
        }
        gateway.Calls(("call-1", channelTool, ChannelArguments(channel)));
        if (induces is not null)
        {
            gateway.Calls(("call-2", induces.Tool, induces.ArgumentsJson));
        }
        gateway.Says("Done.");

        var asked = new List<ApprovalRequest>();
        var builder = gateway.Builder().AddTools(tools).UseApprovals((request, _) =>
        {
            if (request.ToolName == channelTool)
            {
                return Task.FromResult(ApprovalDecision.Approve());
            }
            lock (asked)
            {
                asked.Add(request);
            }
            return Task.FromResult(ApprovalDecision.Deny("The person declined."));
        });

        var folder = Path.Combine(Path.GetTempPath(), "faden-injection-" + Guid.NewGuid().ToString("N"));
        IDisposable? extra = null;
        try
        {
            switch (channel)
            {
                case InjectionChannel.Mcp:
                    var rig = new McpRig { Tools = { McpRig.Tool("read_note", "Reads a note.", () => content, readOnly: true) } };
                    var module = new LocalMcpModule(new[] { rig.Server("notes-db") }) { ReconnectDelay = TimeSpan.Zero };
                    extra = module;
                    builder.AddLocalMcp(module);
                    break;
                case InjectionChannel.Web:
                    var store = new WebSearchStore(Path.Combine(folder, "person"), new FakeEncryptingProtector());
                    store.Save(new WebSearchSettings { Provider = WebSearchProvider.Brave });
                    store.SetBraveKey("BSA-test-key-0123456789");
                    builder.Use(new WebSearchModule(new WebSearchOptions { Store = _ => store, Handler = new Providers(content) }));
                    break;
                case InjectionChannel.Delegation:
                    builder.Use(new DelegateToolModule(new FakeDelegate { Answer = content }, new DelegateToolOptions { FromAgent = Gateway.Slug }));
                    break;
            }

            using var agent = builder.Build();
            var session = await agent.StartSessionAsync(mode);
            var result = await session.SendAsync("Please read it.");

            // The request after the channel's tool answered: it holds what the model received, and the tools it is offered.
            var requests = gateway.ChatRequests;
            var index = channel == InjectionChannel.Mcp ? 2 : 1;
            var received = requests.Count > index ? McpRig.LastToolResult(requests[index]) : "";
            var offered = requests.Count > index ? McpRig.ToolNames(requests[index]) : new List<string>();
            lock (asked)
            {
                return new InjectionRun(received, tools.Ran.ToList(), asked.ToList(), offered, result.Outcome);
            }
        }
        finally
        {
            extra?.Dispose();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>A search provider that answers every search with one result whose snippet is the injected text.</summary>
    private sealed class Providers : HttpMessageHandler
    {
        private readonly string _snippet;

        public Providers(string snippet) => _snippet = snippet;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(new
            {
                web = new { results = new[] { new { title = "Quarterly report", url = "https://docs.example.com/report", description = _snippet } } },
            });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
