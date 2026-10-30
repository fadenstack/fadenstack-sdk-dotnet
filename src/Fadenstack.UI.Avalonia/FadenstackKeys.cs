namespace Fadenstack.UI.Avalonia;

/// <summary>
/// The names of the theme resources of the Avalonia skin (doc 22 section 3), for code that looks them up; XAML uses the
/// strings through <c>DynamicResource</c>. Colours are brushes per theme variant (Light, Dark, HighContrast); sizes are
/// <see cref="double"/>, spaces <c>Thickness</c>, radii <c>CornerRadius</c>, the elevation <c>BoxShadows</c>. Font family and
/// icon keys are not here: they belong to the typography and icon dictionaries.
/// </summary>
public static class FadenstackKeys
{
    private const string P = "Fadenstack.";

    // Surfaces and text

    /// <summary>The window's background.</summary>
    public const string Background = P + "Background";

    /// <summary>Cards, menus, sheets, inputs (the console's paper).</summary>
    public const string Surface = P + "Surface";

    /// <summary>Table heads, raised tiles.</summary>
    public const string SurfaceAlt = P + "SurfaceAlt";

    /// <summary>Inner tiles on a surface: black (light) or white (dark) at 3 %.</summary>
    public const string SurfaceSubtle = P + "SurfaceSubtle";

    /// <summary>Primary text.</summary>
    public const string Foreground = P + "Foreground";

    /// <summary>Secondary text and icons.</summary>
    public const string ForegroundMuted = P + "ForegroundMuted";

    /// <summary>Dividers and outlines.</summary>
    public const string Border = P + "Border";

    /// <summary>
    /// Outlines that identify a control (inputs, the composer, secondary buttons): at least 3:1 on Surface (WCAG 2.2
    /// SC 1.4.11), darker than the console's MUI outline on purpose (fadenstack-dev desktop-agent/22 §3.1).
    /// </summary>
    public const string BorderStrong = P + "BorderStrong";

    // Accent (per named accent and theme variant)

    /// <summary>The accent: primary fill, selection, user bubble.</summary>
    public const string Accent = P + "Accent";

    /// <summary>The accent's hover fill.</summary>
    public const string AccentHover = P + "AccentHover";

    /// <summary>A lighter step of the accent ramp.</summary>
    public const string AccentLight = P + "AccentLight";

    /// <summary>Text and icons on an accent fill.</summary>
    public const string AccentForeground = P + "AccentForeground";

    /// <summary>The accent as text or an icon on a surface (at least 4.5:1).</summary>
    public const string AccentText = P + "AccentText";

    /// <summary>The accent as an icon or stroke colour (the older name for the accent on a surface).</summary>
    public const string AccentIcon = P + "AccentIcon";

    /// <summary>The current page in a navigation list: its fill.</summary>
    public const string NavSelected = P + "NavSelected";

    /// <summary>The current page in a navigation list: its text.</summary>
    public const string NavSelectedForeground = P + "NavSelectedForeground";

    /// <summary>Hover fill of quiet controls: the accent at 12 %.</summary>
    public const string Hover = P + "Hover";

    /// <summary>Selected row, chip fill, drop target: the accent at 6 %.</summary>
    public const string Tint = P + "Tint";

    /// <summary>Pressed fill: the accent at 20 %.</summary>
    public const string Pressed = P + "Pressed";

    /// <summary>The keyboard focus ring (the accent as text).</summary>
    public const string Focus = P + "Focus";

    /// <summary>Links (the accent as text).</summary>
    public const string Link = P + "Link";

    /// <summary>The user's chat bubble tint (the older style; the console's solid bubble is <see cref="Accent"/>).</summary>
    public const string UserBubble = P + "UserBubble";

    /// <summary>The agent's chat bubble (with a <see cref="Border"/> line).</summary>
    public const string AgentBubble = P + "AgentBubble";

    // Status

    /// <summary>Errors.</summary>
    public const string Danger = P + "Danger";

    /// <summary>Warnings as a fill or icon.</summary>
    public const string Warning = P + "Warning";

