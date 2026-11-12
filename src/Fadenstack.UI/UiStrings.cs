using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using Fadenstack.Agents;
using Fadenstack.Tools;

namespace Fadenstack.UI;

/// <summary>
/// Every text the chat shows, read from one place: the package's resources, <c>Resources/UiText.resx</c> (English)
/// and its translations (<c>UiText.de.resx</c>, <c>.fr</c>, <c>.es</c>, <c>.zh-Hans</c>). The view models take their
/// texts from here and the WPF skin binds to it, so neither has a text of its own. A host adds a language or
/// changes words with resources of its own (a <c>.resx</c> with the same names): for each language, from the most
/// specific to English, the host's text wins, then the package's.
/// </summary>
public sealed class UiStrings
{
    private static readonly ResourceManager Package = new("Fadenstack.UI.Resources.UiText", typeof(UiStrings).Assembly);
    private readonly ResourceManager? _host;
    private readonly IReadOnlyList<CultureInfo> _cultures;
    private readonly ConcurrentDictionary<string, string?> _found = new(StringComparer.Ordinal);

    /// <param name="culture">The language of the texts. de-AT finds German, zh-CN Simplified Chinese, and a language
    /// neither the host nor the package has finds English.</param>
    /// <param name="overrides">The host's own resources, tried first: another language, or its own words.</param>
    /// <param name="formatCulture">For numbers and dates; <see cref="CultureInfo.CurrentCulture"/> by default.</param>
    public UiStrings(CultureInfo culture, ResourceManager? overrides = null, CultureInfo? formatCulture = null)
    {
        Culture = culture ?? throw new ArgumentNullException(nameof(culture));
        FormatCulture = formatCulture ?? CultureInfo.CurrentCulture;
        _host = overrides;
        var cultures = new List<CultureInfo>();
        for (var c = culture; !Equals(c, CultureInfo.InvariantCulture); c = c.Parent)
        {
            cultures.Add(c);
        }
        cultures.Add(CultureInfo.InvariantCulture);
        _cultures = cultures;
        Language = cultures.FirstOrDefault(c => !Equals(c, CultureInfo.InvariantCulture) && (Has(_host, c) || Has(Package, c)))?.Name ?? "en";
    }

    /// <summary>The texts in <paramref name="culture"/>'s language.</summary>
    public static UiStrings For(CultureInfo culture) => new(culture);

    /// <summary>The texts in the current UI language (<see cref="CultureInfo.CurrentUICulture"/>).</summary>
    public static UiStrings Current => new(CultureInfo.CurrentUICulture);

    /// <summary>The languages the package ships.</summary>
    public static IReadOnlyList<string> BuiltInLanguages { get; } = new[] { "en", "de", "fr", "es", "zh-Hans" };

    /// <summary>The language asked for.</summary>
    public CultureInfo Culture { get; }

    /// <summary>The culture numbers and dates are written in.</summary>
    public CultureInfo FormatCulture { get; }

    /// <summary>The language the texts are in: the nearest one there are texts for ("de" for de-AT, "en" when none).</summary>
    public string Language { get; }

    /// <summary>The texts run right to left (an Arabic or Hebrew translation): a skin mirrors its layout.</summary>
    public bool IsRightToLeft => CultureInfo.GetCultureInfo(Language).TextInfo.IsRightToLeft;

    /// <summary>The text named <paramref name="name"/>, or the name itself when there is none (for a skin's binding).</summary>
    public string this[string name] => Find(name) ?? name;

    /// <summary>The text named <paramref name="name"/>: the host's, else the package's; null when neither has one.</summary>
    public string? Find(string name) => _found.GetOrAdd(name, Lookup);

    // -- the language and the settings ----------------------------------------------------------------

    /// <summary>The language's name in itself ("Deutsch"): the settings list each language by it, so anyone finds their own.</summary>
    public string LanguageName => Text();

    public string Settings => Text();

