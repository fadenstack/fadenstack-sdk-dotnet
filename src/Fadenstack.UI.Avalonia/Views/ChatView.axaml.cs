using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Fadenstack.UI.Avalonia.Behaviors;
using Fadenstack.UI.Avalonia.Services;
using Fadenstack.UI.Avalonia.Themes;
using Fadenstack.UI.ViewModels;

namespace Fadenstack.UI.Avalonia.Views;

public enum ChatTheme
{
    /// <summary>Follows the host application's theme variant, which follows the system by default.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// The chat panel for an <see cref="AgentChatViewModel"/> (set it as the DataContext). Everything it shows comes from
/// the view model, its texts too (<see cref="AgentChatViewModel.Strings"/>); the code here only gives the panel the
/// texts' language and direction, sends on Enter, moves the focus to the composer when the chat asks, and opens the
/// panel's links through <see cref="MarkdownText.OpenLink"/>.
/// </summary>
public partial class ChatView : UserControl
{
    /// <summary>Extra content at the composer's bottom left, for the host's own buttons (e.g. "Add context").</summary>
    public static readonly StyledProperty<object?> ComposerToolbarProperty = AvaloniaProperty.Register<ChatView, object?>(nameof(ComposerToolbar));

    /// <summary>The host's own items in the panel's settings, below the language (sign out, its own options).</summary>
    public static readonly StyledProperty<object?> SettingsContentProperty = AvaloniaProperty.Register<ChatView, object?>(nameof(SettingsContent));

    public static readonly StyledProperty<ChatTheme> AppearanceProperty = AvaloniaProperty.Register<ChatView, ChatTheme>(nameof(Appearance));

    /// <summary>The host's own icon buttons at the header's right end (the desktop's "Close the chat panel").</summary>
    public static readonly StyledProperty<object?> HeaderToolbarProperty = AvaloniaProperty.Register<ChatView, object?>(nameof(HeaderToolbar));

    /// <summary>Attaches files: when the host sets it, the composer shows the Attach icon button.</summary>
    public static readonly StyledProperty<ICommand?> AttachCommandProperty = AvaloniaProperty.Register<ChatView, ICommand?>(nameof(AttachCommand));

    /// <summary>The agent's avatar over an empty chat (96 px); without one the chat shows its own mark.</summary>
    public static readonly StyledProperty<object?> AvatarProperty = AvaloniaProperty.Register<ChatView, object?>(nameof(Avatar));

    /// <summary>The host's sign-in, shown in place of the turns while the agent needs a sign-in.</summary>
    public static readonly StyledProperty<object?> SignInContentProperty = AvaloniaProperty.Register<ChatView, object?>(nameof(SignInContent));

    /// <summary>Below this width the panel is a side panel: code wraps instead of scrolling.</summary>
    internal const double NarrowWidth = 600;

    /// <summary>The resource the user's bubble takes its largest width from (75 % of the turn, 85 % below 480 px).</summary>
    internal const string UserBubbleMaxWidth = "Fadenstack.Chat.UserBubble.MaxWidth";

    /// <summary>The widest a turn gets: the reading width of design 22, section 6.6.</summary>
    private const double TurnWidth = 760;

    /// <summary>A turn's margin at each side (Parts.axaml, <c>StackPanel.turn</c>).</summary>
    private const double TurnMargin = 16;

    private AgentChatViewModel? _chat;

    public ChatView()
    {
        FadenstackControls.EnsureLoaded();
        Resources[UserBubbleMaxWidth] = Math.Floor(TurnWidth * 0.75);
        InitializeComponent();
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
    }

    public object? HeaderToolbar
    {
        get => GetValue(HeaderToolbarProperty);
        set => SetValue(HeaderToolbarProperty, value);
    }

    public ICommand? AttachCommand
    {
        get => GetValue(AttachCommandProperty);
        set => SetValue(AttachCommandProperty, value);
    }

    public object? Avatar
    {
        get => GetValue(AvatarProperty);
        set => SetValue(AvatarProperty, value);
    }

    public object? SignInContent
    {
        get => GetValue(SignInContentProperty);
        set => SetValue(SignInContentProperty, value);
    }

    /// <summary>
    /// The layout that depends on the panel's width, which XAML cannot say in percent: the user's bubble at most 75 % of the
    /// turn (85 % below 480 px), and below <see cref="NarrowWidth"/> the <c>narrow</c> class and code that wraps.
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var width = e.NewSize.Width;
        var turn = Math.Max(0, Math.Min(width - 2 * TurnMargin, TurnWidth));
        Resources[UserBubbleMaxWidth] = Math.Floor(turn * (width < 480 ? 0.85 : 0.75));
        Root.Classes.Set("narrow", width < NarrowWidth);
        MarkdownView.SetWrapCode(Root, width < NarrowWidth);
    }

    public object? ComposerToolbar
    {
        get => GetValue(ComposerToolbarProperty);
        set => SetValue(ComposerToolbarProperty, value);
    }

    public object? SettingsContent
    {
        get => GetValue(SettingsContentProperty);
        set => SetValue(SettingsContentProperty, value);
    }

    public ChatTheme Appearance
    {
        get => GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty)
        {
            Follow(DataContext as AgentChatViewModel);
        }
        else if (change.Property == AppearanceProperty)
        {
            Scope.RequestedThemeVariant = Appearance switch
            {
                ChatTheme.Light => ThemeVariant.Light,
                ChatTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    private void Follow(AgentChatViewModel? chat)
    {
        if (_chat is not null)
        {
            _chat.PropertyChanged -= OnChatChanged;
            _chat.LinkRequested -= OnLinkRequested;
            _chat.ComposerFocusRequested -= OnComposerFocusRequested;
        }
        _chat = chat;
        if (_chat is not null)
        {
            _chat.PropertyChanged += OnChatChanged;
            _chat.LinkRequested += OnLinkRequested;
            _chat.ComposerFocusRequested += OnComposerFocusRequested;
        }
        ApplyStrings();
    }

    /// <summary>The user picked another language: the panel's texts, language and direction follow.</summary>
    private void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentChatViewModel.Strings) or null or "")
        {
            ApplyStrings();
        }
    }

    /// <summary>The view model's texts for everything in the panel (<see cref="ChatText"/>), with their language and direction.</summary>
    private void ApplyStrings()
    {
        var strings = _chat?.Strings;
        ChatText.SetStrings(this, strings);
        if (strings is null)
        {
            ClearValue(FlowDirectionProperty);
            return;
        }
        FlowDirection = strings.IsRightToLeft ? global::Avalonia.Media.FlowDirection.RightToLeft : global::Avalonia.Media.FlowDirection.LeftToRight;
    }

    private static void OnLinkRequested(object? sender, Uri url) => MarkdownText.OpenLink(url);

    private void OnComposerFocusRequested(object? sender, EventArgs e)
    {
        Composer.Focus();
        Composer.CaretIndex = Composer.Text?.Length ?? 0;
    }

    /// <summary>Enter sends, Shift+Enter is a new line; the skill picker's keys (arrows, Tab) are the host's to add.</summary>
    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _chat is { SendCommand: var send } && send.CanExecute(null))
        {
            send.Execute(null);
            e.Handled = true;
        }
    }
}