    /// <summary>A warning as text on a surface.</summary>
    public const string WarningText = P + "WarningText";

    /// <summary>An error as text on a surface (at least 4.5:1); <see cref="Danger"/> is for icons and fills.</summary>
    public const string DangerText = P + "DangerText";

    /// <summary>Success.</summary>
    public const string Success = P + "Success";

    /// <summary>Info banner fill.</summary>
    public const string InfoBackground = P + "InfoBackground";

    /// <summary>Warning banner fill.</summary>
    public const string WarningBackground = P + "WarningBackground";

    /// <summary>Error or blocked banner fill.</summary>
    public const string BlockedBackground = P + "BlockedBackground";

    /// <summary>Success banner fill.</summary>
    public const string SuccessBackground = P + "SuccessBackground";

    /// <summary>Added lines of a diff.</summary>
    public const string AddedBackground = P + "AddedBackground";

    /// <summary>Removed lines of a diff.</summary>
    public const string RemovedBackground = P + "RemovedBackground";

    // Other

    /// <summary>The veil behind a modal sheet.</summary>
    public const string Scrim = P + "Scrim";

    /// <summary>Code blocks.</summary>
    public const string CodeBackground = P + "CodeBackground";

    /// <summary>Inline code.</summary>
    public const string CodeInlineBackground = P + "CodeInlineBackground";

    /// <summary>Scrollbar thumb.</summary>
    public const string ScrollThumb = P + "ScrollThumb";

    /// <summary>Scrollbar thumb on hover.</summary>
    public const string ScrollThumbHover = P + "ScrollThumbHover";

    /// <summary>The mark's plates (never UI state).</summary>
    public const string BrandPlate = P + "BrandPlate";

    /// <summary>The mark's thread (never UI state).</summary>
    public const string BrandThread = P + "BrandThread";

    /// <summary>The one shadow of floating surfaces (<c>BoxShadows</c>).</summary>
    public const string Elevation = P + "Elevation";

    // Shape and space

    /// <summary>The one radius, 12 (<c>CornerRadius</c>).</summary>
    public const string Radius = P + "Radius";

    /// <summary>Pills and chips, 999.</summary>
    public const string RadiusPill = P + "Radius.Pill";

    /// <summary>The user's message bubble corners, 12 4 12 12.</summary>
    public const string RadiusBubbleUser = P + "Radius.Bubble.User";

    /// <summary>The agent's message bubble corners, 4 12 12 12.</summary>
    public const string RadiusBubbleAgent = P + "Radius.Bubble.Agent";

    /// <summary>Space 4 (<c>Thickness</c>, the older name of <see cref="Space1"/>).</summary>
    public const string SpaceSmall = P + "Space.Small";

    /// <summary>Space 8 (<c>Thickness</c>, the older name of <see cref="Space2"/>).</summary>
    public const string SpaceMedium = P + "Space.Medium";

    /// <summary>Space 16 (<c>Thickness</c>, the older name of <see cref="Space4"/>).</summary>
    public const string SpaceLarge = P + "Space.Large";

    /// <summary>Space 4 as a <c>Thickness</c>.</summary>
    public const string Space1 = P + "Space.1";

    /// <summary>Space 8 as a <c>Thickness</c>.</summary>
    public const string Space2 = P + "Space.2";

    /// <summary>Space 12 as a <c>Thickness</c>.</summary>
    public const string Space3 = P + "Space.3";

    /// <summary>Space 16 as a <c>Thickness</c>.</summary>
    public const string Space4 = P + "Space.4";

    /// <summary>Space 24 as a <c>Thickness</c>.</summary>
    public const string Space5 = P + "Space.5";

    /// <summary>Space 32 as a <c>Thickness</c>.</summary>
    public const string Space6 = P + "Space.6";

    /// <summary>Space 4 as a number, for <c>Spacing</c> and sizes.</summary>
    public const string Space1Value = P + "Space.1.Value";

    /// <summary>Space 8 as a number.</summary>
    public const string Space2Value = P + "Space.2.Value";