    /// <summary>The heading of the languages in the settings.</summary>
    public string LanguageMenu => Text();

    /// <summary>"Automatic (English)": follow the host's language, named in it.</summary>
    public string LanguageAutomatic(string name) => Fill(name);

    /// <summary>Under the languages when the organisation fixed one.</summary>
    public string LanguageLocked => Text();

    // -- the turn ------------------------------------------------------------------------------------

    public string Working => Text();

    /// <summary>The turn's line while the host's turn gate holds it back.</summary>
    public string WaitingForSlot => Text();

    public string Stopped => Text();

    public string Failed => Text();

    public string Tokens(long count) => Fill(count);

    public string NotAttached(string title) => Fill(title);

    /// <summary>"1.2 s", "350 ms".</summary>
    public string Duration(TimeSpan duration) => duration.TotalSeconds >= 1
        ? Fill(duration.TotalSeconds, "DurationSeconds")
        : Fill(duration.TotalMilliseconds, "DurationMilliseconds");

    // -- tools ---------------------------------------------------------------------------------------

    public string ToolRunning(string name) => Fill(name);

    public string ToolWaiting(string name) => Fill(name);

    public string ToolRan(string name) => Fill(name);

    public string ToolFailed(string name) => Fill(name);

    public string ToolDenied(string name) => Fill(name);

    public string ToolStopped(string name) => Fill(name);

    /// <summary>A tool's class on its line: Read, Write, Destructive, Unclassified.</summary>
    public string ToolClassLabel(ToolClass toolClass) => this["ToolClass_" + toolClass];

    /// <summary>Where a tool runs, for <c>host</c>, <c>mcp</c> and <c>gateway</c>; another source as it is.</summary>
    public string ToolSource(string source) => Find("ToolSource_" + source) ?? source;

    public string MoreCharacters(int count) => Fill(count);

    public string Arguments => Text();

    public string Result => Text();

    // -- approvals -----------------------------------------------------------------------------------

    public string ApprovalTitle(string toolName) => Fill(toolName);

    /// <summary>What a card says the tool does: Read, Changes data, Cannot be undone, Unclassified.</summary>
    public string ApprovalClassLabel(ToolClass toolClass) => this["ApprovalClass_" + toolClass];

    /// <summary>Why a card asks about one of the agent's own tools, by its class.</summary>
    public string ApprovalWhy(ToolClass toolClass) => this["ApprovalWhy_" + toolClass];

    public string ServerTool => Text();

    /// <summary>The source line of a card for a local MCP server's tool: "From {0}, an MCP server on this computer".</summary>
    public string ApprovalFromMcp(string label) => Fill(label);

    /// <summary>Why a card asks about a local MCP server's tool nobody classified.</summary>
    public string ApprovalUnchecked => Text();

    /// <summary>What the server's own annotations say, after <see cref="ApprovalUnchecked"/>.</summary>
    public string ApprovalServerReadOnly => Text();

    public string ApprovalServerDestructive => Text();

    /// <summary>The badge of a card for a tool that reaches outside the organisation (a web search, opening a page).</summary>
    public string ApprovalOutside => Text();

    /// <summary>Why a card asks about a tool that reaches outside the organisation.</summary>
    public string ApprovalOutsideWhy => Text();

    /// <summary>Why a card asks although the user let the agent work in a place: a link leads out of it, to <c>{0}</c>.</summary>
    public string ApprovalLeadsOutside(string realPlace) => Fill(realPlace);

    public string Allow => Text();

    public string AllowForChat => Text();

    public string AllowMore => Text();

    public string AllowAlways => Text();

    public string Deny => Text();

    public string Allowed => Text();

    public string AllowedForChat => Text();

    public string AllowedAlways => Text();

    public string Denied => Text();

    public string NoAnswer => Text();

    public string ResetApprovals => Text();

    public string ResetApprovalsHint => Text();

    /// <summary>The card's button that opens the times of a grant for a time: "Allow for…".</summary>
    public string AllowFor => Text();

