using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using Fadenstack.Agents;
using Fadenstack.SkillAuthoring;
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

    /// <summary>The link on a card that opens the field for a reason: "Say why".</summary>
    public string SayWhy => Text();

    /// <summary>The reason field's hint: "Why not? The assistant sees your reason."</summary>
    public string DenyReasonPlaceholder => Text();

    /// <summary>The reason field's accessible name: "Your reason for declining".</summary>
    public string DenyReasonName => Text();

    /// <summary>A declined card's line: "Your reason: ...".</summary>
    public string YourReason(string reason) => Fill(reason);

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

    /// <summary>The owner's note for a refusal with <c>images_disabled</c> (the notice's title is <c>Notice_images_disabled</c>).</summary>
    public string ImagesDisabled => Text();

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

    /// <summary>"Copied": what the answer's Copy says for a moment once the text is on the clipboard.</summary>
    public string Copied => Text();

    /// <summary>"Not copied: this answer could not be marked as AI-generated, and your organisation requires it." (the policy refused the copy)</summary>
    public string CopyRefused => Text();

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

    // -- the skill library, the editor and "Draft from this chat" (sdk/51 F04) ------------------------------------

    public string SkillLibraryTitle => Text();

    public string SkillLibraryLoading => Text();

    public string SkillLibraryFailed => Text();

    public string SkillLibraryEmpty => Text();

    public string SkillOriginBundled => Text();

    public string SkillOriginOwn => Text();

    public string SkillOriginOrganisation => Text();

    public string SkillNotReviewed => Text();

    public string SkillOn => Text();

    public string SkillOff => Text();

    public string SkillNew => Text();

    public string SkillEdit => Text();

    public string SkillView => Text();

    public string SkillDuplicate => Text();

    public string SkillDelete => Text();

    public string SkillImport => Text();

    public string SkillExport => Text();

    public string SkillCancel => Text();

    public string SkillSave => Text();

    public string SkillReplace => Text();

    /// <summary>"Delete {0}?"</summary>
    public string SkillDeleteTitle(string name) => Fill(name);

    public string SkillDeleteBody => Text();

    /// <summary>"Import {0}?"</summary>
    public string SkillImportTitle(string name) => Fill(name);

    public string SkillImportBody => Text();

    /// <summary>"A skill named {0} is already here. Importing replaces it."</summary>
    public string SkillImportReplaceBody(string name) => Fill(name);

    public string SkillImportRefused => Text();

    /// <summary>"{0} was imported."</summary>
    public string SkillImported(string name) => Fill(name);

    /// <summary>"{0} was saved."</summary>
    public string SkillSaved(string name) => Fill(name);

    /// <summary>"{0} was deleted."</summary>
    public string SkillDeleted(string name) => Fill(name);

    /// <summary>"{0} was exported."</summary>
    public string SkillExported(string name) => Fill(name);

    /// <summary>"{0} could not be exported."</summary>
    public string SkillExportFailed(string name) => Fill(name);

    public string SkillSaveFailed => Text();

    /// <summary>"Replace {0}?"</summary>
    public string SkillExistsTitle(string name) => Fill(name);

    public string SkillExistsBody => Text();

    /// <summary>"Editing {0}"</summary>
    public string SkillEditorEditing(string name) => Fill(name);

    /// <summary>"{0} is read only."</summary>
    public string SkillEditorReadOnly(string name) => Fill(name);

    public string SkillEditorProposal => Text();

    public string SkillName => Text();

    public string SkillDescription => Text();

    public string SkillInstructions => Text();

    public string SkillPreview => Text();

    public string SkillLicense => Text();

    public string SkillCompatibility => Text();

    public string SkillMatching => Text();

    public string SkillTitleField => Text();

    public string SkillTags => Text();

    public string SkillKeywords => Text();

    public string SkillFileTypes => Text();

    public string SkillPreferredTools => Text();

    public string SkillPriority => Text();

    public string SkillActivation => Text();

    public string SkillActivationAuto => Text();

    public string SkillActivationAlways => Text();

    public string SkillActivationManual => Text();

    public string SkillResources => Text();

    public string SkillNoResources => Text();

    public string SkillAddFile => Text();

    public string SkillRenameFile => Text();

    public string SkillRemoveFile => Text();

    public string SkillDropUnknownKeys => Text();

    public string SkillChecksHeading => Text();

    public string SkillChecksOk => Text();

    public string SkillTestHeading => Text();

    public string SkillTestPrompt => Text();

    public string SkillTestRun => Text();

    public string SkillTestListed => Text();

    public string SkillTestPreloaded => Text();

    public string SkillTestNone => Text();

    public string SkillTestEditedNotListed => Text();

    public string SkillMatchExplicit => Text();

    public string SkillMatchAlways => Text();

    public string SkillMatchTrigger => Text();

    public string SkillAskModel => Text();

    public string SkillModelNote => Text();

    public string SkillModelAsking => Text();

    /// <summary>"The model would load {0}."</summary>
    public string SkillModelWouldLoad(string name) => Fill(name);

    /// <summary>"The model would load another skill instead: {0}."</summary>
    public string SkillModelWouldLoadOther(string name) => Fill(name);

    public string SkillModelWouldNot => Text();

    public string SkillModelFailed => Text();

    public string SkillDraftTitle => Text();

    public string SkillDraftGuidance => Text();

    public string SkillDraftStart => Text();

    public string SkillDraftWorking => Text();

    public string SkillDraftReady => Text();

    public string SkillDraftNoChat => Text();

    public string SkillDraftNoModel => Text();

    public string SkillDraftFailed => Text();

    public string SkillDraftUnreadable => Text();

    public string SkillDraftBroken => Text();

    /// <summary>"This skill cannot be read: {0}" (the reason is a check's words).</summary>
    public string SkillBroken(string reason) => Fill(reason);

    /// <summary>A broken skill whose reason is not known.</summary>
    public string SkillBrokenNoReason => Text();

    /// <summary>The Try panel's heading for the skills that matched.</summary>
    public string SkillTestMatchesHeading => Text();

    /// <summary>"about {0:N0} of {1:N0} tokens": the instructions' size against the advisory limit.</summary>
    public string SkillTokens(int tokens, int limit) => Fill2(tokens, limit);

    /// <summary>In every state of a draft: nothing is saved until Save.</summary>
    public string SkillDraftNothingSaved => Text();

    public string SkillDraftOpen => Text();

    /// <summary>"Edit {0}": a row button's name for a screen reader.</summary>
    public string SkillEditNamed(string name) => Fill(name);

    public string SkillViewNamed(string name) => Fill(name);

    public string SkillDeleteNamed(string name) => Fill(name);

    public string SkillExportNamed(string name) => Fill(name);

    public string SkillDuplicateNamed(string name) => Fill(name);

    /// <summary>What a check found, in words: a rule broken, with the field or file, the limit and the size, and the detail that go with it.</summary>
    public string SkillIssueText(SkillIssueCode code, string subject, int limit, int actual, string detail) =>
        string.Format(FormatCulture, this["SkillIssue" + code.ToString()], subject, limit, actual, detail);

    /// <summary>How the pre-selection came to pick a skill: <c>explicit</c>, <c>always</c> or <c>trigger</c>.</summary>
    public string SkillMatchLabel(string matchedBy) => matchedBy switch
    {
        "explicit" => SkillMatchExplicit,
        "always" => SkillMatchAlways,
        "trigger" => SkillMatchTrigger,
        _ => matchedBy,
    };

    /// <summary>Where a skill comes from.</summary>
    public string SkillOriginLabel(SkillOrigin origin) => origin switch
    {
        SkillOrigin.Bundled => SkillOriginBundled,
        SkillOrigin.Own => SkillOriginOwn,
        _ => SkillOriginOrganisation,
    };

    /// <summary>The name of an activation: <c>auto</c>, <c>always</c> or <c>manual</c>.</summary>
    public string SkillActivationLabel(string activation) => activation switch
    {
        "always" => SkillActivationAlways,
        "manual" => SkillActivationManual,
        _ => SkillActivationAuto,
    };

    // -- pictures -------------------------------------------------------------------------------------

    /// <summary>The second line of a picture's chip.</summary>
    public string Picture => Text();

    /// <summary>A pasted picture's name.</summary>
    public string PastedPicture => Text();

    /// <summary>Said when a picture is added for an agent whose model does not see pictures.</summary>
    public string PicturesNotSeen => Text();

    public string PictureNotRead(string name) => Fill(name);

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

    /// <summary>About's line for an agent on the person's own model endpoint (no server): its address.</summary>
    public string AboutEndpoint(string address) => Fill(address);

    /// <summary>The organisation's own Help, on its server.</summary>
    public string Help => Text();

    /// <summary>The SDK documentation for developers.</summary>
    public string DeveloperDocs => Text();

    /// <summary>Copies what About shows, for a support request.</summary>
    public string CopyDetails => Text();

    /// <summary>"Help (opens in your browser)": a link's name for screen readers.</summary>
    public string OpensInBrowser(string link) => Fill(link);

    // -- the connection wizard (sdk/51 F15) --------------------------------------------------------------

    /// <summary>"Back"</summary>
    public string WizardBack => Text();

    /// <summary>"Continue"</summary>
    public string WizardContinue => Text();

    /// <summary>"Cancel"</summary>
    public string WizardCancel => Text();

    /// <summary>"Close"</summary>
    public string WizardClose => Text();

    /// <summary>"Try again"</summary>
    public string WizardTryAgain => Text();

    /// <summary>"Check"</summary>
    public string WizardCheck => Text();

    /// <summary>"How do you want to use {0}?"</summary>
    public string WizardChooseTitle(string app) => Fill(app);

    /// <summary>"Connect to my organisation"</summary>
    public string WizardChooseServer => Text();

    /// <summary>"Your organisation runs Fadenstack: sign in with your account."</summary>
    public string WizardChooseServerHelp => Text();

    /// <summary>"Use my own AI"</summary>
    public string WizardChooseOwnAi => Text();

    /// <summary>"Connect to an AI server you choose: on this computer, in your network or at a provider."</summary>
    public string WizardChooseOwnAiHelp => Text();

    /// <summary>"Try the demo"</summary>
    public string WizardChooseDemo => Text();

    /// <summary>"The settings of this computer allow none of these. Ask your IT."</summary>
    public string WizardNothingAllowed => Text();

    /// <summary>"Without this permission the app cannot go on."</summary>
    public string WizardAppStepRefused => Text();

    /// <summary>"The app could not save its settings. Try again."</summary>
    public string WizardStoreFailed => Text();

    /// <summary>"Connect to your organisation"</summary>
    public string WizardServerTitle => Text();

    /// <summary>"Server address"</summary>
    public string WizardServerLabel => Text();

    /// <summary>"https://ai.example.com"</summary>
    public string WizardServerPlaceholder => Text();

    /// <summary>"Your IT gives you this address."</summary>
    public string WizardServerHelp => Text();

    /// <summary>"Your organisation set this address."</summary>
    public string WizardServerLocked => Text();

    /// <summary>"Enter the server's address, for example https://ai.example.com."</summary>
    public string WizardServerAddressInvalid => Text();

    /// <summary>"Enter the address as https://… Plain http works only for this computer."</summary>
    public string WizardServerHttpsRequired => Text();

    /// <summary>"Checking the server…"</summary>
    public string WizardChecking => Text();

    /// <summary>"The server cannot be reached. Check the address and your network."</summary>
    public string WizardUnreachable => Text();

    /// <summary>WizardCertificateNotTrusted</summary>
    public string WizardCertificateNotTrusted => Text();

    /// <summary>"This address does not answer like a Fadenstack server. Check the address."</summary>
    public string WizardNotFadenstack => Text();

    /// <summary>"Connected to {0}. The app starts again so that you can sign in."</summary>
    public string WizardRestarting(string server) => Fill(server);

    /// <summary>"Change the server"</summary>
    public string WizardChangeServer => Text();

    /// <summary>"Server: {0}"</summary>
    public string WizardServerAt(string server) => Fill(server);

    /// <summary>"Asking the server for a code…"</summary>
    public string WizardStarting => Text();

    /// <summary>"Sign in to {0}"</summary>
    public string WizardCodeTitle(string app) => Fill(app);

    /// <summary>"Approve this computer in your browser. Your password stays in the browser."</summary>
    public string WizardCodeIntro => Text();

    /// <summary>"Your sign-in code"</summary>
    public string WizardCodeName => Text();

    /// <summary>"Copy the code"</summary>
    public string WizardCopyCode => Text();

    /// <summary>"Open the sign-in page"</summary>
    public string WizardOpenPage => Text();

    /// <summary>"Waiting for your approval… The code expires in {0}." ({0} as 9:41).</summary>
    public string WizardWaiting(TimeSpan left) => Fill(MinutesAndSeconds(left));

    /// <summary>"The server cannot be reached for the moment. Trying again… The code expires in {0}." ({0} as 9:41).</summary>
    public string WizardWaitingRetry(TimeSpan left) => Fill(MinutesAndSeconds(left));

    /// <summary>"The sign-in was denied in the browser."</summary>
    public string WizardDenied => Text();

    /// <summary>"The code expired."</summary>
    public string WizardExpired => Text();

    /// <summary>"Get a new code"</summary>
    public string WizardNewCode => Text();

    /// <summary>"This server does not support signing in with a code yet. Ask your IT to update Fadenstack."</summary>
    public string WizardTooOld => Text();

    /// <summary>"You can sign in with your e-mail and password instead."</summary>
    public string WizardTooOldPassword => Text();

    /// <summary>Why the first pairing request failed.</summary>
    public string WizardStartFailure(Wizard.StartFailure value) => this["WizardStartFailure_" + value];

    /// <summary>"The sign-in stopped. Try again."</summary>
    public string WizardPairFailed => Text();

    /// <summary>"Signing in…"</summary>
    public string WizardRedeeming => Text();

    /// <summary>"Signed in as {0}."</summary>
    public string WizardSignedInAs(string person) => Fill(person);

    /// <summary>"Signed in."</summary>
    public string WizardSignedIn => Text();

    /// <summary>"You have no access to this assistant. Ask your IT to give you access."</summary>
    public string WizardNoAccess => Text();

    /// <summary>"Your sign-in ended. Sign in again."</summary>
    public string WizardSignInEnded => Text();

    /// <summary>"Sign in with e-mail and password instead"</summary>
    public string WizardUsePassword => Text();

    /// <summary>"E-mail"</summary>
    public string WizardEmail => Text();

    /// <summary>"Password"</summary>
    public string WizardPassword => Text();

    /// <summary>"Sign in"</summary>
    public string WizardSignIn => Text();

    /// <summary>"Sign in with a code instead"</summary>
    public string WizardBackToCode => Text();

    /// <summary>Why the password sign-in failed.</summary>
    public string WizardPasswordFailure(Wizard.PasswordFailure value) => this["WizardPasswordFailure_" + value];

    /// <summary>"Use your own AI"</summary>
    public string WizardOwnTitle => Text();

    /// <summary>WizardOwnDataFlow</summary>
    public string WizardOwnDataFlow => Text();

    /// <summary>"The provider's terms are between you and the provider."</summary>
    public string WizardOwnProviderTerms => Text();

    /// <summary>The standalone notice (F15, owner 14 Nov): the report's draft without pictures (D17). Counsel reviews; German is binding.</summary>
    public string WizardOwnNotice => Text();

    /// <summary>The liability sentence (F15): counsel reviews; German is binding.</summary>
    public string WizardOwnLiability => Text();

    /// <summary>"The German text is the binding version."</summary>
    public string WizardOwnBindingVersion => Text();

    /// <summary>"Read the full terms"</summary>
    public string WizardOwnFullTerms => Text();

    /// <summary>"I have read this and accept the terms."</summary>
    public string WizardOwnConsent => Text();

    /// <summary>"Look for AI servers on this computer (Ollama, LM Studio, vLLM, llama.cpp)"</summary>
    public string WizardOwnSearchConsent => Text();

    /// <summary>"Accept and continue"</summary>
    public string WizardOwnAccept => Text();

    /// <summary>"Set up server mode instead"</summary>
    public string WizardOwnServerInstead => Text();

    /// <summary>"Looking for AI servers on this computer…"</summary>
    public string WizardOwnSearching => Text();

    /// <summary>"Choose your AI"</summary>
    public string WizardOwnPickTitle => Text();

    /// <summary>"Found on this computer"</summary>
    public string WizardOwnFound => Text();

    /// <summary>"No AI server was found on this computer."</summary>
    public string WizardOwnNothingFound => Text();

    /// <summary>"Providers and servers"</summary>
    public string WizardOwnPresets => Text();

    /// <summary>"Address"</summary>
    public string WizardOwnAddress => Text();

    /// <summary>"https://…/v1"</summary>
    public string WizardOwnAddressPlaceholder => Text();

    /// <summary>"This server needs a key"</summary>
    public string WizardOwnNeedsKey => Text();

    /// <summary>"Use this"</summary>
    public string WizardOwnUse => Text();

    /// <summary>"Enter an address that starts with https:// (or http:// for this computer)."</summary>
    public string WizardEndpointAddressInvalid => Text();

    /// <summary>WizardEndpointHttpsRequired</summary>
    public string WizardEndpointHttpsRequired => Text();

    /// <summary>"The settings of this computer do not allow this address. Ask your IT."</summary>
    public string WizardAddressNotAllowed => Text();

    /// <summary>"The settings of this computer do not allow plain http. Use the https address."</summary>
    public string WizardAddressPlainHttpForbidden => Text();

    /// <summary>"A key never goes over plain http to another computer. Use the https address."</summary>
    public string WizardAddressKeyNeedsHttps => Text();

    /// <summary>"Plain http in your local network"</summary>
    public string WizardPlainHttpTitle => Text();

    /// <summary>WizardPlainHttpWarning</summary>
    public string WizardPlainHttpWarning => Text();

    /// <summary>"Use it anyway"</summary>
    public string WizardPlainHttpConfirm => Text();

    /// <summary>"The key"</summary>
    public string WizardKeyTitle => Text();

    /// <summary>"API key"</summary>
    public string WizardKeyLabel => Text();

    /// <summary>"The key is kept in this computer's key store, never in a file."</summary>
    public string WizardKeyHelp => Text();

    /// <summary>"Checking the connection…"</summary>
    public string WizardOwnChecking => Text();

    /// <summary>What the endpoint check found, in plain words.</summary>
    public string WizardProblem(Fadenstack.Endpoints.EndpointProblem value) => this["WizardProblem_" + value];

    /// <summary>"Enter the key again"</summary>
    public string WizardEnterKey => Text();

    /// <summary>"How to allow it"</summary>
    public string WizardShowHelp => Text();

    /// <summary>"Allow this app"</summary>
    public string WizardOwnHelpTitle => Text();

    /// <summary>WizardOwnHelp</summary>
    public string WizardOwnHelp => Text();

    /// <summary>"The connection works."</summary>
    public string WizardOwnWorks => Text();

    /// <summary>Whether the connection watermarks text (F03 item 8).</summary>
    public string WizardWatermark(Fadenstack.Endpoints.TextWatermark value) => this["WizardWatermark_" + value];

    /// <summary>"The server confirmed its watermark."</summary>
    public string WizardWatermarkConfirmed => Text();

    /// <summary>"Choose the model"</summary>
    public string WizardModelTitle => Text();

    /// <summary>"Model"</summary>
    public string WizardModelLabel => Text();

    /// <summary>"Your AI is ready."</summary>
    public string WizardOwnDone => Text();

    /// <summary>"Connection"</summary>
    public string WizardSettingsTitle => Text();

    /// <summary>"Connected to {0}."</summary>
    public string WizardConnectedServer(string server) => Fill(server);

    /// <summary>"Your own AI: {0}."</summary>
    public string WizardConnectedEndpoint(string endpoint) => Fill(endpoint);

    /// <summary>"Connect to my organisation"</summary>
    public string WizardConnectOrganisation => Text();

    /// <summary>"Change the AI"</summary>
    public string WizardChangeEndpoint => Text();

    /// <summary>"Sign out"</summary>
    public string WizardSignOut => Text();

    /// <summary>"Remove this AI"</summary>
    public string WizardRemoveEndpoint => Text();

    /// <summary>"Sign out on this computer?"</summary>
    public string WizardSignOutTitle => Text();

    /// <summary>"The sign-in ends at the server and on this computer."</summary>
    public string WizardSignOutBody => Text();

    /// <summary>"Remove this AI from this computer?"</summary>
    public string WizardRemoveTitle => Text();

    /// <summary>"Its key is deleted from this computer's key store."</summary>
    public string WizardRemoveBody => Text();

    /// <summary>"Also delete the chats kept on this computer"</summary>
    public string WizardDeleteChats => Text();

    // -- the Tools view (sdk/51 F05) ---------------------------------------------------------------------

    /// <summary>
    /// The version of the warning "Plain http is not encrypted..." (<see cref="McpPlainHttpWarning"/>): the date its words were last changed. The
    /// app passes it with the person's confirmation, and the SDK records which warning they saw (the device's audit log). Change it
    /// whenever the warning's words change.
    /// </summary>
    public const string McpPlainHttpWarningVersion = "2026-11-15";

    /// <summary>"Tools"</summary>
    public string ToolsTitle => Text();

    /// <summary>"What your assistant can reach, and whether it is on."</summary>
    public string ToolsIntro => Text();

    /// <summary>"This assistant may not use MCP servers on this computer."</summary>
    public string ToolsBannerNotAllowed => Text();

    /// <summary>"Sign in to see the tools of this assistant."</summary>
    public string ToolsSignInNeeded => Text();

    /// <summary>"Allowed"</summary>
    public string ToolsPolicyAllowed => Text();

    /// <summary>"This assistant may use MCP servers on this computer."</summary>
    public string ToolsPolicyAllowedTip => Text();

    /// <summary>"Developer mode"</summary>
    public string ToolsPolicyDeveloper => Text();

    /// <summary>"Developer mode: any server, for you only."</summary>
    public string ToolsPolicyDeveloperTip => Text();

    /// <summary>"Not allowed for this assistant"</summary>
    public string ToolsPolicyNotAllowed => Text();

    /// <summary>"In this app · {0}"</summary>
    public string ToolsTabApp(int count) => Fill(count);

    /// <summary>"This app gives the assistant no tools of its own."</summary>
    public string ToolsAppNone => Text();

    /// <summary>"On this computer · {0}"</summary>
    public string ToolsTabLocal(int count) => Fill(count);

    /// <summary>"On your server · {0}"</summary>
    public string ToolsTabServer(int count) => Fill(count);

    /// <summary>"Filter tools"</summary>
    public string ToolsFilterPlaceholder => Text();

    /// <summary>"Add server"</summary>
    public string ToolsAddServer => Text();

    /// <summary>"Scan this computer"</summary>
    public string ToolsScanThisComputer => Text();

    /// <summary>"MCP servers"</summary>
    public string ToolsSectionServers => Text();

    /// <summary>"Programs on this computer that offer tools. The assistant looks them up when it needs them, and every call asks you first."</summary>
    public string ToolsSectionServersHelp => Text();

    /// <summary>"This computer has no key store, so your servers are kept only until you close the app."</summary>
    public string ToolsKeptInMemory => Text();

    /// <summary>"The saved list of servers could not be read. It was set aside and the list starts empty."</summary>
    public string ToolsListUnreadable => Text();

    /// <summary>"Your organisation’s server runs these tools; your data goes through its checks. Your administrator decides which ones this assistant may use."</summary>
    public string ToolsCloudIntro => Text();

    /// <summary>"This server does not list its tools yet."</summary>
    public string ToolsCloudNotListed => Text();

    /// <summary>"The server’s tools could not be loaded right now."</summary>
    public string ToolsCloudUnavailable => Text();

    /// <summary>"Your server offers no tools to this assistant."</summary>
    public string ToolsCloudNone => Text();

    /// <summary>"For this assistant"</summary>
    public string ToolsCloudForThis => Text();

    /// <summary>"Not for this assistant"</summary>
    public string ToolsCloudNotFor => Text();

    /// <summary>"Switched off by your administrator"</summary>
    public string ToolsCloudOffByAdmin => Text();

    /// <summary>"{0} for this assistant"</summary>
    public string ToolsForThisAssistant(int count) => Fill(count);

    /// <summary>"{0} switched off"</summary>
    public string ToolsSwitchedOffCount(int count) => Fill(count);

    /// <summary>"Collapse {0}"</summary>
    public string ToolsCollapse(string server) => Fill(server);

    /// <summary>"Expand {0}"</summary>
    public string ToolsExpand(string server) => Fill(server);

    /// <summary>"Try again"</summary>
    public string ToolsTryAgain => Text();

    /// <summary>"Loading..."</summary>
    public string ToolsLoading => Text();

    /// <summary>"Give your assistant tools from this computer"</summary>
    public string ToolsEmptyTitle => Text();

    /// <summary>"MCP servers are small programs that offer tools: your notes, a database, a calendar. Find the ones you already use in other apps, or add one yourself."</summary>
    public string ToolsEmptyText => Text();

    /// <summary>"Running"</summary>
    public string ToolsStateRunning => Text();

    /// <summary>"Off"</summary>
    public string ToolsStateOff => Text();

    /// <summary>"Starting"</summary>
    public string ToolsStateStarting => Text();

    /// <summary>"Can’t start: {0}"</summary>
    public string ToolsStateFailed(string reason) => Fill(reason);

    /// <summary>"Not allowed"</summary>
    public string ToolsStateNotAllowed => Text();

    /// <summary>"Changed its tools"</summary>
    public string ToolsStateChangedTools => Text();

    /// <summary>"Can’t start: {0} has no value"</summary>
    public string ToolsStateMissingValue(string name) => Fill(name);

    /// <summary>"Not allowed: {0}"</summary>
    public string ToolsStateNotAllowedBecause(string reason) => Fill(reason);

    /// <summary>"this assistant may not use MCP servers on this computer"</summary>
    public string ToolsReasonPolicy => Text();

    /// <summary>"no reason given"</summary>
    public string ToolsReasonUnknown => Text();

    /// <summary>"1 tool"</summary>
    public string ToolsCountOne => Text();

    /// <summary>"{0} tools"</summary>
    public string ToolsCountMany(int count) => Fill(count);

    /// <summary>"told 1 folder"</summary>
    public string ToolsToldFoldersOne => Text();

    /// <summary>"told {0} folders"</summary>
    public string ToolsToldFoldersMany(int count) => Fill(count);

    /// <summary>"+{0}"</summary>
    public string ToolsMoreTools(int count) => Fill(count);

    /// <summary>"Add the value"</summary>
    public string ToolsFixAddValue => Text();

    /// <summary>"Added by you"</summary>
    public string ToolsOriginTyped => Text();

    /// <summary>"Found by a scan"</summary>
    public string ToolsOriginScan => Text();

    /// <summary>"From {0}"</summary>
    public string ToolsOriginImported(string origin) => Fill(origin);

    /// <summary>"{0} · {1}"</summary>
    public string ToolsOriginLine(string origin, string where) => Fill2(origin, where);

    /// <summary>"Switch {0} on or off"</summary>
    public string ToolsSwitchName(string label) => Fill(label);

    /// <summary>"Show details of {0}"</summary>
    public string ToolsOpenDetails(string label) => Fill(label);

    /// <summary>"{0}, may delete"</summary>
    public string ToolsToolMayDelete(string name) => Fill(name);

    /// <summary>"{0}, MCP server"</summary>
    public string ToolsDrawerName(string label) => Fill(label);

    /// <summary>"Running since {0}"</summary>
    public string ToolsRunningSince(string time) => Fill(time);

    /// <summary>"Added"</summary>
    public string ToolsFactAdded => Text();

    /// <summary>"Runs"</summary>
    public string ToolsFactRuns => Text();

    /// <summary>"Address"</summary>
    public string ToolsFactAddress => Text();

    /// <summary>"Told folders"</summary>
    public string ToolsFactFolders => Text();

    /// <summary>"Secrets"</summary>
    public string ToolsFactSecrets => Text();

    /// <summary>"Longest call"</summary>
    public string ToolsFactLongestCall => Text();

    /// <summary>"By you"</summary>
    public string ToolsAddedByYou => Text();

    /// <summary>"From {0}"</summary>
    public string ToolsAddedFrom(string origin) => Fill(origin);

    /// <summary>"Found by a scan"</summary>
    public string ToolsAddedByScan => Text();

    /// <summary>"None"</summary>
    public string ToolsNoneText => Text();

    /// <summary>"None (no folders allowed)"</summary>
    public string ToolsNoFoldersTold => Text();

    /// <summary>"1 second"</summary>
    public string ToolsSecondsOne => Text();

    /// <summary>"{0} seconds"</summary>
    public string ToolsSecondsMany(int count) => Fill(count);

    /// <summary>"1 minute"</summary>
    public string ToolsMinutesOne => Text();

    /// <summary>"{0} minutes"</summary>
    public string ToolsMinutesMany(int count) => Fill(count);

    /// <summary>"The server describes its own tools. Nobody has checked them, so every call asks you first."</summary>
    public string ToolsDrawerNote => Text();

    /// <summary>"Tools · {0}"</summary>
    public string ToolsListHeading(int count) => Fill(count);

    /// <summary>"Filter"</summary>
    public string ToolsDrawerFilterPlaceholder => Text();

    /// <summary>"Filter the tools"</summary>
    public string ToolsDrawerFilterName => Text();

    /// <summary>"No tools yet."</summary>
    public string ToolsNoTools => Text();

    /// <summary>"No tool matches."</summary>
    public string ToolsNoToolsMatch => Text();

    /// <summary>"Says: reads only"</summary>
    public string ToolsSaysReadOnly => Text();

    /// <summary>"Says: may delete"</summary>
    public string ToolsSaysDestructive => Text();

    /// <summary>"Says: safe to repeat"</summary>
    public string ToolsSaysIdempotent => Text();

    /// <summary>"Says: reaches outside this computer"</summary>
    public string ToolsSaysOpenWorld => Text();

    /// <summary>"Test the server"</summary>
    public string ToolsTestTooltip => Text();

    /// <summary>"Restart"</summary>
    public string ToolsRestartTooltip => Text();

    /// <summary>"Edit"</summary>
    public string ToolsEditTooltip => Text();

    /// <summary>"Remove"</summary>
    public string ToolsRemoveTooltip => Text();

    /// <summary>"Restarted."</summary>
    public string ToolsRestartedLine => Text();

    /// <summary>"Remove {0}?"</summary>
    public string ToolsRemoveConfirm(string label) => Fill(label);

    /// <summary>"Remove"</summary>
    public string ToolsRemoveYes => Text();

    /// <summary>"Keep"</summary>
    public string ToolsRemoveKeep => Text();

    /// <summary>"Remove {0}"</summary>
    public string ToolsRemoveName(string label) => Fill(label);

    /// <summary>"Close"</summary>
    public string ToolsCloseDetails => Text();

    /// <summary>"Add a server"</summary>
    public string ToolsSheetAddTitle => Text();

    /// <summary>"Edit {0}"</summary>
    public string ToolsSheetEditTitle(string label) => Fill(label);

    /// <summary>"Name"</summary>
    public string ToolsFieldLabel => Text();

    /// <summary>"Lower case letters, digits and hyphens, up to 32."</summary>
    public string ToolsFieldLabelHelp => Text();

    /// <summary>"Program"</summary>
    public string ToolsKindProgram => Text();

    /// <summary>"Address"</summary>
    public string ToolsKindAddress => Text();

    /// <summary>"Program"</summary>
    public string ToolsFieldCommand => Text();

    /// <summary>"Arguments (one per line)"</summary>
    public string ToolsFieldArguments => Text();

    /// <summary>"Working folder (optional)"</summary>
    public string ToolsFieldFolder => Text();

    /// <summary>"Choose..."</summary>
    public string ToolsChooseFolder => Text();

    /// <summary>"Environment variables"</summary>
    public string ToolsFieldEnvironment => Text();

    /// <summary>"Headers"</summary>
    public string ToolsFieldHeaders => Text();

    /// <summary>"Add"</summary>
    public string ToolsAddRow => Text();

    /// <summary>"Name"</summary>
    public string ToolsRowName => Text();

    /// <summary>"Value"</summary>
    public string ToolsRowValue => Text();

    /// <summary>"••••••••"</summary>
    public string ToolsValueSaved => Text();

    /// <summary>"Replace"</summary>
    public string ToolsReplaceValue => Text();

    /// <summary>"Remove"</summary>
    public string ToolsRemoveRow => Text();

    /// <summary>"Address"</summary>
    public string ToolsFieldUrl => Text();

    /// <summary>"Longest call (seconds)"</summary>
    public string ToolsFieldTimeout => Text();

    /// <summary>"Switch on after saving"</summary>
    public string ToolsFieldEnabled => Text();

    /// <summary>"Values are kept encrypted on this computer, are never shown again and are never sent anywhere else."</summary>
    public string ToolsSecretsNote => Text();

    /// <summary>"Save"</summary>
    public string ToolsSave => Text();

    /// <summary>"Cancel"</summary>
    public string ToolsCancel => Text();

    /// <summary>"Close"</summary>
    public string ToolsCloseSheet => Text();

    /// <summary>"Kind of server"</summary>
    public string ToolsKindName => Text();

    /// <summary>"Add"</summary>
    public string ToolsAddButton => Text();

    /// <summary>"Test"</summary>
    public string ToolsTestButton => Text();

    /// <summary>"Saved value (hidden)"</summary>
    public string ToolsValueSavedName => Text();

    /// <summary>"Import from a file…"</summary>
    public string ToolsImportMore => Text();

    /// <summary>"Give the server a name."</summary>
    public string ToolsProblemLabelMissing => Text();

    /// <summary>"Use lower case letters, digits and hyphens, up to 32."</summary>
    public string ToolsProblemLabelInvalid => Text();

    /// <summary>"You have a server with that name already."</summary>
    public string ToolsProblemLabelTaken => Text();

    /// <summary>"Enter the program to start."</summary>
    public string ToolsProblemCommandMissing => Text();

    /// <summary>"The program name cannot contain line breaks."</summary>
    public string ToolsProblemCommandInvalid => Text();

    /// <summary>"The working folder must be a full path."</summary>
    public string ToolsProblemFolder => Text();

    /// <summary>"A name may contain only letters, digits and underscores (headers: also hyphens)."</summary>
    public string ToolsProblemName => Text();

    /// <summary>"A value cannot contain line breaks."</summary>
    public string ToolsProblemValue => Text();

    /// <summary>"Enter the address."</summary>
    public string ToolsProblemAddressMissing => Text();

    /// <summary>"That is not an address (for example http://localhost:8123/mcp)."</summary>
    public string ToolsProblemAddressInvalid => Text();

    /// <summary>"Enter a number of seconds from 5 to 600."</summary>
    public string ToolsProblemTimeout => Text();

    /// <summary>"Testing..."</summary>
    public string ToolsTestRunning => Text();

    /// <summary>"Started. Tools: {0}"</summary>
    public string ToolsTestOk(string tools) => Fill(tools);

    /// <summary>"Started, but it offers no tools."</summary>
    public string ToolsTestOkNoTools => Text();

    /// <summary>"Could not start: {0}"</summary>
    public string ToolsTestFailed(string reason) => Fill(reason);

    /// <summary>"Not started: this assistant may not use MCP servers on this computer."</summary>
    public string ToolsTestNotAllowed => Text();

    /// <summary>"Import servers"</summary>
    public string ToolsImportTitle => Text();

    /// <summary>"Choose where to import from. Nothing is copied until you tick servers and press Import."</summary>
    public string ToolsImportIntro => Text();

    /// <summary>"Claude Desktop"</summary>
    public string ToolsSourceClaude => Text();

    /// <summary>"VS Code"</summary>
    public string ToolsSourceVsCode => Text();

    /// <summary>"Cursor"</summary>
    public string ToolsSourceCursor => Text();

    /// <summary>"A .mcp.json file..."</summary>
    public string ToolsSourceFile => Text();

    /// <summary>"Choose a .mcp.json file"</summary>
    public string ToolsChooseFileTitle => Text();

    /// <summary>"Read {0}"</summary>
    public string ToolsImportRead(string file) => Fill(file);

    /// <summary>"That file was not found on this computer."</summary>
    public string ToolsImportProblemNotFound => Text();

    /// <summary>"That file cannot be read."</summary>
    public string ToolsImportProblemUnreadable => Text();

    /// <summary>"That file has no list of servers."</summary>
    public string ToolsImportProblemNoList => Text();

    /// <summary>"That file is too large."</summary>
    public string ToolsImportProblemTooLarge => Text();

    /// <summary>"Nothing in that file can be imported."</summary>
    public string ToolsImportNothingToAdd => Text();

    /// <summary>"Import"</summary>
    public string ToolsImportButton => Text();

    /// <summary>"Import {0}"</summary>
    public string ToolsImportTick(string label) => Fill(label);

    /// <summary>"Needs a value you must fill in; it is added switched off."</summary>
    public string ToolsImportNoteNeedsValue => Text();

    /// <summary>"Contains placeholders such as ${env:NAME}, which are kept as written."</summary>
    public string ToolsImportNotePlaceholders => Text();

    /// <summary>"Left out: {0}"</summary>
    public string ToolsImportNoteIgnored(string fields) => Fill(fields);

    /// <summary>"Renamed to fit the rules for names."</summary>
    public string ToolsImportNoteRenamed => Text();

    /// <summary>"It was switched off there."</summary>
    public string ToolsImportNoteWasOff => Text();

    /// <summary>"Skipped"</summary>
    public string ToolsSkippedTitle => Text();

    /// <summary>"It has no program or address."</summary>
    public string ToolsSkipNothing => Text();

    /// <summary>"It was not understood."</summary>
    public string ToolsSkipNotUnderstood => Text();

    /// <summary>"It breaks a rule: {0}"</summary>
    public string ToolsSkipNotValid(string detail) => Fill(detail);

    /// <summary>"Added: {0}"</summary>
    public string ToolsImported(int count) => Fill(count);

    /// <summary>"Scan this computer"</summary>
    public string ToolsScanTitle => Text();

    /// <summary>"Finds MCP servers you already use in other apps, and servers running on this computer."</summary>
    public string ToolsScanIntro => Text();

    /// <summary>"Other apps’ settings"</summary>
    public string ToolsScanOtherApps => Text();

    /// <summary>"{0} · {1}"</summary>
    public string ToolsScanChip(string name, int found) => Fill2(name, found);

    /// <summary>"{0}: not installed"</summary>
    public string ToolsScanNotInstalled(string names) => Fill(names);

    /// <summary>"Servers running on this computer"</summary>
    public string ToolsScanThisComputerOption => Text();

    /// <summary>"localhost, ports {0}. It only says hello in MCP; nothing is sent."</summary>
    public string ToolsScanPorts(string ranges) => Fill(ranges);

    /// <summary>"{0}–{1}"</summary>
    public string ToolsScanPortRange(int from, int to) => Fill2(from, to);

    /// <summary>"and"</summary>
    public string ToolsScanAnd => Text();

    /// <summary>"Scan progress"</summary>
    public string ToolsScanProgressName => Text();

    /// <summary>"Looking… {0} of {1} done"</summary>
    public string ToolsScanRunning(int done, int total) => Fill2(done, total);

    /// <summary>"Looking…"</summary>
    public string ToolsScanStarting => Text();

    /// <summary>"Done in {0} s · {1}"</summary>
    public string ToolsScanDone(int seconds, string found) => Fill2(seconds, found);

    /// <summary>"1 server found"</summary>
    public string ToolsScanFoundOne => Text();

    /// <summary>"{0} servers found"</summary>
    public string ToolsScanFoundMany(int count) => Fill(count);

    /// <summary>"Nothing found"</summary>
    public string ToolsScanFoundNone => Text();

    /// <summary>"Scan again"</summary>
    public string ToolsScanAgain => Text();

    /// <summary>"Stopped."</summary>
    public string ToolsScanStopped => Text();

    /// <summary>"The scan could not finish. Try again."</summary>
    public string ToolsScanFailed => Text();

    /// <summary>"No MCP servers were found. You can add one yourself."</summary>
    public string ToolsScanNothingFoundHelp => Text();

    /// <summary>"Tick at least one place to look in."</summary>
    public string ToolsScanNothingToLookAt => Text();

    /// <summary>"Found in {0}"</summary>
    public string ToolsScanFoundIn(string app) => Fill(app);

    /// <summary>"Running on this computer"</summary>
    public string ToolsScanRunningHere => Text();

    /// <summary>"Program"</summary>
    public string ToolsScanKindProgram => Text();

    /// <summary>"Address"</summary>
    public string ToolsScanKindAddress => Text();

    /// <summary>"Already added"</summary>
    public string ToolsScanStateAlready => Text();

    /// <summary>"Needs a value; added switched off"</summary>
    public string ToolsScanStateNeedsValue => Text();

    /// <summary>"Not understood"</summary>
    public string ToolsScanStateNotUnderstood => Text();

    /// <summary>"Takes {0} from {1}’s settings; kept encrypted here"</summary>
    public string ToolsScanTakes(string names, string app) => Fill2(names, app);

    /// <summary>"Answered as {0}"</summary>
    public string ToolsScanAnswered(string name) => Fill(name);

    /// <summary>"Answered as {0} · {1}"</summary>
    public string ToolsScanAnsweredTools(string name, string tools) => Fill2(name, tools);

    /// <summary>"Add {0}"</summary>
    public string ToolsScanTick(string label) => Fill(label);

    /// <summary>"{0}, already added"</summary>
    public string ToolsScanTickAlready(string label) => Fill(label);

    /// <summary>"Nothing leaves this computer. A server starts only after you add it, and only where this assistant may use MCP servers."</summary>
    public string ToolsScanFooter => Text();

    /// <summary>"Add 1 server"</summary>
    public string ToolsScanAddOne => Text();

    /// <summary>"Add {0} servers"</summary>
    public string ToolsScanAddMany(int count) => Fill(count);

    /// <summary>"Close"</summary>
    public string ToolsScanClose => Text();

    /// <summary>"Where to look"</summary>
    public string ToolsScanOptionsName => Text();

    /// <summary>"A server on another computer is not imported. Add it yourself if you need it."</summary>
    public string ToolsSkipNetwork => Text();

    /// <summary>"On another computer: add it yourself"</summary>
    public string ToolsScanStateLater => Text();

    /// <summary>"{0}, not added by a scan"</summary>
    public string ToolsScanTickLater(string label) => Fill(label);

    /// <summary>"Use https for a server anywhere. Plain http works on this computer, and on your local network when you allow it under Advanced."</summary>
    public string ToolsFieldUrlHelp => Text();

    /// <summary>"Headers hold secrets, so a plain http address beyond this computer cannot have any. Use https or remove the headers."</summary>
    public string ToolsProblemPlainHttpWithHeaders => Text();

    /// <summary>"Plain http goes only to this computer or the local network. Use https for an address elsewhere."</summary>
    public string ToolsProblemPlainHttpNotLocal => Text();

    /// <summary>"Trusted"</summary>
    public string ToolsTrusted => Text();

    /// <summary>"Only a trusted server’s “reads only” hints count. A server’s own word is never enough."</summary>
    public string ToolsTrustedHelp => Text();

    /// <summary>"Marked by your administrator"</summary>
    public string ToolsTrustedByAdmin => Text();

    /// <summary>"Trust {0}"</summary>
    public string ToolsTrustedName(string label) => Fill(label);

    /// <summary>"Asks every time"</summary>
    public string ToolsClassUnknown => Text();

    /// <summary>"Reads"</summary>
    public string ToolsClassRead => Text();

    /// <summary>"Changes things"</summary>
    public string ToolsClassWrite => Text();

    /// <summary>"Destructive"</summary>
    public string ToolsClassDestructive => Text();

    /// <summary>"Treat as"</summary>
    public string ToolsRaiseLabel => Text();

    /// <summary>"As it arrives"</summary>
    public string ToolsRaiseNone => Text();

    /// <summary>"Reads only"</summary>
    public string ToolsRaiseRead => Text();

    /// <summary>"Changes things"</summary>
    public string ToolsRaiseWrite => Text();

    /// <summary>"Class of {0}"</summary>
    public string ToolsRaiseName(string tool) => Fill(tool);

    /// <summary>"The server says this tool may delete or overwrite. It always asks, and nothing can lower that."</summary>
    public string ToolsDestructiveHelp => Text();

    /// <summary>"Advanced"</summary>
    public string ToolsAdvanced => Text();

    /// <summary>"Allow plain http on the local network"</summary>
    public string McpPlainHttpSwitch => Text();

    /// <summary>"For a server on your own network that has no https. Never with a header value."</summary>
    public string McpPlainHttpSwitchHelp => Text();

    /// <summary>"Allow plain http?"</summary>
    public string McpPlainHttpWarningTitle => Text();

    /// <summary>"Plain http is not encrypted. Anyone on your network can read and change what goes to and from this server: the tool calls, their arguments and their results, which may hold document content. A changed answer can steer the agent. Use https where the server offers it."</summary>
    public string McpPlainHttpWarning => Text();

    /// <summary>"Allow"</summary>
    public string McpPlainHttpConfirm => Text();

    /// <summary>"Keep it off"</summary>
    public string McpPlainHttpCancel => Text();

    /// <summary>"Plain http goes only to this computer. Turn on “Allow plain http on the local network” under Advanced."</summary>
    public string McpPlainHttpNoOptIn => Text();

    /// <summary>"This address is not on the local network."</summary>
    public string McpPlainHttpNotLocal => Text();

    /// <summary>"A server with a header value needs https."</summary>
    public string McpPlainHttpSecret => Text();

    /// <summary>"Plain http is not allowed here."</summary>
    public string McpPlainHttpNotAllowed => Text();

    /// <summary>"Web search"</summary>
    public string WebSearchRowName => Text();

    /// <summary>"Lets the assistant search the web. Off until you set it up."</summary>
    public string WebSearchRowText => Text();

    /// <summary>"Off"</summary>
    public string WebSearchStateOff => Text();

    /// <summary>"Brave"</summary>
    public string WebSearchStateBrave => Text();

    /// <summary>"SearXNG · {0}"</summary>
    public string WebSearchStateSearxng(string host) => Fill(host);

    /// <summary>"Set up"</summary>
    public string WebSearchSetUp => Text();

    /// <summary>"Change"</summary>
    public string WebSearchChange => Text();

    /// <summary>"Web search"</summary>
    public string WebSearchTitle => Text();

    /// <summary>"Choose where the assistant’s web searches go."</summary>
    public string WebSearchIntro => Text();

    /// <summary>"Search provider"</summary>
    public string WebSearchChoiceName => Text();

    /// <summary>"Off"</summary>
    public string WebSearchChoiceOff => Text();

    /// <summary>"Brave Search"</summary>
    public string WebSearchChoiceBrave => Text();

    /// <summary>"SearXNG"</summary>
    public string WebSearchChoiceSearxng => Text();

    /// <summary>"The assistant has no web search tool."</summary>
    public string WebSearchOffHelp => Text();

    /// <summary>"Brave API key"</summary>
    public string WebSearchKeyLabel => Text();

    /// <summary>"Use your own key from your Brave Search API account."</summary>
    public string WebSearchKeyHelp => Text();

    /// <summary>"A key is saved"</summary>
    public string WebSearchKeyKept => Text();

    /// <summary>"Replace key"</summary>
    public string WebSearchKeyReplace => Text();

    /// <summary>"Remove key"</summary>
    public string WebSearchKeyRemove => Text();

    /// <summary>"The key will be removed when you save."</summary>
    public string WebSearchKeyRemoved => Text();

    /// <summary>"Enter your Brave API key, or pick another choice."</summary>
    public string WebSearchKeyMissing => Text();

    /// <summary>"SearXNG address"</summary>
    public string WebSearchUrlLabel => Text();

    /// <summary>"The address of your SearXNG instance, with its JSON format turned on."</summary>
    public string WebSearchUrlHelp => Text();

    /// <summary>"Enter the address of your SearXNG instance."</summary>
    public string WebSearchUrlMissing => Text();

    /// <summary>"Use an https address, or http on this computer only, without a user name or password."</summary>
    public string WebSearchUrlRefused => Text();

    /// <summary>"The key is kept encrypted in this device’s key store and is never shown again."</summary>
    public string WebSearchKeptStore => Text();

    /// <summary>"The key is kept for this session only: this device has no key store, so it is forgotten when the app closes."</summary>
    public string WebSearchKeptSession => Text();

    /// <summary>"A search sends the words of the query outside your organisation. The assistant asks first, unless it is set to Auto."</summary>
    public string WebSearchPrivacy => Text();

    /// <summary>"Save"</summary>
    public string WebSearchSave => Text();

    /// <summary>"Cancel"</summary>
    public string WebSearchCancel => Text();

    /// <summary>"Saved."</summary>
    public string WebSearchSaved => Text();

    /// <summary>"The settings could not be saved on this device. Try again."</summary>
    public string WebSearchSaveFailed => Text();

    // -- the old-chats setting ----------------------------------------------------------------------

    /// <summary>"Old chats"</summary>
    public string ChatRetentionTitle => Text();

    /// <summary>"Delete old chats automatically"</summary>
    public string ChatRetentionAutoDelete => Text();

    /// <summary>"When this is off, chats stay on this device until you delete them."</summary>
    public string ChatRetentionAutoDeleteHint => Text();

    /// <summary>"Delete chats older than (days)"</summary>
    public string ChatRetentionMaxAge => Text();

    /// <summary>"Keep at most (chats)"</summary>
    public string ChatRetentionMaxSessions => Text();

    /// <summary>"Days after which old chats are deleted"</summary>
    public string ChatRetentionMaxAgeName => Text();

    /// <summary>"Number of chats to keep"</summary>
    public string ChatRetentionMaxSessionsName => Text();

    /// <summary>"Enter a whole number of days from {0} to {1}."</summary>
    public string ChatRetentionDaysError(int first, int second) => Fill2(first, second);

    /// <summary>"Enter a whole number of chats from {0} to {1}."</summary>
    public string ChatRetentionChatsError(int first, int second) => Fill2(first, second);

    /// <summary>"Save"</summary>
    public string ChatRetentionSave => Text();

    /// <summary>"Cancel"</summary>
    public string ChatRetentionCancel => Text();

    /// <summary>"Save the setting for old chats"</summary>
    public string ChatRetentionSaveName => Text();

    /// <summary>"Cancel changes to the setting for old chats"</summary>
    public string ChatRetentionCancelName => Text();

    /// <summary>"Saved."</summary>
    public string ChatRetentionSaved => Text();

    /// <summary>"Your organisation keeps at most {0} chats for {1} days on this device."</summary>
    public string ChatRetentionOrganisationLimit(int first, int second) => Fill2(first, second);

    /// <summary>"This device keeps at most {0} chats for {1} days." (governed, the server sets no limit: the app's limits)</summary>
    public string ChatRetentionDeviceLimit(int first, int second) => Fill2(first, second);

    /// <summary>"Set by your organisation's policy on this computer."</summary>
    public string ChatRetentionPolicyFixed => Text();

    // -- "Your data on this device" ---------------------------------------------------------------

    /// <summary>"Your data on this device"</summary>
    public string MyDataTitle => Text();

    /// <summary>"Fadenstack apps keep your chats, approvals and settings on this device. You can save a copy or remove them here."</summary>
    public string MyDataIntro => Text();

    /// <summary>"Export my data…"</summary>
    public string MyDataExport => Text();

    /// <summary>"Export my data… (this app only)"</summary>
    public string MyDataExportName => Text();

    /// <summary>"Export all…"</summary>
    public string MyDataExportAll => Text();

    /// <summary>"Export all… (every Fadenstack app on this device)"</summary>
    public string MyDataExportAllName => Text();

    /// <summary>"Remove my data…"</summary>
    public string MyDataRemove => Text();

    /// <summary>"Remove my data… (this app only)"</summary>
    public string MyDataRemoveName => Text();

    /// <summary>"Remove all my Fadenstack data on this device…"</summary>
    public string MyDataRemoveAll => Text();

    /// <summary>"This removes this app's chats, approvals and preferences on this device. Your shared data (skills, the MCP list, keys and provenance records) stays, because other Fadenstack apps use it: {0}."</summary>
    public string MyDataConfirmThisAppShared(string value) => Fill(value);

    /// <summary>"No other Fadenstack app uses your data on this device, so everything of yours here is removed: chats, approvals, preferences, skills, the MCP list, keys and provenance records."</summary>
    public string MyDataConfirmThisAppEverything => Text();

    /// <summary>"This removes all your Fadenstack data on this device, shared data included. These apps are affected:"</summary>
    public string MyDataConfirmAll => Text();

    /// <summary>"This cannot be undone."</summary>
    public string MyDataConfirmUndo => Text();

    /// <summary>"{0} (this app)"</summary>
    public string MyDataThisApp(string value) => Fill(value);

    /// <summary>"Remove my data"</summary>
    public string MyDataConfirmRemove => Text();

    /// <summary>"Remove everything"</summary>
    public string MyDataConfirmRemoveAll => Text();

    /// <summary>"Cancel"</summary>
    public string MyDataCancel => Text();

    /// <summary>"OK"</summary>
    public string MyDataDismiss => Text();

    /// <summary>"Please wait…"</summary>
    public string MyDataBusy => Text();

    /// <summary>"Removed everything."</summary>
    public string MyDataRemovedEverything => Text();

    /// <summary>"Removed. Shared data kept for: {0}."</summary>
    public string MyDataRemovedKept(string value) => Fill(value);

    /// <summary>"Data of these agents stays, because another app uses them too: {0}."</summary>
    public string MyDataAgentsKept(string value) => Fill(value);

    /// <summary>"Chats or approvals that this app keeps in a store of its own were not removed here."</summary>
    public string MyDataHostStores => Text();

    /// <summary>"Your data was saved to the file you chose."</summary>
    public string MyDataExported => Text();

    /// <summary>"The apps that use your data could not be listed. Nothing was removed. Try again."</summary>
    public string MyDataErrorRead => Text();

    /// <summary>"Your data could not be removed completely. Try again."</summary>
    public string MyDataErrorRemove => Text();

    /// <summary>"The export did not work. Try again."</summary>
    public string MyDataErrorExport => Text();

    /// <summary>", "</summary>
    public string MyDataListSeparator => Text();

    private static string MinutesAndSeconds(TimeSpan left)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        return $"{seconds / 60}:{seconds % 60:00}";
    }

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