    /// <summary>Space 12 as a number.</summary>
    public const string Space3Value = P + "Space.3.Value";

    /// <summary>Space 16 as a number.</summary>
    public const string Space4Value = P + "Space.4.Value";

    /// <summary>Space 24 as a number.</summary>
    public const string Space5Value = P + "Space.5.Value";

    /// <summary>Space 32 as a number.</summary>
    public const string Space6Value = P + "Space.6.Value";

    // Sizes

    /// <summary>Height of text buttons, inputs, selects: 36, compact 32.</summary>
    public const string ControlHeight = P + "ControlHeight";

    /// <summary>Large icon button (title bar, composer): 36.</summary>
    public const string IconButtonSizeLarge = P + "IconButton.Size.Large";

    /// <summary>Normal icon button (headers, bubble): 32.</summary>
    public const string IconButtonSizeNormal = P + "IconButton.Size.Normal";

    /// <summary>Small icon button (tab strip, chips, list rows): 28.</summary>
    public const string IconButtonSizeSmall = P + "IconButton.Size.Small";

    /// <summary>Icon inside a large icon button: 20.</summary>
    public const string IconSizeLarge = P + "Icon.Size.Large";

    /// <summary>Icon inside a normal icon button: 18.</summary>
    public const string IconSizeNormal = P + "Icon.Size.Normal";

    /// <summary>Icon inside a small icon button: 16.</summary>
    public const string IconSizeSmall = P + "Icon.Size.Small";

    /// <summary>Icon in the activity bar: 22.</summary>
    public const string IconSizeActivityBar = P + "Icon.Size.ActivityBar";

    /// <summary>Title bar height: 48, compact 44.</summary>
    public const string TitleBarHeight = P + "TitleBar.Height";

    /// <summary>Status bar height: 28, compact 24.</summary>
    public const string StatusBarHeight = P + "StatusBar.Height";

    /// <summary>Activity bar width: 56.</summary>
    public const string ActivityBarWidth = P + "ActivityBar.Width";

    /// <summary>Activity bar width with labels: 84.</summary>
    public const string ActivityBarWidthLabels = P + "ActivityBar.Width.Labels";

    /// <summary>The chat beside the workbench: 380.</summary>
    public const string SidePanelWidth = P + "SidePanel.Width";

    /// <summary>Smallest side panel: 300.</summary>
    public const string SidePanelMinWidth = P + "SidePanel.MinWidth";

    /// <summary>Largest side panel: 560.</summary>
    public const string SidePanelMaxWidth = P + "SidePanel.MaxWidth";

    /// <summary>The overlay's bubble: 340.</summary>
    public const string BubbleWidth = P + "Bubble.Width";

    /// <summary>The bubble when it shows a card: 420.</summary>
    public const string BubbleWidthCard = P + "Bubble.Width.Card";

    /// <summary>Modal sheets: 480.</summary>
    public const string SheetWidth = P + "Sheet.Width";

    /// <summary>The details drawer: 400.</summary>
    public const string DrawerWidth = P + "Drawer.Width";

    // Type sizes (multiplied by the text scale)

    /// <summary>Micro, 11: count badges, key chips.</summary>
    public const string FontSizeMicro = P + "FontSize.Micro";

    /// <summary>Caption, 12: captions, help, meta lines.</summary>
    public const string FontSizeCaption = P + "FontSize.Caption";

    /// <summary>The older name of <see cref="FontSizeCaption"/>.</summary>
    public const string FontSizeSmall = P + "FontSize.Small";

    /// <summary>Body, 14.</summary>
    public const string FontSizeBody = P + "FontSize.Body";

    /// <summary>Subtitle, 16: section and card titles.</summary>
    public const string FontSizeSubtitle = P + "FontSize.Subtitle";

    /// <summary>Title, 20: view titles.</summary>
    public const string FontSizeTitle = P + "FontSize.Title";

    /// <summary>Display, 24: the sign-in title only.</summary>
    public const string FontSizeDisplay = P + "FontSize.Display";
}