    /// <summary>A card answered for a time: "Allowed for 30 min".</summary>
    public string AllowedFor(string duration) => Fill(duration);

    /// <summary>A card once the server confirmed the time (it may be less than the one picked): "Server confirmed up to 30 min".</summary>
    public string AllowedForConfirmed(string duration) => Fill(duration);

    /// <summary>A card whose time the person picked and the server has not yet answered.</summary>
    public string ConfirmingGrant => Text();

    /// <summary>What a time grant is, beside "Allow for…".</summary>
    public string TimeGrantHint => Text();

    /// <summary>A call a running grant let through without asking.</summary>
    public string AllowedByGrant => Text();

    /// <summary>A time a user allows, or the time left: "15 min", "2 h", "1 h 30 min".</summary>
    public string Period(TimeSpan span)
    {
        var minutes = (int)Math.Ceiling(span.TotalMinutes);
        return minutes < 60 ? Fill(minutes, "Duration_Minutes")
            : minutes % 60 == 0 ? Fill(minutes / 60, "Duration_Hours")
            : Fill2(minutes / 60, minutes % 60, "Duration_HoursMinutes");
    }

    // -- grants: the card, the marker, Settings → Approvals (sdk/38 §7) ------------------------------

    /// <summary>The grant card's question: "Work alongside you?".</summary>
    public string GrantTitle => Text();

    /// <summary>The card that extends a running grant: "Keep working alongside you?".</summary>
    public string GrantExtendTitle => Text();

    /// <summary>The label of the card's time picker.</summary>
    public string GrantDuration => Text();

    /// <summary>Before the places: "I'll work in:".</summary>
    public string GrantPlaces => Text();

    /// <summary>Before the places an extension adds: "Also in:".</summary>
    public string GrantNewPlaces => Text();

    /// <summary>An extension's time: "30 min longer".</summary>
    public string GrantMoreTime(string duration) => Fill(duration);

    /// <summary>What the agent may do without asking, by the classes the grant covers.</summary>
    public string GrantMay(Parts.GrantAbility ability) => this[$"GrantMay_{ability}"];

    /// <summary>What still asks inside a grant.</summary>
    public string GrantStillAsks => Text();

    public string GrantAllow => Text();

    public string GrantNotNow => Text();

    /// <summary>A place's button on the card: take it out of the grant.</summary>
    public string GrantRemovePlace => Text();

    /// <summary>A place's button on the card: put it back.</summary>
    public string GrantKeepPlace => Text();

    /// <summary>The card once allowed.</summary>
    public string GrantAllowed => Text();

    /// <summary>The card once declined.</summary>
    public string GrantDeclined => Text();

    /// <summary>The card when the server refused or could not be reached: no answer, no grant.</summary>
    public string GrantFailed => Text();

    /// <summary>The marker in the chat header: "Working alongside you · 23 min left".</summary>
    public string MarkerWorking(string left) => Fill(left);

    /// <summary>The marker while the grant is paused (the person away).</summary>
    public string MarkerPaused => Text();

    /// <summary>The marker's button that ends every grant and the turn.</summary>
    public string MarkerStop => Text();

    /// <summary>The marker's button near the end: "Another 30 min".</summary>
    public string MarkerKeepGoing(string duration) => Fill(duration);

    /// <summary>The Settings section with what the user allowed for longer than one call.</summary>
    public string SettingsApprovals => Text();

    public string SettingsApprovalsEmpty => Text();

    /// <summary>A grant for a time in Settings: "add_comment, 23 min left".</summary>
    public string SettingsGrantTime(string tool, string left) => Fill2(tool, left);

    /// <summary>A stretch of work in Settings: "Working alongside you in report.docx, 23 min left".</summary>
    public string SettingsGrantWork(string places, string left) => Fill2(places, left);

    /// <summary>An "always" approval in Settings: "add_comment, always".</summary>
    public string SettingsAlways(string tool) => Fill(tool);

