using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.Endpoints;
using Fadenstack.Logging;
using Fadenstack.Storage;
using Fadenstack.UI.Parts;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// The "Report a problem" sheet in the chat (SDK 0.3.1): whom it writes to in each mode, what the mail and the copy say, the link on a failed
/// turn, the secrets that must never be in it, the masked profile path, and "Open the log folder".
/// </summary>
[Collection("File logger in the process")]
public class ReportProblemSheetTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 42, 0, TimeSpan.Zero);

    private const string Mail = """{"supportEmail":"it-help@example.org"}""";

    private static readonly string[] Languages = { "en", "de", "fr", "es", "zh-Hans" };

    private static AgentChatViewModel Governed(Gateway gateway, string? policy = null, string? host = null, NoClipboard? clipboard = null,
        Action<AgentBuilder>? build = null, string language = "en", IList<string>? languages = null)
    {
        var broker = new ApprovalBroker();
        var builder = gateway.Builder().UseApprovals(broker);
        if (policy is not null)
        {
            builder.UseMachinePolicy(MachinePolicy.FromJson(policy));
        }
        build?.Invoke(builder);
        var options = new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo(language),
            Clipboard = clipboard ?? new NoClipboard(), Clock = () => Now, Languages = languages,
        };
        if (host is not null)
        {
            options.SupportEmail = host;
        }
        return new AgentChatViewModel(builder.Build(), broker, InlineDispatcher.Instance, options);
    }

    private const string Definition = """
        {"slug":"local-assistant","name":"Local","version":{"instructions":"Hi.","host_tool_contract":[],"ui":{}}}
        """;

    private static async Task<AgentChatViewModel> Standalone(string? policy = null, string? host = null, NoClipboard? clipboard = null,
        EndpointDialect? dialect = null, string baseUrl = "http://localhost:11434/v1")
    {
        var broker = new ApprovalBroker();
        var builder = FadenstackAgent.CreateBuilder()
            .UseEndpoint(new ModelEndpoint
            {
                BaseUrl = new Uri(baseUrl), Model = "llama3.1:8b", Dialect = dialect ?? EndpointDialect.Ollama, Handler = new OpenAiFake(),
            })
            .UseLocalProfile(AgentDefinition.FromJson(Definition))
            .UseSessionStore(new MemorySessionStore())
            .UseApprovalStore(new MemoryApprovalStore())
            .UseApprovals(broker);
        if (policy is not null)
        {
            builder.UseMachinePolicy(MachinePolicy.FromJson(policy));
        }
        var options = new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, AutomaticLanguage = new CultureInfo("en"), Preferences = new MemoryPreferenceStore(),
            Clipboard = clipboard ?? new NoClipboard(), Clock = () => Now,
        };
        if (host is not null)
        {
            options.SupportEmail = host;
        }
        var chat = new AgentChatViewModel(builder.Build(), broker, InlineDispatcher.Instance, options);
        await chat.InitializeAsync();
        return chat;
    }

    private static async Task<AgentChatViewModel> GovernedStarted(Gateway? gateway = null, string? policy = null, string? host = null,
        NoClipboard? clipboard = null, Action<AgentBuilder>? build = null, string language = "en")
    {
        var chat = Governed(gateway ?? new Gateway(), policy, host, clipboard, build, language);
        await chat.InitializeAsync();
        return chat;
    }

    private static string[] Lines(string text) => text.Split('\n');

    // ---- governed -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Governed_with_the_policys_key_writes_to_that_address_and_Write_mail_hands_the_link_to_the_hook()
    {
        var chat = await GovernedStarted(policy: """{"supportEmail":"it-help@example.org"}""", host: "host@example.org");
        var links = new List<Uri>();
        chat.LinkRequested += (_, url) => links.Add(url);

        chat.OpenReportCommand.Execute(null);

        var report = chat.Report;
        Assert.True(report.IsOpen);
        Assert.Same(report, report.OpenSheet);
        Assert.Equal(("it-help@example.org", "This goes to it-help@example.org.", ""), (report.Address, report.GoesTo, report.AskIt));
        Assert.Equal("Report a problem", report.Title);
        Assert.Equal("This report contains:", report.Contains);
        Assert.Equal("Nothing from your chats is included.", report.NoChats);
        Assert.Null(report.HelpLink);
        Assert.True(report.WriteMailCommand.CanExecute(null));
        report.WriteMailCommand.Execute(null);

        var link = Assert.Single(links);
        Assert.Equal(report.Mailto, link.OriginalString);
        Assert.StartsWith("mailto:it-help@example.org?subject=Problem%20with%20Fadenstack%20SDK%20", link.OriginalString);
        Assert.StartsWith("To: it-help@example.org\nSubject: Problem with Fadenstack SDK ", report.CopyText);
        Assert.Contains("Mode: Organisation", report.Preview);
        Assert.Contains("Server: https://faden.test", report.Preview);
        Assert.Contains("Agent: Docs, Version 3, Model: qwen3-32b", report.Preview);
        Assert.Contains("Time: 2026-11-19 10:42 UTC", report.Preview);
        Assert.DoesNotContain("Error:", report.Preview);
        Assert.DoesNotContain("host@example.org", report.CopyText);
    }

    [Fact]
    public async Task Governed_without_the_key_offers_no_mail_the_help_link_and_a_copy_without_a_To_line()
    {
        var clipboard = new NoClipboard();
        var chat = await GovernedStarted(host: "host@example.org", clipboard: clipboard);
        var links = new List<Uri>();
        chat.LinkRequested += (_, url) => links.Add(url);

        chat.OpenReportCommand.Execute(null);

        var report = chat.Report;
        Assert.False(report.HasAddress);
        Assert.Null(report.Address);
        Assert.Null(report.Mailto);
        Assert.Equal("", report.GoesTo);
        Assert.Equal("Ask your organisation's IT for help. You can copy the details below for them.", report.AskIt);
        Assert.False(report.WriteMailCommand.CanExecute(null));
        report.WriteMailCommand.Execute(null);
        Assert.Empty(links);
        Assert.Equal("https://faden.test/help/", report.HelpLink!.Url.AbsoluteUri);
        Assert.True(report.HasHelp);
        // neither the host's address nor ours: the organisation's, or nobody's
        Assert.StartsWith("Subject: Problem with Fadenstack SDK ", report.CopyText);
        Assert.DoesNotContain("To:", report.CopyText);
        Assert.DoesNotContain("host@example.org", report.CopyText + report.Preview);
        Assert.DoesNotContain("support@fadenstack.com", report.CopyText + report.Preview);

        report.CopyCommand.Execute(null);
        Assert.StartsWith("Subject: ", clipboard.LastText);
        Assert.Equal("Copied", report.CopyStatus);
    }

    [Theory]
    [InlineData("""{"supportEmail":"a@b.example?bcc=x@y.example"}""")]
    [InlineData("""{"supportEmail":"a@b.example,c@d.example"}""")]
    [InlineData("""{"supportEmail":"a b@c.example"}""")]
    [InlineData("""{"supportEmail":"a%40b.example"}""")]
    [InlineData("""{"supportEmail":42}""")]
    [InlineData("""{"supportEmail":"https://helpdesk.example/new"}""")]
    public async Task A_policy_value_that_is_not_one_plain_address_counts_as_absent_and_is_reported_without_the_value(string json)
    {
        var policy = MachinePolicy.FromJson(json);
        Assert.Null(policy.SupportEmail);
        Assert.Contains("supportEmail", policy.Locked);
        var warning = Assert.Single(policy.Warnings);
        Assert.Contains("'supportEmail'", warning);
        Assert.DoesNotContain("example", warning);

        var chat = await GovernedStarted(policy: json);
        chat.OpenReportCommand.Execute(null);

        Assert.False(chat.Report.HasAddress);
        Assert.Null(chat.Report.Mailto);
    }

    // ---- standalone ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Standalone_uses_the_hosts_address_and_names_the_dialect_and_where_it_points_but_never_the_address()
    {
        var chat = await Standalone(host: "help@host.example");

        chat.OpenReportCommand.Execute(null);
        await chat.Report.PlaceResolved;

        var report = chat.Report;
        Assert.Equal("help@host.example", report.Address);
        Assert.Contains("Mode: Own AI", report.Preview);
        Assert.Contains("Endpoint: ollama, on this computer", report.Preview);
        Assert.DoesNotContain("Server:", report.Preview);
        Assert.DoesNotContain("11434", report.CopyText + report.Mailto);
        Assert.DoesNotContain("localhost", report.CopyText + report.Mailto);
        Assert.Contains("Agent: Local, Model: llama3.1:8b", report.Preview);
        Assert.StartsWith("To: help@host.example\n", report.CopyText);
    }

    [Fact]
    public async Task Standalone_without_a_host_address_writes_to_ours()
    {
        var chat = await Standalone();

        chat.OpenReportCommand.Execute(null);

        Assert.Equal("support@fadenstack.com", chat.Report.Address);
        Assert.StartsWith("mailto:support@fadenstack.com?subject=", chat.Report.Mailto);
        Assert.StartsWith("To: support@fadenstack.com\n", chat.Report.CopyText);
    }

    [Fact]
    public async Task The_policys_address_beats_the_hosts_in_standalone_too()
    {
        var chat = await Standalone(policy: """{"supportEmail":"it@example.org"}""", host: "help@host.example");

        chat.OpenReportCommand.Execute(null);

        Assert.Equal("it@example.org", chat.Report.Address);
    }

    [Fact]
    public async Task A_standalone_endpoint_in_the_local_network_or_on_the_internet_is_named_by_where_it_points_not_by_its_address()
    {
        var local = await Standalone(baseUrl: "http://192.168.1.40:8000/v1", dialect: EndpointDialect.Vllm);
        local.OpenReportCommand.Execute(null);
        await local.Report.PlaceResolved;
        Assert.Contains("Endpoint: vllm, in the local network", local.Report.Preview);
        Assert.DoesNotContain("192.168", local.Report.CopyText + local.Report.Mailto);

        var cloud = await Standalone(baseUrl: "https://8.8.8.8/v1", dialect: EndpointDialect.OpenAI);
        cloud.OpenReportCommand.Execute(null);
        await cloud.Report.PlaceResolved;
        Assert.Contains("Endpoint: openai, on the internet", cloud.Report.Preview);
        Assert.DoesNotContain("8.8.8.8", cloud.Report.CopyText + cloud.Report.Mailto);
    }

    [Theory]
    [InlineData("a@b.example?bcc=x@y.example")]
    [InlineData("a@b.example,c@d.example")]
    [InlineData("a b@c.example")]
    [InlineData("a@b.example\n")]
    [InlineData("a%40b.example")]
    public void A_host_option_that_is_not_one_plain_address_throws_when_it_is_set(string value)
    {
        var options = new AgentChatOptions { SupportEmail = "keep@example.org" };

        var error = Assert.Throws<ArgumentException>(() => options.SupportEmail = value);

        Assert.DoesNotContain(value.Trim(), error.Message);
        Assert.Equal("keep@example.org", options.SupportEmail); // it stays as it was
    }

    [Fact]
    public void The_host_option_takes_one_plain_address_or_none()
    {
        var options = new AgentChatOptions();
        Assert.Null(options.SupportEmail);
        options.SupportEmail = "help@host.example";
        Assert.Equal("help@host.example", options.SupportEmail);
        options.SupportEmail = null;
        Assert.Null(options.SupportEmail);
    }

    // ---- a failed turn ------------------------------------------------------------------------------------

    private static Func<Recorded, HttpResponseMessage> Refuse(string? code, string message = "raw server text") => _ => FakeServer.Respond(HttpStatusCode.BadRequest,
        new { error = new { message, type = "invalid_request_error", param = (string?)null, code } });

    [Fact]
    public async Task A_failed_turns_link_adds_the_errors_code_and_never_its_message()
    {
        var chat = await GovernedStarted(new Gateway().Then(Refuse("picture_type_not_allowed", "SECRET-ERROR-TEXT")), policy: Mail);
        await chat.SendAsync("Hi");
        var turn = Assert.Single(chat.Turns);

        Assert.True(turn.CanReport);
        Assert.Equal("picture_type_not_allowed", turn.ErrorCode);
        turn.ReportCommand.Execute(null);

        Assert.True(chat.Report.IsOpen);
        Assert.Contains("Error: picture_type_not_allowed", chat.Report.Preview);
        Assert.DoesNotContain("SECRET-ERROR-TEXT", chat.Report.Preview + chat.Report.CopyText + chat.Report.Mailto);
        Assert.EndsWith("Error: picture_type_not_allowed", chat.Report.CopyText);
        Assert.EndsWith("Error%3A%20picture_type_not_allowed", chat.Report.Mailto);
    }

    [Fact]
    public async Task An_error_without_a_code_says_its_http_status()
    {
        var chat = await GovernedStarted(new Gateway().Then(Refuse(null, "SECRET-ERROR-TEXT")), policy: Mail);
        await chat.SendAsync("Hi");

        chat.Turns[0].ReportCommand.Execute(null);

        Assert.Equal("http_400", chat.Turns[0].ErrorCode);
        Assert.Contains("Error: http_400", chat.Report.Preview);
        Assert.DoesNotContain("SECRET-ERROR-TEXT", chat.Report.CopyText);
    }

    [Fact]
    public async Task A_turn_that_did_not_fail_has_no_link_and_About_opens_the_sheet_without_an_error()
    {
        var chat = await GovernedStarted(new Gateway().Says("Fine."));
        await chat.SendAsync("Hi");

        Assert.False(chat.Turns[0].CanReport);
        Assert.False(chat.Turns[0].ReportCommand.CanExecute(null));
        chat.OpenReportCommand.Execute(null);
        Assert.DoesNotContain("Error:", chat.Report.Preview);
    }

    [Fact]
    public void The_error_code_is_the_servers_when_it_is_an_identifier_else_the_status_else_the_exception_type()
    {
        Assert.Equal("agent_disabled", AgentChatViewModel.ErrorCodeOf(new FadenstackApiException(HttpStatusCode.Forbidden, "m", "agent_disabled")));
        Assert.Equal("http_503", AgentChatViewModel.ErrorCodeOf(new FadenstackApiException(HttpStatusCode.ServiceUnavailable, "m")));
        Assert.Equal("http_400", AgentChatViewModel.ErrorCodeOf(new FadenstackApiException(HttpStatusCode.BadRequest, "m", "has spaces and\nnewline")));
        Assert.Equal("http_400", AgentChatViewModel.ErrorCodeOf(new FadenstackApiException(HttpStatusCode.BadRequest, "m", new string('x', 65))));
        Assert.Equal("code.with-dots_1:2", AgentChatViewModel.ErrorCodeOf(new FadenstackApiException(HttpStatusCode.BadRequest, "m", "code.with-dots_1:2")));
        Assert.Equal("rate_limited",
            AgentChatViewModel.ErrorCodeOf(new InvalidOperationException("outer", new FadenstackApiException(HttpStatusCode.TooManyRequests, "m", "rate_limited"))));
        Assert.Equal("FadenstackException", AgentChatViewModel.ErrorCodeOf(new FadenstackException("SECRET")));
        Assert.Null(AgentChatViewModel.ErrorCodeOf(new InvalidOperationException("SECRET")));
        Assert.Null(AgentChatViewModel.ErrorCodeOf(null));
    }

    // ---- what is never in it --------------------------------------------------------------------------------

    private static async Task<ApprovalPart> Pending(AgentChatViewModel chat)
    {
        for (var i = 0; i < 500; i++)
        {
            var card = chat.Turns.ToArray().SelectMany(t => t.Parts.ToArray()).OfType<ApprovalPart>().FirstOrDefault(p => p.AllowCommand.CanExecute(null));
            if (card is not null)
            {
                return card;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("No approval card.");
    }

    [Fact]
    public async Task A_seeded_prompt_answer_and_tool_argument_are_never_in_the_subject_the_body_the_link_or_the_copy()
    {
        var gateway = new Gateway().UseHostChecks().Says("Noted SECRET-ANSWER-789").Says("Seen SECRET-ANSWER-789.");
        var broker = new ApprovalBroker();
        var agent = gateway.Builder().AddTools(new PageTools()).UseApprovals(broker).UseMachinePolicy(MachinePolicy.FromJson(Mail)).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = new CultureInfo("en"), Clock = () => Now,
        });
        await chat.InitializeAsync();
        await chat.SendAsync("Please remember SECRET-PROMPT-123");
        var running = chat.InvokeToolAsync("add_comment", new Dictionary<string, object?> { ["page"] = 2, ["text"] = "SECRET-ARG-456" },
            new ChatCommandOptions { Label = "Comment on SECRET-LABEL-321" });
        (await Pending(chat)).AllowCommand.Execute(null);
        await running;
        // the chat holds all of it: the prompt, the answer, the tool's argument as the grant cards show it
        Assert.Contains("SECRET-PROMPT-123", chat.Turns[0].UserText);
        Assert.Contains("SECRET-ANSWER-789", chat.Turns[0].AnswerText);
        Assert.Contains("SECRET-ARG-456", gateway.Checks[0].Json.ToString());

        chat.OpenReportCommand.Execute(null);

        var report = chat.Report;
        var everything = string.Join("\n", report.Subject, report.Preview, report.CopyText, report.Mailto,
            Uri.UnescapeDataString(report.Mailto!), report.GoesTo, report.AskIt, report.Contains, report.NoChats);
        foreach (var secret in new[] { "SECRET-PROMPT-123", "SECRET-ANSWER-789", "SECRET-ARG-456", "SECRET-LABEL-321", "add_comment", "page 2" })
        {
            Assert.DoesNotContain(secret, everything);
        }
    }

    [Fact]
    public async Task The_system_line_has_no_machine_or_user_name_and_nothing_names_the_person()
    {
        var chat = await GovernedStarted();
        chat.OpenReportCommand.Execute(null);

        var everything = chat.Report.CopyText + chat.Report.Mailto;

        Assert.Contains("System: ", chat.Report.Preview);
        Assert.DoesNotContain(Environment.MachineName, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_copy_masks_the_profile_path()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(profile));
        var clipboard = new NoClipboard();
        var chat = await GovernedStarted(clipboard: clipboard,
            build: b => b.DescribeApp(new AppDescription { Name = "Tool " + Path.Combine(profile, "AppData", "tool.exe"), Version = "1.0" }));
        chat.OpenReportCommand.Execute(null);

        chat.Report.CopyCommand.Execute(null);

        var marker = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "%USERPROFILE%" : "~";
        Assert.DoesNotContain(profile, clipboard.LastText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tool " + marker, clipboard.LastText);
        Assert.StartsWith("Subject: Problem with Tool " + marker, clipboard.LastText);
    }

    // ---- the log folder -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Open_the_log_folder_is_offered_only_with_a_file_log_and_opens_the_folder_as_a_file_link()
    {
        var folder = Path.Combine(Path.GetTempPath(), "faden-report-" + Guid.NewGuid().ToString("N"));
        using var root = FadenstackPaths.UseRootForTests(folder);
        try
        {
            var chat = await GovernedStarted();
            var links = new List<Uri>();
            chat.LinkRequested += (_, url) => links.Add(url);
            chat.OpenReportCommand.Execute(null);

            Assert.False(chat.Report.CanOpenLogFolder);
            Assert.False(chat.Report.OpenLogFolderCommand.CanExecute(null));
            chat.Report.OpenLogFolderCommand.Execute(null);
            Assert.Empty(links);

            using (new FadenstackFileLoggerProvider("report-test"))
            {
                Assert.True(chat.Report.CanOpenLogFolder);
                chat.Report.OpenLogFolderCommand.Execute(null);

                var link = Assert.Single(links);
                Assert.True(link.IsFile);
                Assert.True(PanelLinkPolicy.OpensByDefault(link));
                Assert.Equal(Path.GetFullPath(FadenstackPaths.LogRoot).TrimEnd(Path.DirectorySeparatorChar),
                    link.LocalPath.TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase);
            }
            Assert.False(chat.Report.CanOpenLogFolder);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void The_default_link_hook_opens_web_mail_and_the_log_folder_only()
    {
        var folder = Path.Combine(Path.GetTempPath(), "faden-policy-" + Guid.NewGuid().ToString("N"));
        using var root = FadenstackPaths.UseRootForTests(folder);
        var logs = new Uri(Path.GetFullPath(FadenstackPaths.LogRoot) + Path.DirectorySeparatorChar);

        Assert.True(PanelLinkPolicy.OpensByDefault(new Uri("https://fadenstack.com/")));
        Assert.True(PanelLinkPolicy.OpensByDefault(new Uri("http://faden.test/help/")));
        Assert.True(PanelLinkPolicy.OpensByDefault(new Uri("mailto:a@b.example?subject=x")));
        Assert.True(PanelLinkPolicy.OpensByDefault(logs));
        Assert.False(PanelLinkPolicy.OpensByDefault(new Uri(Path.Combine(Path.GetFullPath(FadenstackPaths.LogRoot), "..", "other") + Path.DirectorySeparatorChar)));
        Assert.False(PanelLinkPolicy.OpensByDefault(new Uri(Path.Combine(Path.GetTempPath(), "program.exe"))));
        Assert.False(PanelLinkPolicy.OpensByDefault(new Uri("ftp://example.org/")));
        Assert.False(PanelLinkPolicy.OpensByDefault(new Uri("javascript:alert(1)")));
        Assert.False(PanelLinkPolicy.OpensByDefault(null));
    }

    [Fact]
    public void A_link_in_an_answer_opens_web_links_only()
    {
        Assert.True(PanelLinkPolicy.OpensFromAnswer(new Uri("https://fadenstack.com/")));
        Assert.True(PanelLinkPolicy.OpensFromAnswer(new Uri("http://faden.test/help/")));
        Assert.False(PanelLinkPolicy.OpensFromAnswer(new Uri("mailto:a@b.example?body=secret")));
        Assert.False(PanelLinkPolicy.OpensFromAnswer(new Uri("file:///C:/Windows")));
        Assert.False(PanelLinkPolicy.OpensFromAnswer(new Uri("ftp://example.org/")));
        Assert.False(PanelLinkPolicy.OpensFromAnswer(null));
    }

    // ---- the sheet's own behaviour --------------------------------------------------------------------------

    [Fact]
    public async Task Close_closes_the_sheet_and_About_stays_under_it()
    {
        var chat = await GovernedStarted();
        chat.OpenAboutCommand.Execute(null);

        chat.OpenReportCommand.Execute(null);
        Assert.True(chat.IsAboutOpen);
        Assert.True(chat.Report.IsOpen);

        chat.Report.CloseCommand.Execute(null);

        Assert.False(chat.Report.IsOpen);
        Assert.Null(chat.Report.OpenSheet);
        Assert.True(chat.IsAboutOpen);
    }

    [Fact]
    public async Task The_open_sheet_follows_a_change_of_language_and_the_copy_status_goes_by_itself()
    {
        var chat = await GovernedStarted(policy: """{"supportEmail":"it@example.org"}""");
        chat.OpenReportCommand.Execute(null);
        Assert.Contains("What happened:", chat.Report.Preview);

        chat.SetLanguageCommand.Execute("de");

        Assert.Equal("Ein Problem melden", chat.Report.Title);
        Assert.Equal("Das geht an it@example.org.", chat.Report.GoesTo);
        Assert.Contains("Was ist passiert:", chat.Report.Preview);
        Assert.Contains("Modus: Organisation", chat.Report.Preview);
        Assert.StartsWith("An: it@example.org\nBetreff: Problem mit Fadenstack SDK ", chat.Report.CopyText);
        chat.Report.CopyCommand.Execute(null);
        Assert.Equal(chat.Strings.Copied, chat.Report.CopyStatus);
    }

    [Fact]
    public async Task Opening_the_sheet_again_starts_without_the_last_error_and_copy_status()
    {
        var chat = await GovernedStarted(new Gateway().Then(Refuse("agent_disabled")), policy: Mail);
        await chat.SendAsync("Hi");
        chat.Turns[0].ReportCommand.Execute(null);
        chat.Report.CopyCommand.Execute(null);
        Assert.Equal("Copied", chat.Report.CopyStatus);
        chat.Report.CloseCommand.Execute(null);

        chat.OpenReportCommand.Execute(null);

        Assert.Equal("", chat.Report.CopyStatus);
        Assert.DoesNotContain("Error:", chat.Report.Preview);
    }

    // ---- the texts ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_five_languages_have_every_new_text_and_the_words_of_the_mail()
    {
        var names = typeof(UiStrings).GetProperties().Select(p => p.Name).Where(n => n.StartsWith("Report", StringComparison.Ordinal) && n != "ReportLabels").ToList();
        Assert.True(names.Count >= 25, string.Join(", ", names));
        var english = UiStrings.For(CultureInfo.GetCultureInfo("en"));
        foreach (var language in Languages)
        {
            var strings = UiStrings.For(CultureInfo.GetCultureInfo(language));
            Assert.Equal(language, strings.Language);
            foreach (var name in names)
            {
                var text = strings.Find(name);
                Assert.True(!string.IsNullOrWhiteSpace(text), $"{language}: no text for {name}");
                if (language != "en" && name is "ReportProblem" or "ReportWriteMail" or "ReportContains" or "ReportNoChats" or "ReportAskIt" or "ReportOpenLogFolder"
                    or "ReportPlaceThisComputer" or "ReportPlaceInternet")
                {
                    Assert.NotEqual(english.Find(name), text);
                }
            }
            Assert.Contains("{0}", strings.Find("ReportSubjectApp"));
            Assert.Contains("{0}", strings.Find("ReportSubjectNoApp"));
            Assert.Contains("support@fadenstack.com", strings.ReportGoesTo("support@fadenstack.com"));
            var labels = strings.ReportLabels();
            Assert.NotEqual("ReportLabelWhatHappened", labels.WhatHappened);
            Assert.Equal(strings.Copied, strings.Find("Copied"));
        }
    }
}
