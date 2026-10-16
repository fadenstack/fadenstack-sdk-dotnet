# Chat panel

This page covers the ready chat UI: the view models in `Fadenstack.UI`, the WPF skin in `Fadenstack.UI.Wpf`, what the panel shows, its languages and settings, hosting it in add-ins, and extending how answers are rendered.

## Two packages

The UI is split in two, and the split is strict:

- **`Fadenstack.UI`** holds all of the behaviour, as view models without platform references: the chat (`AgentChatViewModel`), its turns, the parts of each answer, the tool-mode picker, the session history, every text the panel shows (`UiStrings`, from the package's resources), and the `IUiDispatcher` and `IClipboard` services a skin implements. It targets `netstandard2.0` and `net8.0`, and it runs headless in unit tests.
- **`Fadenstack.UI.Wpf`** only renders. It is XAML templates, styles, themes and a few view-only behaviours, such as keeping the conversation scrolled to the bottom and moving the focus to the composer. It targets `net472` and `net8.0-windows`.

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

- `ApprovalBroker` is the agent's approval handler for a UI: each request becomes an approval card in the chat, and the user's click is the decision. Give the same broker to the builder and to the view model. When no chat is listening, it declines.
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

- **The header** shows the agent's name, the tool-mode picker with a line on what the picked mode means, the list of chats on this device, and a button for a new chat. If the user may not pick a mode, the picker shows it greyed out.
- **An empty chat** shows the agent's greeting and suggested prompts, from the agent's profile (`ui.greeting`, `ui.suggestions`). The definition may carry them per language (`ui.languages.<tag>.greeting` and `.suggestions`; `de` also serves `de-AT`): the panel shows the ones for its language, and others when the user picks another.
- **The answer streams as Markdown**: headings, lists, code blocks with a copy button, tables, quotes, rules and links. Text and tool calls appear in the order they happened.
- **Each tool call is one line**: "Running read_page" while it runs, then "Ran read_page" with its class and duration. It expands to its arguments and result.
- **Approvals are cards** in the answer, above the call they are for, with *Allow* and *Deny*, and *Allow for this chat* and *Allow always* where the request offers them: never for destructive or unclassified tools, nor for a call an administrator's rule covers. Destructive and unclassified tools are marked. A call that an earlier approval lets through gets no card, also when a session is opened again. A card for a tool that reaches outside the organisation (`OpenWorld`, such as a web search) says that what it is given leaves the organisation; a destructive one still says it cannot be undone.
- **The tool-mode menu** ends with *Reset approvals*, which forgets what the user allowed always for this agent on this device and what they allowed for the current chat (`ResetApprovalsCommand`).
- **Refusals and limits are notices** in the answer. A refused prompt is marked *Not sent*, with *Edit and resend*. A notice shows the server's decision id for support.
- **Citations** ("Used 3 references") and **the model's reasoning** ("Thinking...", then how long it thought) are collapsed until the user opens them. Personal data the server kept from the model is reported as a notice.
- **Gateway events the panel does not know** appear as generic notices.
- **Under each answer** are its token count, its duration, a copy button, and a retry button for an answer that failed or was stopped.
- **The composer** shows attached context as chips and the current tool mode. *Enter* sends, *Shift+Enter* starts a new line, *Esc* stops the running turn, and *Ctrl+N* starts a new chat.
- **Skills**: on a server with skills, `$` at the end of the text (or `/` as the whole of it) opens the skills the user can pick, narrowed as they type. *Up* and *Down* move, *Enter* or *Tab* picks, *Esc* closes the list. A picked skill is a chip for the next message only, and shows under the message when it is sent.
- **Pictures**: for a model that sees pictures, a pasted picture, or picture files pasted or dropped on the composer, go with the next message, at most 1600 pixels on the longer side, as JPEG. A chip shows each small. For another model the panel says it does not see pictures. Pasted text stays text.

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

`ChatView.PaletteUri(theme)` returns the resource dictionary of a theme's colours, and `ChatView.SystemUsesDarkMode()` tells whether Windows is in dark app mode, for hosts that draw around the panel in the same colours.

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

The gear in the header opens the panel's settings: the language, and below it your add-in's own items, such as sign out or its options, so they all sit in one place. Put them in `ChatView.SettingsContent`; they get the panel's colours:

```csharp
var view = new ChatView
{
    DataContext = chat,
    SettingsContent = new Button { Content = "Sign out", Command = signOut },
};
```

The gear only shows when there is something to set: a choice of languages, a fixed one to show, or your items.

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
| `SendCommand`, `StopCommand`, `NewChatCommand`, `LoadEarlierCommand`, and others | Commands for the skin. |

`InlineDispatcher` runs everything on the calling thread, for tests and hosts without a UI thread.