    public string Revoke => Text();

    // -- Bypass: the confirmation, the marker, its end (sdk/09 L-58, L-59, L-63) --------------------------

    /// <summary>The confirmation's question: "Switch approvals off in this chat?".</summary>
    public string BypassTitle => Text();

    /// <summary>What no longer asks: the agent's destructive and unclassified tools too, in this chat only.</summary>
    public string BypassNoLongerAsks => Text();

    /// <summary>What still asks: an administrator's "Ask first" rules and tools marked to always ask.</summary>
    public string BypassStillAsks => Text();

    /// <summary>
    /// What the server records of each call under Bypass, in plain words, by the level the server names (<c>decisions</c>,
    /// <c>calls</c>, <c>results</c>; sdk/09 L-60). None named: <c>calls</c>, the server's default; a level this SDK does
    /// not know: the most it could be, <c>results</c> -- the card never says less is kept than is.
    /// </summary>
    public string BypassRecords(string? evidence) => this["BypassRecords_" + Level(evidence)];

    /// <summary>The marker's short line on what is recorded, by the running grant's level.</summary>
    public string BypassRecorded(string? evidence) => this["BypassRecorded_" + Level(evidence)];

    /// <summary>Under the time: the time is not extended; more time is a new Bypass.</summary>
    public string BypassNoExtension => Text();

    /// <summary>The label of the optional reason.</summary>
    public string BypassReason => Text();

    /// <summary>Under the reason: administrators see it.</summary>
    public string BypassReasonSeen => Text();

    /// <summary>The button that switches Bypass on: never the default button.</summary>
    public string BypassAllow => Text();

    /// <summary>The confirmation after the click, until the server answers.</summary>
    public string BypassConfirming => Text();

    /// <summary>The confirmation once the server confirmed it.</summary>
    public string BypassOn => Text();

    /// <summary>The confirmation while the second sign-in runs in the browser (sdk/41).</summary>
    public string BypassSignIn => Text();

    /// <summary>Under it: the code the page shows, for the person to compare.</summary>
    public string BypassSignInCode(string code) => Fill(code);

    /// <summary>The button that opens the sign-in page in the browser again.</summary>
    public string BypassOpenAgain => Text();

    /// <summary>The same button for screen readers.</summary>
    public string BypassOpenAgainName => Text();

    /// <summary>The button on the "Bypass ended" line that asks for Bypass again (the card, then the second sign-in).</summary>
    public string BypassSwitchOnAgain => Text();

    /// <summary>The same button for screen readers.</summary>
    public string BypassSwitchOnAgainName => Text();

    /// <summary>The chat list's line for a chat that runs in Bypass: "Bypass · 12 min left".</summary>
    public string BypassListMarker(string left) => Fill(left);

    /// <summary>
    /// The confirmation when the server gave no acknowledgement (L-59), by why: <c>refused</c>, <c>offline</c>,
    /// <c>unavailable</c>, and for the second sign-in <c>denied</c>, <c>timed_out</c>, <c>no_browser</c>; null: it did not say.
    /// </summary>
    public string BypassNotConfirmed(string? why) => why is null ? this["BypassNotConfirmed"] : Find("BypassNotConfirmed_" + why) ?? this["BypassNotConfirmed"];

    /// <summary>The marker: "Bypass on · 12 min left".</summary>
    public string BypassMarker(string left) => Fill(left);

    /// <summary>The marker's Stop for screen readers: "Stop Bypass".</summary>
    public string BypassStopName => Text();

    /// <summary>
    /// Why Bypass ended, from the grant's <c>revoked_by</c> or its time (<see cref="Fadenstack.Agents.AgentGrants.WhyEndedAsync"/>):
    /// <c>person</c>, <c>expired</c>, <c>admin</c>, <c>kill_switch</c>, <c>limit</c>, <c>spent</c>; another or none: the
    /// server ended it.
    /// </summary>
    public string BypassEnded(string? why) => why is null ? this["BypassEnded"] : Find("BypassEnded_" + why) ?? this["BypassEnded"];

