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

    public string Allow => Text();

    public string AllowForChat => Text();

    public string AllowForTime => Text();

    public string AllowMore => Text();

    public string AllowAlways => Text();

    public string Deny => Text();

    public string Allowed => Text();

    public string AllowedForChat => Text();

    public string AllowedForTime => Text();

    public string AllowedAlways => Text();

    public string Denied => Text();

    public string NoAnswer => Text();

    public string ResetApprovals => Text();

    public string ResetApprovalsHint => Text();

    // -- notices -------------------------------------------------------------------------------------

    /// <summary>A notice's title for the server's outcome (<c>prompt_blocked</c>, ...); null for one there is no text for.</summary>
    public string? NoticeTitle(string outcome) => Find("Notice_" + outcome);

    public string NoticeFallback => Text();

    public string TryAgainIn(double seconds) => Fill(seconds);

    public string Decision(string id) => Fill(id);

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

    public string ToolModeMenu => Text();

    /// <summary>A level of thinking. In a two-level Off / On switch every level but Off reads "On".</summary>
    public string ReasoningLabel(ReasoningLevel level, bool asSwitch = false) =>
        this[$"Reasoning_{(asSwitch && level != ReasoningLevel.Off ? "On" : level.ToString())}_Label"];

    /// <summary>What the level means, in one line.</summary>
    public string ReasoningDescription(ReasoningLevel level, bool asSwitch = false) =>
        this[$"Reasoning_{(asSwitch && level != ReasoningLevel.Off ? "On" : level.ToString())}_Description"];

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
