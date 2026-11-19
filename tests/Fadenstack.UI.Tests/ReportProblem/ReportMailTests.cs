using System.Globalization;
using System.Text.Json;
using Fadenstack.Agents;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// The "Report a problem" mail as a pure function (SDK 0.3.1): the shared table <c>ReportProblem/cases.json</c> that the TypeScript SDK holds
/// too (its expected values come from an independent implementation of the spec, not from this builder), the address rules, the encoding,
/// and the words of the mail in the resx.
/// </summary>
public class ReportMailTests
{
    private static readonly JsonElement Table = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ReportProblem", "cases.json"))).RootElement;

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static TheoryData<string> CaseNames()
    {
        var names = new TheoryData<string>();
        foreach (var c in Table.GetProperty("cases").EnumerateArray())
        {
            names.Add(c.GetProperty("name").GetString()!);
        }
        return names;
    }

    private static JsonElement CaseNamed(string name) =>
        Table.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ReportFacts FactsOf(JsonElement f) => new()
    {
        App = Str(f, "app"),
        SdkVersion = Str(f, "sdkVersion")!,
        Sdk = Str(f, "sdk")!,
        System = Str(f, "system"),
        Mode = Str(f, "mode"),
        Agent = Str(f, "agent"),
        Server = Str(f, "server"),
        Endpoint = Str(f, "endpoint"),
        Time = Str(f, "time")!,
        Error = f.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? new ReportedError(Str(e, "code"), Str(e, "requestId")) : null,
    };

    private static ReportLabels LabelsOf(string language) =>
        JsonSerializer.Deserialize<ReportLabels>(Table.GetProperty("labels").GetProperty(language).GetRawText(), Camel)!;

    private static ReportDraft BuildCase(JsonElement c) => ReportMailBuilder.Build(Str(c, "address"), LabelsOf(c.GetProperty("language").GetString()!),
        FactsOf(c.GetProperty("facts")),
        c.TryGetProperty("log", out var log) ? log.EnumerateArray().Select(l => l.GetString()!).ToList() : null);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_table_gives_the_same_subject_body_link_and_copy(string name)
    {
        var c = CaseNamed(name);
        var expected = c.GetProperty("expected");
        var draft = BuildCase(c);

        Assert.Equal(expected.GetProperty("subject").GetString(), draft.Subject);
        Assert.Equal(expected.GetProperty("body").GetString(), draft.Body);
        Assert.Equal(expected.GetProperty("mailBody").GetString(), draft.MailBody);
        Assert.Equal(expected.GetProperty("dropped").GetInt32(), draft.Dropped);
        Assert.Equal(expected.GetProperty("mailto").ValueKind == JsonValueKind.Null ? null : expected.GetProperty("mailto").GetString(), draft.Mailto);
        Assert.Equal(expected.GetProperty("copy").GetString(), draft.CopyText);
    }

    [Fact]
    public void The_table_has_English_and_German_cases_and_the_limit_cases()
    {
        var languages = Table.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("language").GetString()).Distinct().OrderBy(l => l).ToArray();
        Assert.Equal(new[] { "de", "en" }, languages);
        Assert.Contains(Table.GetProperty("cases").EnumerateArray(), c => c.GetProperty("expected").GetProperty("dropped").GetInt32() > 0);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void The_words_of_the_mail_in_the_resx_are_the_tables_words(string language)
    {
        Assert.Equal(LabelsOf(language), UiStrings.For(CultureInfo.GetCultureInfo(language)).ReportLabels());
    }