    /// <summary>A running Bypass in Settings: "Bypass in a chat, 12 min left · Each tool that runs is recorded…".</summary>
    public string SettingsGrantBypass(string left, string recorded) => Fill2(left, recorded);

    // -- the marker for every grant of the device, across chats (sdk/46 G2) -----------------------------

    /// <summary>The device-wide marker with several grants running: "2 grants running · first ends in 12 min".</summary>
    public string GrantsMarkerMany(int count, string left) => Fill2(count, left);

    /// <summary>One grant's time left in a marker's line: "12 min left".</summary>
    public string GrantsLeft(string left) => Fill(left);

    /// <summary>A paused grant in a marker's line.</summary>
    public string GrantsPaused => Text();

    /// <summary>The avatar's badge while the grant that ends first is paused.</summary>
    public string GrantsBadgePaused => Text();

    /// <summary>The marker of a single Bypass that is paused (the app was closed).</summary>
    public string GrantsBypassPaused => Text();

    /// <summary>What a grant is called in a marker's line: "Working alongside you", or "Bypass" for <c>bypass</c>.</summary>
    public string GrantsKind(string kind) => kind == Fadenstack.Models.GrantKinds.Bypass ? this["Mode_Bypass_Label"] : this["GrantsKindWork"];

    /// <summary>The chat of a grant when the app cannot name it.</summary>
    public string GrantsChatUnknown => Text();

    /// <summary>Stop did not reach the server: what is left runs on until its time.</summary>
    public string GrantsStopFailed => Text();

    public string GrantsExtendFailed => Text();

    /// <summary>The server refused more time: the grant is at the longest it may run.</summary>
    public string GrantsAtLongest => Text();

    /// <summary>A grant stays paused because the server could not check whether it may go on.</summary>
    public string GrantsWaiting => Text();

    /// <summary>The title of a stretch of work's summary: "Worked alongside you for 23 min".</summary>
    public string GrantsSummaryWork(string period) => Fill(period);

    /// <summary>The title of a Bypass's summary: "Bypass ran for 23 min".</summary>
    public string GrantsSummaryBypass(string period) => Fill(period);

    /// <summary>"Where: Word "report.docx" · Folder "Offers"".</summary>
    public string GrantsSummaryPlaces(string places) => Fill(places);

    /// <summary>"Ran without asking: 3".</summary>
    public string GrantsSummaryCalls(int calls) => Fill(calls);

    /// <summary>Why a grant ended, as <see cref="Fadenstack.Agents.GrantLifecycle"/> says it: one sentence for a summary.</summary>
    public string GrantsEnded(Fadenstack.Agents.GrantEndReason reason) => this["GrantsEnded_" + reason];

    private static string Level(string? evidence) => evidence switch
    {
        null or "" or "calls" => "calls",
        "decisions" => "decisions",
        _ => "results",
    };

    // -- notices -------------------------------------------------------------------------------------

    /// <summary>A notice's title for the server's outcome (<c>prompt_blocked</c>, ...); null for one there is no text for.</summary>
    public string? NoticeTitle(string outcome) => Find("Notice_" + outcome);

    public string NoticeFallback => Text();

    public string TryAgainIn(double seconds) => Fill(seconds);

    public string Decision(string id) => Fill(id);

    /// <summary>The notice for an answer without a machine-readable AI mark (shown only when the user turned it on).</summary>
    public string UnmarkedTitle => Text();

    public string UnmarkedMessage => Text();

    /// <summary>The settings item that turns that notice on.</summary>
    public string SettingsUnmarked => Text();

    public string PiiTitle => Text();

    /// <summary>"3 values kept from the model: 2 person, 1 email".</summary>
    public string PiiMessage(int total, string list) => Fill2(total, list, total == 1 ? "PiiMessage_One" : "PiiMessage_Other");

