# Chat panel

This page covers the ready chat UI: the view models in `Fadenstack.UI`, the WPF skin in `Fadenstack.UI.Wpf`, the Avalonia skin in `Fadenstack.UI.Avalonia`, what the panel shows, its languages and settings, hosting it in add-ins, and extending how answers are rendered.

## The packages

The UI is split into a layer of view models and a skin that draws them, and the split is strict:

- **`Fadenstack.UI`** holds all of the behaviour, as view models without platform references: the chat (`AgentChatViewModel`), its turns, the parts of each answer, the tool-mode picker, the session history, every text the panel shows (`UiStrings`, from the package's resources), and the `IUiDispatcher` and `IClipboard` services a skin implements. It targets `netstandard2.0` and `net8.0`, and it runs headless in unit tests.
- **`Fadenstack.UI.Wpf`** only renders. It is XAML templates, styles, themes and a few view-only behaviours, such as keeping the conversation scrolled to the bottom and moving the focus to the composer. It targets `net472` and `net8.0-windows`.
- **`Fadenstack.UI.Avalonia`** renders the same view models with Avalonia 12, for desktop applications that run on Windows, macOS and Linux. It targets `net8.0`. See [The Avalonia skin](#the-avalonia-skin).

Another skin can bind to the same view models.

## Show the panel

```csharp
using Fadenstack.Agents;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Fadenstack.UI.Wpf.Services;
using Fadenstack.UI.Wpf.Views;

var approvals = new ApprovalBroker();          // approval cards in the chat decide
var agent = FadenstackAgent.CreateBuilder()
    .UseClient(client)
    .UseAgent("docs-assistant")
    .AddTools(new PageTools())
    .UseApprovals(approvals)
    .Build();

var chat = new AgentChatViewModel(agent, approvals, new WpfDispatcher(),
    new AgentChatOptions { Clipboard = new WpfClipboard() });
panel.Content = new ChatView { DataContext = chat, Theme = ChatTheme.System };
await chat.InitializeAsync();
```

What each piece does:

- `ApprovalBroker` is the agent's approval handler for a UI: each request becomes an approval card in the chat, and the user's click is the decision. Give the same broker to the builder and to the view model. When no chat is listening, it declines. Several chats can share one broker: every request carries the session and the turn it belongs to (`ApprovalRequest.SessionId`, `TurnId`), and a chat ignores the requests of other sessions. A request with no session is shown by every chat.
- `WpfDispatcher` is the WPF UI thread. Create it on the UI thread. It runs the chat's updates at background priority, so typing always comes first.
- `WpfClipboard` backs the copy buttons.
- `InitializeAsync` starts the agent and reads the session list. Call it on the UI thread.

When the agent needs a sign-in, `InitializeAsync` sets `SignInRequired` and `Error`. Show your sign-in, then call `InitializeAsync` again. Other errors appear in `Error`, which the panel shows with a button to try again.

The panel can also be placed in XAML; set its `DataContext` to the view model:

```xml
<Window x:Class="MyApp.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="clr-namespace:Fadenstack.UI.Wpf.Views;assembly=Fadenstack.UI.Wpf">
    <views:ChatView Theme="System" />
</Window>
```

When the panel closes, dispose the view model and then the agent.

## What the panel shows

- **The header** shows the agent's name, the tool-mode picker with a line on what the picked mode means, the list of chats on this device, a button for a new chat, and the settings (the gear). If the user may not pick a mode, the picker shows it greyed out.
- **An empty chat** shows the agent's greeting and suggested prompts, from the agent's profile (`ui.greeting`, `ui.suggestions`). The definition may carry them per language (`ui.languages.<tag>.greeting` and `.suggestions`; `de` also serves `de-AT`): the panel shows the ones for its language, and others when the user picks another.
- **The answer streams as Markdown**: headings, lists, code blocks with a copy button, tables, quotes, rules and links. Text and tool calls appear in the order they happened.
- **Quotes and list items hold blocks**, three levels deep: a table, a list or a code block inside a quote or an item is drawn as one. A quote, a list item or a Markdown preview inside three others does not nest further: the content of a container at the third level is one block of plain text, with its markup as typed. A thousand `>` cost three levels, and the text is kept, not dropped. The cap is `MarkdownParser.MaxNestingDepth` (3), so parsing is bounded too.
- **A `markdown` (or `md`) fence is a preview**: its content drawn, with a *Preview* and *Source* switch above it. It counts as one level of nesting. In the view model it is a `PreviewBlock` (`Source` is the fence as a `CodeBlock`, `Blocks` its content). A `mermaid` fence is a `CodeBlock` whose `IsDiagram` is true, labelled *Diagram · Mermaid*. It is shown as text and not drawn: drawing a diagram is up to the host.
- **Each tool call is one line**: "Running read_page" while it runs, then "Ran read_page" with its class and duration. It expands to its arguments and result.
- **Approvals are cards** in the answer, above the call they are for, with *Allow* and *Deny*. Where the request offers more, *Allow* is a split button: its main part allows this call, its drop-down lists *Allow for this chat* and *Allow always*: never for destructive or unclassified tools, nor for a call an administrator's rule covers; for a tool that reaches outside the organisation, below Auto, *Allow for this chat* only ([Open-world tools](tools.md#open-world-tools)). The buttons sit in one row when they fit; in a narrow pane they go two by two, or one under the other, never one alone on a row. Destructive and unclassified tools are marked. A tool that says what a call acts on (`AgentTool.Describe`) has it on the card, above the arguments. The title has a row of its own, with the tool's name in code ("Run `allowed_folders`?"). Under it, a tool from a local MCP server says where it comes from ("From Files, an MCP server on this computer") and that the organisation has not checked it, with the server's own hint when it gave one ("The server says it only reads." or "The server says it may delete or overwrite."; if it says both, the warning wins). A chat opened again draws the same cards, because the transcript keeps the tool's class and the MCP server's name. In the view model these are `ApprovalPart.TitleInlines`, `SourceLine`, `HasSourceLine` and `Reason`. A call that an earlier approval lets through gets no card, also when a session is opened again. A card for a tool that reaches outside the organisation (`OpenWorld`, such as a web search) says that what it is given leaves the organisation; a destructive one still says it cannot be undone.
- **On a server that holds grants**, a card for a read or write tool also offers *Allow for…* in that drop-down, a sub-menu with the times the agent allows (15 min up to its longest grant); the card then says *Allowed for 30 min*. An app that knows where the agent will work offers a **grant card** with `session.OfferWorkAsync(places)`: "Work alongside you?", the places (each can be taken out), the time (30 min to start), what the agent may do without asking and what still asks, *Allow* and *Not now*. It shows above the composer (`Grants.Cards`). See [Grants](tools.md#grants-for-a-time-and-working-alongside).
- **While a stretch of work runs**, a band under the header says "Working alongside you · 23 min left", counted by the server's clock, with *Stop*, which ends every grant of the agent on this device and the running turn. Two minutes before the end it also offers *Another 30 min*, within the agent's longest grant (`Grants.HasMarker`, `StopCommand`, `KeepGoingCommand`).
- **Bypass**, where the server offers it (`agent.BypassOffered`), is the picker's last entry. Picking it shows the **confirmation** above the composer (`Grants.BypassCard`): what will no longer be asked in this chat, what still asks, what the server records of each call (as the organisation set it), the time (the shortest first), that it cannot be extended, and an optional reason that administrators see. *Not now* comes first; *Switch Bypass on* is never the default button, *Enter* and *Esc* never switch it on, and only the person's click does. The card then waits for the server: without its confirmation it says why (offline, not allowed, limits not readable) and the chat stays in its mode. A chat without a conversation yet gets one, which goes again when the card is declined (`StartBypassAsync`).
- **While the chat runs in Bypass**, a band under the header says "Bypass on · 12 min left" and what is recorded, with *Stop*, which ends Bypass and the running turn (`Grants.HasBypass`, `BypassText`, `BypassRecordedText`, `StopBypassCommand`). It never offers more time. When Bypass ends -- its time, *Stop*, *Revoke* in the settings, an administrator, the organisation turning it off, a narrower limit -- the band goes, the chat goes back to the mode it had before, and a line says why (`HasBypassEnded`, `BypassEndedText`). Leaving the chat for another, or a new one, ends its Bypass; a chat opened again starts without it.
- **The tool-mode picker** says whose limit a greyed-out level is: the organisation's, or the agent's own highest level.
- **The thinking pill** sits beside the tool mode. The person picks how long the agent thinks there: *Auto*, *Off*, *Low*, *Medium* or *High*, up to the maximum the server sets. See [How long it thinks](agent.md#how-long-it-thinks-the-persons-choice). In the view model: `ReasoningOptions`, `SelectedReasoning`, `SelectReasoningCommand` and `ReasoningPillText`.
- **The tool-mode menu** ends with *Reset approvals*, which forgets what the user allowed always for this agent on this device and what they allowed for the current chat (`ResetApprovalsCommand`).
- **Refusals and limits are notices** in the answer. A refused prompt is marked *Not sent*, with *Edit and resend*. A notice shows the server's decision id for support.
- **Citations** ("Used 3 references") and **the model's reasoning** ("Thinking...", then how long it thought) are collapsed until the user opens them. Personal data the server kept from the model is reported as a notice.
- **Gateway events the panel does not know** appear as generic notices.
- **Under each answer** are its token count, its duration, a copy button, and a retry button for an answer that failed or was stopped.
- **The composer** shows attached context as chips and the current tool mode. *Enter* sends, *Shift+Enter* starts a new line, *Esc* stops the running turn, and *Ctrl+N* starts a new chat.
- **Under the composer** the panel says, in the user's language, that the answers are generated by AI and can be wrong (`UiStrings.AiNotice`). The EU AI Act asks that people know they are dealing with an AI system. Change its words with your own resources like any text; an empty text hides it, for a host that says the same in another place the user always sees. Below it, on a small line of its own, is a muted *Fadenstack* link to fadenstack.com; see [About and the Fadenstack link](#about-and-the-fadenstack-link).
- **Skills**: on a server with skills, `$` at the end of the text (or `/` as the whole of it) opens the skills the user can pick, narrowed as they type. *Up* and *Down* move, *Enter* or *Tab* picks, *Esc* closes the list. A picked skill is a chip for the next message only, and shows under the message when it is sent.
- **Pictures**: for a model that sees pictures, a pasted picture, or picture files pasted or dropped on the composer, go with the next message, at most 1600 pixels on the longer side, as JPEG. A chip shows each small. For another model the panel says it does not see pictures. Pasted text stays text.

## The Avalonia skin

`Fadenstack.UI.Avalonia` draws the same view models as the WPF skin, with Avalonia 12, so a desktop application can run on Windows, macOS and Linux. It targets `net8.0`.

```csharp
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Views;
using Fadenstack.UI.ViewModels;

var chat = new AgentChatViewModel(agent, approvals, new AvaloniaUiDispatcher(),
    new AgentChatOptions { Clipboard = new AvaloniaClipboard(() => TopLevel.GetTopLevel(panel)) });
panel.Content = new ChatView { DataContext = chat };
await chat.InitializeAsync();
```

The application uses `FluentTheme`, and the skin's control styles go after it. `ChatView` adds them itself when the application has not:

```xml
<Application.Styles>
  <FluentTheme />
  <fs:FadenstackControls xmlns:fs="using:Fadenstack.UI.Avalonia.Themes" />
</Application.Styles>
```

The skin follows the system's light or dark theme and has a high contrast variant (`FadenstackTheme.HighContrastVariant`). `FadenstackTheme.Apply(target, new ThemeChoices { ... })` sets the accent, the density, the text size and high contrast at run time.

`ChatView` takes the host's own content in slots, as the WPF panel does:

| Property | What it takes |
|---|---|
| `HeaderToolbar` | Icon buttons at the header's right end. |
| `ComposerToolbar` | Content for the composer's bottom left. |
| `SettingsContent` | Your items in the panel's settings. |
| `SignInContent` | Content shown in place of the turns while the agent needs a sign-in. |
| `AttachCommand` | A command; when set, the composer shows *Attach*. |
| `AgentPicture` | Any control as the agent's picture over the empty chat, such as an animated avatar. It is in the visual tree only while the empty chat shows. |

The options, languages, grants and notices described on this page belong to the view models, so they apply to both skins. The XAML examples, `WpfDispatcher`, `WpfClipboard` and the VSTO hosting are WPF only.

## Built for long sessions

The panel stays responsive however long a session grows:

- The agent runs off the UI thread. Its events reach the UI in batches, one per frame, with consecutive text merged into one update.
- A streamed answer is rendered incrementally: only its last Markdown block is parsed again as text arrives, and the blocks before it never change.
- The conversation is a virtualized, recycled list of turns, scrolled by pixel. Tool output and reasoning are formatted only when the user expands them.
- Opening a session reads and builds it off the UI thread and shows the latest turns. *Show earlier messages* pages in older turns.

## Attach context from the host

The host application offers context, such as the current selection, through `chat.Attachments`. An `AttachmentViewModel` is a chip in the composer; its content is read when the message is sent, off the UI thread, so "the selection" is what is selected at that moment.

```csharp
chat.Attachments.Add(new AttachmentViewModel(
    title: "Selection",
    subtitle: "range",
    resolve: cancellationToken => ReadSelectionAsync(cancellationToken),
    isImplicit: true));
```

- `resolve` returns the `MessageAttachment` to send, or null to send nothing.
- An implicit chip stays between messages, and the user can switch it off for the next message. Other chips go with one message and are then removed.
- `AttachmentViewModel.FromContent(attachment)` makes a chip from content you already have.
- If reading the content fails, the message is sent without it and the turn shows a warning.
- `chat.AttachPicture(dataUrl, title, kind)` adds a picture the host took, such as a screenshot of the document. It returns false, and `Error` says why, when the agent's model does not see pictures (`CanAttachPictures`). `Pictures.ToDataUrl(bitmap)` and `Pictures.ReadFile(path)` in `Fadenstack.UI.Wpf.Services` make the data URL.

`ChatView.ComposerToolbar` takes your own content for the composer's bottom left, for example an *Add context* button; it gets the panel's colours. `chat.RequestComposerFocus()` moves the focus to the composer.

## Themes

`ChatView.Theme` picks the colours:

| `ChatTheme` | Colours |
|---|---|
| `System` | Follows Windows' light or dark app mode. |
| `Light` | Light. |
| `Dark` | Dark. |
| `DarkGray` | Dark surfaces on mid-grey, for hosts whose own chrome is grey rather than black. |

`ChatView.PaletteUri(theme)` returns the resource dictionary of a theme's colours, and `ChatView.SystemUsesDarkMode()` tells whether Windows is in dark app mode, for hosts that draw around the panel in the same colours. In it, `Fadenstack.Accent` fills a button under white text; `Fadenstack.AccentIcon` is the accent as an icon, mark or line on the panel's surfaces (in the dark themes a lighter blue, which stands out on grey).

In every theme, text reads at 4.5:1 or more on whatever it sits on, hover and pressed included, and icons, marks, focus rings and the scroll thumb at 3:1 or more (WCAG 2.2 AA). The tests check it.

`AgentAvatar` is the agent's picture as a WPF control. Set `Small` at sizes of 32 pixels and below; `ThreadBrush` and `ThreadSideBrush` recolour it for a custom agent.

## Languages

The panel has no text of its own. Every text it shows is in the resources of `Fadenstack.UI`: `Resources/UiText.resx` holds the English texts, and `UiText.de.resx`, `UiText.fr.resx`, `UiText.es.resx` and `UiText.zh-Hans.resx` the German, French, Spanish and Simplified Chinese ones. They are the one place to change a word. The view models read them through `UiStrings`, and the WPF skin binds its buttons, tooltips and screen-reader names to the same texts.

**The user picks the language** in the panel's settings (the gear in the header): *Automatic*, or one of the languages, each listed in its own name (Deutsch, Français, 中文（简体）), so anyone finds their own. The whole panel changes at once, the conversation too, and the choice is remembered for every Fadenstack panel of the user on this computer, so a language picked in Word is Excel's too, and for the user on the server, so their other devices, the browser agent and the console follow it. Your add-in only says what *Automatic* means, the language the host runs in:

```csharp
using System.Globalization;

var options = new AgentChatOptions
{
    Clipboard = new WpfClipboard(),
    // Office's own UI language, which can differ from Windows'
    AutomaticLanguage = CultureInfo.GetCultureInfo(application.LanguageSettings.LanguageID[MsoAppLanguageID.msoLanguageIDUI]),
};
```

Without `AutomaticLanguage`, *Automatic* is `CultureInfo.CurrentUICulture`. A culture finds the nearest language: `de-AT` gets German, `zh-CN` Simplified Chinese, and a language without texts, such as `zh-TW` or `ja-JP`, gets English.

| Option | What it is |
|---|---|
| `AutomaticLanguage` | What *Automatic* means: the host's language. |
| `Languages` | The languages to pick from, by tag: `UiStrings.BuiltInLanguages` by default. |
| `TextOverrides` | Your own resources (a `.resx` with the same names): your words, and the texts of the languages you add to `Languages`. |
| `LockedLanguage` | A language your organisation fixed, from its policy: the panel uses it, and the settings show it with *Set by your organisation* and no choice. |
| `Preferences` | Where the choice is kept (`IPreferenceStore`). By default a `FilePreferenceStore`: `%APPDATA%\Fadenstack\preferences.json`. `MemoryPreferenceStore` keeps nothing, for tests. |
| `Strings` | Texts you fix instead (`UiStrings.For(culture)`): the panel then offers no choice. |

`chat.SetLanguage("fr")` (or `SetLanguageCommand`) and `chat.SetLanguage(AgentChatViewModel.Automatic)` do from code what the settings do; `await chat.SetLanguageAsync("fr")` also waits until the server kept it. `Language`, `LanguageOptions` and `CanChangeLanguage` say what they show.

**The language follows the user.** The agent's profile carries the language the user picked anywhere (`agent.Profile.User.Language`, a tag such as `de` or `zh-Hans`), and the panel takes it over this computer's when it starts, if it offers that language (`zh` is Simplified Chinese; a language it lacks changes nothing). A pick in the settings is sent back (`agent.SetUserLanguageAsync(tag)`, `PATCH /v1/agents/{agent}/user`); *Automatic* sends `null`, so each client follows its host again. A server that cannot keep it, or an older one without the route, leaves the pick on this computer. A language your organisation locked is never sent, and a language from the server never overrides it.

Numbers and dates follow the format culture, `CultureInfo.CurrentCulture`, so an English panel on a German system writes 1.234 and 13.10.2026.

To add a language or change words, ship your own resources with the same names:

```csharp
// MyTexts.resx: <data name="Allow"><value>Allow once</value></data>
// MyTexts.it.resx: <data name="LanguageName"><value>Italiano</value></data>, and the Italian texts you have
var options = new AgentChatOptions
{
    TextOverrides = MyTexts.ResourceManager,
    Languages = UiStrings.BuiltInLanguages.Append("it").ToList(),
};
```

For each language, from the most specific to English, your text wins over the package's, so your English words do not hide the package's German ones. What your resources lack comes from the package. `UiStrings.Language` is the language the texts are in, and `strings["Allow"]` reads any text by name. The names and what fills their placeholders are in the comments of `UiText.resx`.

`ChatView` sets its `Language` from the texts, and mirrors its layout (`FlowDirection`) for a right-to-left language such as Arabic or Hebrew. In your own templates, `{v:Text Allow}` (namespace `Fadenstack.UI.Wpf.Views`) shows a text by name, and follows the user's pick. Outside a `ChatView`, set `ChatText.Strings` on your root element.

Satellite assemblies hold the translations: `de\Fadenstack.UI.resources.dll` and the others next to `Fadenstack.UI.dll`. Deploy those folders with your add-in, or the panel shows English.

## Settings

The gear in the header opens the panel's settings: the language; the notice for [answers without an AI mark](#answers-without-an-ai-mark); *Approvals*, which lists what the user allowed for longer than one call -- the grants the server holds, with their time left (a running Bypass also with what is recorded), and the tools allowed always on this device -- each with *Revoke* (`Grants.Standing`); below it your add-in's own items, such as sign out or its options, so they all sit in one place, and *About* last. Put your items in `ChatView.SettingsContent`; they get the panel's colours:

```csharp
var view = new ChatView
{
    DataContext = chat,
    SettingsContent = new Button { Content = "Sign out", Command = signOut },
};
```

The gear always shows, because *About* is always there.

## Answers without an AI mark

An administrator can let a model route answer without a machine-readable AI mark (the EU AI Act, Art. 50(2), asks for one). The gateway then sends the event `faden.unmarked` with the answer. The panel hides it by default. A person can turn the notice on in the settings, with *Tell me when an answer carries no AI mark*. With it on, an answer without a mark gets one quiet notice, once per answer: "Answer without an AI mark. This answer carries no machine-readable AI mark. Your organisation allowed this model to answer without one."

The choice is kept per user in the panel's preferences, under the key `tell-unmarked` (`AgentChatViewModel.TellUnmarkedPreference`): the value is `1` when on, and the key is removed when off. If the preference store fails, the notice stays off. In code: `chat.TellUnmarked` and `ToggleTellUnmarkedCommand`. The event never reaches the generic notice, so the panel never shows its raw name.

## About and the Fadenstack link

*About*, the last item in the settings, opens a card inside the panel with:

- the agent: its name, *Version 3* or *Draft*, and its model when the profile names one;
- the app the agent runs in, when you described it with `DescribeApp` (its name and version);
- the SDK: *Fadenstack SDK 0.3.0 (.NET)*, the version of the package;
- the server's address, shown only;
- links, one per line: *Help* (your organisation's server, `<server>/help/`), *Fadenstack* (the website) and *Developer docs*;
- *Copy details*, which copies the agent, its version and model, the app, the SDK and the server as lines of text, for a support request.

*Escape*, *Close* or a press anywhere outside the card closes it. About is there before the agent starts, too, so the SDK and the server show when the panel cannot connect.

The links open in the browser through `MarkdownText.OpenLink`, as links in answers do; replace it to route them through your host. Another skin handles `chat.LinkRequested`.

**What a link sends.** A link to fadenstack.com or developers.fadenstack.com carries two values and nothing else: `ref`, where it was opened (`sdk-panel` for the line under the composer, `sdk-about` for About), and `v`, the SDK's major and minor version (`0.3`). It never carries the user, the organisation, the server, the agent, the app, a model, an id or anything typed in the panel. The *Help* link goes to your own server and carries nothing.

| Option | What it is |
|---|---|
| `Branding` | `false` hides the *Fadenstack* link's line under the AI notice; the notice and About stay. Default `true`. |
| `RefSource` | Your app's short name for `ref` (`office` gives `office-panel` and `office-about`): lower-case letters and digits, words joined by hyphens, at most 20 characters. Anything else is `sdk`, the default. Never a user's, organisation's or server's name. |

```csharp
var options = new AgentChatOptions { Clipboard = new WpfClipboard(), RefSource = "office", Branding = false };
```

## Host the panel in VSTO and Windows Forms

Office VSTO add-ins and Windows Forms applications have no WPF `Application`. Host `ChatView` in an `ElementHost`, and create the `WpfDispatcher` on the UI thread:

```csharp
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Fadenstack.UI.Wpf.Views;

var view = new ChatView { DataContext = chat, Theme = ChatTheme.System };
var host = new ElementHost { Dock = DockStyle.Fill, Child = view };
taskPaneControl.Controls.Add(host);
```

`ElementHost` is in the `WindowsFormsIntegration` assembly. In an SDK-style project, set both `<UseWPF>true</UseWPF>` and `<UseWindowsForms>true</UseWindowsForms>`.

## Extend the rendering

Gateway events become parts of an answer through renderers. The panel's own renderers handle citations and personal-data notices, and show any other custom event as a generic notice. Add your own for events your server sends:

```csharp
using Fadenstack.Chat;
using Fadenstack.UI.Parts;
using Fadenstack.UI.ViewModels;

public sealed class StatusRenderer : IAgentEventRenderer
{
    public bool TryRender(FadenstackEventContent gatewayEvent, TurnViewModel turn)
    {
        if (gatewayEvent.Kind != "example.status")
        {
            return false;
        }
        var text = gatewayEvent.Value.GetProperty("text").GetString() ?? "";
        turn.AddPart(new NoticePart(NoticeSeverity.Info, "Status", text));
        return true;
    }
}
```

```csharp
var options = new AgentChatOptions { Clipboard = new WpfClipboard() };
options.Renderers.Add(new StatusRenderer());
```

Your renderers are tried before the built-in ones; the first that returns true wins. They run on the UI thread.

`turn.AddPart` takes any `ResponsePart`. Besides `NoticePart`, these primitives have templates in the WPF skin, so a feature needs no view of its own:

| Part | Shows |
|---|---|
| `CardPart` | A titled card with label and value fields. |
| `TablePart` | A plain table with headers and rows. |
| `ProgressPart` | Progress from 0 to 1, or an indeterminate "working". Its `Label` and `Value` can change. |
| `DiffPart` | A change, line by line, with added, removed and context lines. |
| `NoticePart` | A notice with a severity (`Info`, `Warning`, `Blocked`, `Error`), a title, a message and an optional detail. |

## Options

`AgentChatOptions` adjusts the view model; the defaults suit a desktop chat panel.

| Option | Meaning |
|---|---|
| `Clipboard` | The clipboard for copy buttons. Without it, copying does nothing. |
| `Renderers` | Your gateway event renderers. |
| `RenderInterval` | How often streamed updates reach the UI thread. |
| `InitialTurns` | How many turns an opened session shows at first. |
| `PageSize` | How many older turns *Show earlier messages* adds. |
| `PreviewLength` | How many characters of a tool's arguments or result are shown. |
| `AutomaticLanguage`, `Languages`, `TextOverrides`, `LockedLanguage`, `Preferences` | The language the user picks in the settings, and where it is kept. See [Languages](#languages). |
| `Strings` | Texts you fix instead of a language the user picks. |
| `Branding`, `RefSource` | The *Fadenstack* link under the composer, and your app's name in the links' `ref`. See [About and the Fadenstack link](#about-and-the-fadenstack-link). |
| `BeforeSend`, `AfterSend` | What your add-in does around each message. See below. |
| `Clock` | The time, for tests. |

### Before a message goes

Your add-in may need to do something before each message is sent: check that the user may still send, take the
context its tools read, or attach a document. Do it in `BeforeSend`, so it runs for every message, from the composer,
a suggestion, Retry, or Edit and resend:

```csharp
var options = new AgentChatOptions
{
    BeforeSend = async (message, cancellationToken) =>
    {
        _document = _host.ActiveDocument ?? throw new InvalidOperationException("Open a document first.");
        _chat.Attachments.Add(AttachmentViewModel.FromContent(await ReadSelectionAsync(_document, cancellationToken)));
    },
    AfterSend = _ => _document = null,
};
```

`BeforeSend` runs on the UI thread. While it runs, the chat is busy and Stop cancels its token. If it throws, nothing
is sent: the text stays in the composer and `Error` shows the message. Attachments it adds go with this message.
`AfterSend` runs once after every send, however it ended: its turn finished, failed or was stopped, or `BeforeSend`
threw.

## A gate on running turns

A host that runs several chats can limit how many turns work at the same time. Give the options a `TurnGate`. It is awaited when a turn is about to start, after the user's message is shown and before anything is sent, and it returns a lease that the chat disposes once, when the turn ends:

```csharp
var slots = new SemaphoreSlim(2);

var options = new AgentChatOptions
{
    TurnGate = async cancellationToken =>
    {
        await slots.WaitAsync(cancellationToken);
        return new Slot(slots);
    },
};

public sealed class Slot : IDisposable
{
    private readonly SemaphoreSlim _slots;

    public Slot(SemaphoreSlim slots) => _slots = slots;

    public void Dispose() => _slots.Release();
}
```

While the gate waits, `chat.IsWaitingForSlot` is true and the turn says "Waiting for a free slot" (`Strings.WaitingForSlot`). Stop cancels the wait, and nothing is sent. If the gate throws, the turn fails with the exception's message. Without a `TurnGate` (the default) there is no gate.

## The view model

For your own skin, or to drive the chat from the host:

| Member | Meaning |
|---|---|
| `InitializeAsync()` | Starts the agent and reads the session list. |
| `SendAsync(text)` | Sends a message, after `BeforeSend`; completes when the turn has ended and been shown. |
| `NewChat()`, `OpenSessionAsync(id)`, `RefreshSessionsAsync()` | Chats on this device. |
| `Turns`, `Sessions`, `Attachments`, `PickedSkills`, `Suggestions` | The collections the panel binds to. |
| `SkillPicker`, `CanPickSkills`, `RemoveSkill(slug)` | The skills `$` lists: open, its options, the active one, `Move(step)`, `Pick()`, `Close()`. |
| `AttachPicture(dataUrl)`, `CanAttachPictures`, `PictureNotRead(name)` | Pictures for the next message. |
| `Title`, `Greeting`, `Input`, `SelectedToolMode`, `ToolModes`, `ToolModeHint` | The header, the composer and the mode picker. |
| `IsReady`, `IsBusy`, `CanSend`, `IsEmpty`, `HasEarlierTurns`, `SignInRequired`, `Error` | State. |
| `Strings` | The texts the panel shows, for a skin's labels; they change when the user picks another language. |
| `Language`, `LanguageOptions`, `CanChangeLanguage`, `IsLanguageLocked`, `SetLanguage(id)`, `IsSettingsOpen` | The settings. |
| `About`, `IsAboutOpen`, `OpenAboutCommand`, `CloseAboutCommand`, `CopyDetailsCommand` | About: what it shows (`AboutInfo`, in the panel's language), and opening, closing and copying it. |
| `BrandingLink`, `OpenLinkCommand`, `LinkRequested` | The link under the composer (null when `Branding` is off), and the panel's links: the command raises `LinkRequested` with the address, and the skin opens it. |
| `SendCommand`, `StopCommand`, `NewChatCommand`, `LoadEarlierCommand`, and others | Commands for the skin. |

`InlineDispatcher` runs everything on the calling thread, for tests and hosts without a UI thread.