    [Fact]
    public void The_modes_words_in_the_resx_are_the_ones_the_tables_cases_use()
    {
        var english = UiStrings.For(CultureInfo.GetCultureInfo("en"));
        var german = UiStrings.For(CultureInfo.GetCultureInfo("de"));
        var modes = Table.GetProperty("cases").EnumerateArray().Select(c => (c.GetProperty("language").GetString(), Str(c.GetProperty("facts"), "mode"))).ToHashSet();

        Assert.Contains(("en", english.ReportModeGoverned), modes);
        Assert.Contains(("en", english.ReportModeOwnAi), modes);
        Assert.Contains(("de", german.ReportModeGoverned), modes);
        Assert.Contains(("de", german.ReportModeOwnAi), modes);
        Assert.Equal(("Organisation", "Own AI"), (english.ReportModeGoverned, english.ReportModeOwnAi));
        Assert.Equal(("Organisation", "Eigene KI"), (german.ReportModeGoverned, german.ReportModeOwnAi));
        Assert.Equal(("Organisation", "IA personnelle"), (UiStrings.For(CultureInfo.GetCultureInfo("fr")).ReportModeGoverned, UiStrings.For(CultureInfo.GetCultureInfo("fr")).ReportModeOwnAi));
        Assert.Equal(("Organización", "IA propia"), (UiStrings.For(CultureInfo.GetCultureInfo("es")).ReportModeGoverned, UiStrings.For(CultureInfo.GetCultureInfo("es")).ReportModeOwnAi));
        Assert.Equal(("组织", "自有 AI"), (UiStrings.For(CultureInfo.GetCultureInfo("zh-Hans")).ReportModeGoverned, UiStrings.For(CultureInfo.GetCultureInfo("zh-Hans")).ReportModeOwnAi));
    }

    [Fact]
    public void No_link_is_longer_than_2000_characters_while_a_line_is_left_to_drop()
    {
        foreach (var c in Table.GetProperty("cases").EnumerateArray())
        {
            var draft = BuildCase(c);
            if (draft.Mailto is not null && draft.MailBody.Contains("-- "))
            {
                Assert.True(draft.Mailto.Length <= ReportMailBuilder.MaxLinkLength, c.GetProperty("name").GetString());
            }
        }
    }

    [Fact]
    public void A_value_is_one_line_runs_of_controls_are_one_space_and_a_lone_surrogate_is_the_replacement_character()
    {
        Assert.Equal("a b", ReportMailBuilder.OneLine("a\r\n\t b").Replace("  ", " "));
        Assert.Equal("a b", ReportMailBuilder.OneLine("a\r\n\u0001\u009fb"));
        Assert.Equal("a b", ReportMailBuilder.OneLine("a\u2028b"));
        Assert.Equal("a\ufffdb", ReportMailBuilder.OneLine("a\ud800b"));
        Assert.Equal("a\ufffdb", ReportMailBuilder.OneLine("a\udc00b"));
        Assert.Equal("a\U0001F600b", ReportMailBuilder.OneLine("a\U0001F600b"));
        Assert.Equal("x", ReportMailBuilder.OneLine("\n x \r\n"));
        Assert.Equal("", ReportMailBuilder.OneLine(null));
    }

    [Fact]
    public void Lines_the_limit_drops_go_from_the_end_and_the_copy_keeps_them_all()
    {
        var c = Table.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("expected").GetProperty("dropped").GetInt32() > 0);
        var draft = BuildCase(c);