    /// <summary>A model call ended with no text and no tool call (<c>empty_answer</c>): what the notice says, and that Try again may help.</summary>
    public string EmptyAnswer => Text();

    /// <summary>The same when the model ran out of tokens first (<c>empty_answer</c>, <c>length</c>), often while thinking.</summary>
    public string EmptyAnswerLength => Text();

    // -- reasoning and references --------------------------------------------------------------------

    public string Thinking => Text();

    public string ThoughtFor(int seconds) => Fill(seconds);

    public string UsedReferences(int count) => Fill(count, count == 1 ? "UsedReferences_One" : "UsedReferences_Other");

    public string Part(int number) => Fill(number);

    public string Source => Text();

    // -- the chat ------------------------------------------------------------------------------------

    public string GreetingFallback(string agent) => Fill(agent);

    public string NewChat => Text();

    public string NewChatShortcut => Text();

    public string Yesterday => Text();

    public string ModeLabel(ToolMode mode) => this[$"Mode_{mode}_Label"];

    public string ModeDescription(ToolMode mode) => this[$"Mode_{mode}_Description"];

    /// <summary>"Asks before ... Limited to Read only by your organisation."</summary>
    public string LimitedTo(string description, string mode) => Fill2(description, mode);

    /// <summary>"Asks before ... This agent goes no higher than Ask.": the agent's own highest level is the limit.</summary>
    public string LimitedByAgent(string description, string mode) => Fill2(description, mode);

    public string ToolModeMenu => Text();

    /// <summary>A level of thinking.</summary>
    public string ReasoningLabel(ReasoningLevel level) => this[$"Reasoning_{level}_Label"];

    /// <summary>What the level means, in one line.</summary>
    public string ReasoningDescription(ReasoningLevel level) => this[$"Reasoning_{level}_Description"];

    /// <summary>The pill: "Thinking: Low".</summary>
    public string ReasoningPill(string level) => Fill(level);

    /// <summary>The heading of the thinking menu, and the pill's name for screen readers.</summary>
    public string ReasoningMenu => Text();

    // -- the skin's controls --------------------------------------------------------------------------

    public string History => Text();

    /// <summary>The header button that opens the list of chats.</summary>
    public string ChatHistory => Text();

    /// <summary>The header button that opens the panel's settings (language, About).</summary>
    public string MoreOptions => Text();

    /// <summary>The composer button that attaches files (where the host offers it).</summary>
    public string Attach => Text();

    /// <summary>The title of an empty chat, over the agent's greeting.</summary>
    public string EmptyTitle => Text();

    /// <summary>The title of the chat while the agent needs a sign-in first.</summary>
    public string SignInTitle => Text();

    public string NoHistory => Text();

    public string DeleteSession => Text();

    public string ShowEarlier => Text();

    public string JumpToNewest => Text();

    public string Retry => Text();

    public string Copy => Text();

    public string CopyCode => Text();

    public string MarkdownPreview => Text();

    public string MarkdownSource => Text();

    public string DiagramMermaid => Text();

    public string NotSent => Text();

    public string EditAndResend => Text();

    public string ComposerPlaceholder => Text();

    /// <summary>
    /// Under the message box: that the answers come from an AI and can be wrong (the EU AI Act asks that people know
    /// they deal with an AI). A host's empty text hides it, where it says so in another place the user always sees.
    /// </summary>
    public string AiNotice => Text();

    /// <summary>The composer's name for screen readers.</summary>
    public string Message => Text();

    public string Send => Text();

    public string SendShortcut => Text();

    public string Stop => Text();

    public string StopShortcut => Text();

    public string RemoveAttachment => Text();

    public string IncludeAttachment => Text();

    // -- skills ($ or / in the composer) ---------------------------------------------------------------

    /// <summary>The skill picker's name.</summary>
    public string Skills => Text();

    /// <summary>The second line of a picked skill's chip.</summary>
    public string Skill => Text();

    public string SkillsLoading => Text();

    public string NoSkills => Text();

    /// <summary>"No skill matches "contr"."</summary>
    public string NoSkillMatches(string query) => Fill(query);

    public string SkillsFailed => Text();

    /// <summary>Under the picker: how to use it.</summary>
    public string SkillPickerHint => Text();

    // -- pictures -------------------------------------------------------------------------------------

    /// <summary>The second line of a picture's chip.</summary>
    public string Picture => Text();

    /// <summary>A pasted picture's name.</summary>
    public string PastedPicture => Text();

    /// <summary>Said when a picture is added for an agent whose model does not see pictures.</summary>
    public string PicturesNotSeen => Text();

    public string PictureNotRead(string name) => Fill(name);

    // -- content marking (Fadenstack.Provenance) -------------------------------------------------------

    /// <summary>"AI-generated": a visible label on content made with AI, where the organisation asks for one.</summary>
    public string AiGenerated => Text();

    /// <summary>
    /// The tooltip (<c>title</c>) on content made with AI that an agent put into a page or a mail: <see cref="AiGenerated"/>,
    /// a space, the record's id, in every language (the TypeScript SDK and the host agents write the same, and readers
    /// find the id so).
    /// </summary>
    public string AiGeneratedTitle(string record) => Fill(record);

    // -- About (the settings' last item) --------------------------------------------------------------

    /// <summary>The settings' last item, and the heading of what it opens.</summary>
    public string About => Text();

    /// <summary>Closes About.</summary>
    public string Close => Text();

    /// <summary>"Agent: Docs", a line of the details About copies.</summary>
    public string AboutAgent(string name) => Fill(name);

    /// <summary>"Version 3": the agent's published version.</summary>
    public string AboutVersion(int version) => Fill(version);

    /// <summary>The agent is a maintainer's draft, not a published version.</summary>
    public string AboutDraft => Text();

    /// <summary>"Model: qwen3-32b".</summary>
    public string AboutModel(string model) => Fill(model);

    /// <summary>"Runs in Word add-in 1.2.0": the app the host described.</summary>
    public string AboutApp(string app) => Fill(app);

    /// <summary>"Server: https://ai.example.internal".</summary>
    public string AboutServer(string address) => Fill(address);

    /// <summary>The organisation's own Help, on its server.</summary>
    public string Help => Text();

    /// <summary>The SDK documentation for developers.</summary>
    public string DeveloperDocs => Text();

    /// <summary>Copies what About shows, for a support request.</summary>
    public string CopyDetails => Text();

    /// <summary>"Help (opens in your browser)": a link's name for screen readers.</summary>
    public string OpensInBrowser(string link) => Fill(link);

    // -- lookup --------------------------------------------------------------------------------------

    private string Text([CallerMemberName] string name = "") => this[name];

    private string Fill(object? arg, [CallerMemberName] string name = "") => string.Format(FormatCulture, this[name], arg);

    private string Fill2(object? first, object? second, [CallerMemberName] string name = "") =>
        string.Format(FormatCulture, this[name], first, second);

    private string? Lookup(string name)
    {
        foreach (var culture in _cultures)
        {
            // The package's English is its neutral resources: they come last, after a host's own words.
            var package = culture.Name == "en" ? null : Exact(Package, culture, name);
            if ((Exact(_host, culture, name) ?? package) is { } text)
            {
                return text;
            }
        }
        return null;
    }

    private static string? Exact(ResourceManager? resources, CultureInfo culture, string name)
    {
        try
        {
            return resources?.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(name);
        }
        catch (MissingManifestResourceException)
        {
            return null; // the host has no resources for this language
        }
    }

    private static bool Has(ResourceManager? resources, CultureInfo culture)
    {
        try
        {
            return resources?.GetResourceSet(culture, createIfNotExists: true, tryParents: false) is not null;
        }
        catch (MissingManifestResourceException)
        {
            return false;
        }
    }
}