        Assert.NotEqual(draft.Body, draft.MailBody);
        Assert.StartsWith(draft.MailBody, draft.Body);
        Assert.Equal(draft.MailBody.Replace("\r\n", "\n"), draft.Preview);
        foreach (var line in draft.Body.Split(new[] { "\r\n" }, StringSplitOptions.None).Where(l => l.Length > 0))
        {
            Assert.Contains(line, draft.CopyText.Replace("\n", "\r\n").Split(new[] { "\r\n" }, StringSplitOptions.None));
        }
    }

    [Fact]
    public void Every_field_is_present_for_a_full_report_and_a_line_is_left_out_when_unknown()
    {
        var facts = new ReportFacts
        {
            App = "Word add-in 1.2.0", SdkVersion = "0.3.1", Sdk = "Fadenstack SDK 0.3.1 (.NET)", System = "Windows 10.0.26300", Mode = "Organisation",
            Agent = "Docs", Server = "https://ai.example.internal", Endpoint = "ollama, on this computer", Time = "2026-11-19 10:42 UTC",
            Error = new ReportedError("picture_type_not_allowed", "req-1"),
        };
        string[] Names(ReportFacts f) => ReportMailBuilder.Build("a@b.example", new ReportLabels(), f).Body.Split(new[] { "\r\n" }, StringSplitOptions.None)
            .SkipWhile(l => l != "-- ").Skip(1).Select(l => l.Substring(0, l.IndexOf(':'))).ToArray();

        Assert.Equal(new[] { "App", "SDK", "System", "Mode", "Agent", "Server", "Endpoint", "Time", "Error" }, Names(facts));
        Assert.Equal(new[] { "SDK", "Time" }, Names(new ReportFacts { Sdk = "Fadenstack SDK 0.3.1 (.NET)", Time = "t" }));
        Assert.Equal(new[] { "SDK", "Time" }, Names(new ReportFacts { Sdk = "s", Time = "t", Error = new ReportedError(null, "req-1") })); // an id alone says nothing
    }

    [Fact]
    public void The_body_starts_with_the_label_and_two_empty_lines_and_uses_CRLF()
    {
        var draft = ReportMailBuilder.Build("a@b.example", new ReportLabels(), new ReportFacts { SdkVersion = "0.3.1", Sdk = "s", Time = "t" });

        Assert.StartsWith("What happened:\r\n\r\n\r\n-- \r\n", draft.Body);
        Assert.DoesNotContain("\n", draft.Body.Replace("\r\n", ""));
        Assert.DoesNotContain("\r", draft.CopyText);
        Assert.Contains("body=What%20happened%3A%0D%0A%0D%0A%0D%0A--%20%0D%0A", draft.Mailto);
    }

    [Theory]
    [InlineData("AZaz09-._~", "AZaz09-._~")]
    [InlineData(" ", "%20")]
    [InlineData("\r\n", "%0D%0A")]
    [InlineData("a&b=c?d#e%f+g/h:i", "a%26b%3Dc%3Fd%23e%25f%2Bg%2Fh%3Ai")]
    [InlineData("é", "%C3%A9")]
    [InlineData("€", "%E2%82%AC")]
    [InlineData("文", "%E6%96%87")]
    [InlineData("\U0001F600", "%F0%9F%98%80")]
    [InlineData("'()*!", "%27%28%29%2A%21")]
    public void The_encoding_is_UTF8_percent_encoding_of_all_but_the_unreserved_characters(string text, string encoded)
    {
        Assert.Equal(encoded, ReportMailBuilder.Encode(text));
    }

    // ---- the address --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a@b.example")]
    [InlineData("first.last+tag@mail.example.org")]
    [InlineData("it_help-desk@sub-domain.example.co")]
    [InlineData("support@fadenstack.com")]
    public void One_plain_address_is_accepted(string address)
    {
        Assert.True(SupportAddress.IsValid(address));
    }

    [Theory]
    [InlineData("a@b.example?bcc=x@y.example")]
    [InlineData("a@b.example?subject=x")]
    [InlineData("a@b.example&body=x")]
    [InlineData("a@b.example,c@d.example")]
    [InlineData("a@b.example;c@d.example")]
    [InlineData("a b@c.example")]
    [InlineData("a@b.example ")]
    [InlineData(" a@b.example")]
    [InlineData("a@b.example\n")]
    [InlineData("a@b.example\r\nbcc: x@y.example")]
    [InlineData("a\n@b.example")]
    [InlineData("a%40b.example")]
    [InlineData("a@b.example%0D%0A")]
    [InlineData("a@b")]
    [InlineData("a@.example")]
    [InlineData("a@b..example")]
    [InlineData("@b.example")]
    [InlineData("a@b.example#frag")]
    [InlineData("<a@b.example>")]
    [InlineData("\"a b\"@c.example")]
    [InlineData("mailto:a@b.example")]
    [InlineData("a@b@c.example")]
    [InlineData("ä@b.example")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_could_add_a_recipient_a_bcc_or_a_body_is_refused(string? address)
    {
        Assert.False(SupportAddress.IsValid(address));
    }

    [Fact]
    public void An_address_is_at_most_254_characters()
    {
        var longest = new string('x', 254 - "@b.example".Length) + "@b.example";
        Assert.Equal(254, longest.Length);
        Assert.True(SupportAddress.IsValid(longest));
        Assert.False(SupportAddress.IsValid("x" + longest));
        Assert.False(SupportAddress.IsValid(new string('a', 255)));
    }

    [Fact]
    public void The_builder_refuses_an_address_that_is_not_one_plain_address()
    {
        foreach (var bad in new[] { "a@b.example?bcc=x@y.example", "a@b.example,c@d.example", "a b@c.example", "a@b.example\n", "a%40b.example" })
        {
            Assert.Throws<ArgumentException>(() => ReportMailBuilder.Build(bad, new ReportLabels(), new ReportFacts { Sdk = "s", Time = "t" }));
        }
    }

    // ---- the system line ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Microsoft Windows 10.0.26300", "Windows 10.0.26300")]
    [InlineData("Microsoft Windows 10.0.19045", "Windows 10.0.19045")]
    [InlineData("Linux 6.8.0-45-generic #45-Ubuntu SMP PREEMPT_DYNAMIC Fri Aug 30 12:02:04 UTC 2024", "Linux 6.8")]
    [InlineData("Linux 5.15.153.1-microsoft-standard-WSL2 #1 SMP Fri Mar 29 23:14:13 UTC 2024", "Linux 5.15")]
    [InlineData("Darwin 24.1.0 Darwin Kernel Version 24.1.0: Thu Oct 10 21:02:45 PDT 2024; root:xnu-11215.41.3~2/RELEASE_X86_64", "macOS 15.1")]
    [InlineData("Darwin 23.6.0 Darwin Kernel Version 23.6.0", "macOS 14.6")]
    [InlineData("Darwin 25.0.0 Darwin Kernel Version 25.0.0", "macOS 26.0")]
    [InlineData("Darwin 19.6.0 Darwin Kernel Version 19.6.0", "macOS 10.15")]
    [InlineData("FreeBSD 14.0-RELEASE FreeBSD 14.0-RELEASE-p3", "FreeBSD 14.0")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("something odd", null)]
    public void The_OS_is_reduced_to_its_family_and_version(string? description, string? expected)
    {
        Assert.Equal(expected, ReportSystem.Of(description));
    }

    [Fact]
    public void This_computers_system_line_has_no_machine_or_user_name()
    {
        var system = ReportSystem.Current();

        Assert.NotNull(system);
        Assert.DoesNotContain(Environment.MachineName, system, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, system, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"^[A-Za-z][A-Za-z ]* \d+(\.\d+)+$", system);
    }

    // ---- who gets it, by mode ---------------------------------------------------------------------------------

    [Fact]
    public void The_policys_address_comes_first_in_both_modes_and_only_standalone_has_the_hosts_and_the_default()
    {
        var policy = MachinePolicy.FromJson("""{"supportEmail":"it@example.org"}""");

        Assert.Equal("it@example.org", ReportAddress.Resolve(AgentConnection.Governed, policy, "host@example.org"));
        Assert.Equal("it@example.org", ReportAddress.Resolve(AgentConnection.Standalone, policy, "host@example.org"));
        Assert.Null(ReportAddress.Resolve(AgentConnection.Governed, MachinePolicy.None, "host@example.org"));
        Assert.Equal("host@example.org", ReportAddress.Resolve(AgentConnection.Standalone, MachinePolicy.None, "host@example.org"));
        Assert.Equal("support@fadenstack.com", ReportAddress.Resolve(AgentConnection.Standalone, MachinePolicy.None, null));
        Assert.Equal("support@fadenstack.com", ReportAddress.Resolve(AgentConnection.Standalone, MachinePolicy.None, "not an address"));
    }
}
