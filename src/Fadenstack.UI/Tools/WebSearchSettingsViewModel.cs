using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.UI.Services;
using Fadenstack.WebSearch;

namespace Fadenstack.UI.Tools;

/// <summary>Where the person's web searches go (the settings sheet's three choices).</summary>
public enum WebSearchChoice
{
    /// <summary>The assistant has no web search tool.</summary>
    Off,

    /// <summary>The Brave Search API with the person's own key.</summary>
    Brave,

    /// <summary>A SearXNG instance the person names.</summary>
    SearXng,
}

/// <summary>
/// The web search settings sheet (sdk/51 F06): Off, Brave (the person's own key) or SearXNG (their own address), made from the
/// person's <see cref="WebSearchStore"/>. The key goes to the store's key store and is never shown again: the box is never filled from
/// the store, and a kept key shows as a "kept" line with Replace key and Remove key. The address is checked with
/// <see cref="WebSearchSettings.IsAllowedSearXngUrl"/> (https, or http on this computer only, no user or password). Save writes the
/// settings and then the key; Cancel writes nothing. The typed key lives here only until the sheet is saved, cancelled or disposed
/// (OWASP ASVS 5.0 V13). Two ways to open it: inside the Tools view (<see cref="ToolsViewModel"/> opens it as its sheet, on the
/// <i>In this app</i> tab) or on its own, as the view of an app with a Tools page of its own; <see cref="Closed"/> says when the
/// person is done. Strict MVVM: no view type; the store's change event (from any thread) is brought to the UI thread through the
/// <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class WebSearchSettingsViewModel : ObservableObject, IToolsTexts, IRetext, IDisposable
{
    private enum Problem
    {
        None,
        KeyMissing,
        AddressMissing,
        AddressRefused,
        SaveFailed,
    }

    private readonly WebSearchStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<UiStrings> _strings;
    private bool _hasKept;
    private bool _replacing;
    private bool _removeRequested;
    private Problem _problem;
    private bool _saved;
    private bool _disposed;

    /// <param name="store">The person's web search settings and key.</param>
    /// <param name="dispatcher">The UI thread; inline by default (tests, headless hosts).</param>
    /// <param name="strings">The texts; the current UI language's by default.</param>
    public WebSearchSettingsViewModel(WebSearchStore store, IUiDispatcher? dispatcher = null, Func<UiStrings>? strings = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = dispatcher ?? InlineDispatcher.Instance;
        var current = UiStrings.Current;
        _strings = strings ?? (() => current);
        var settings = store.Settings;
        _choice = settings.Provider switch
        {
            WebSearchProvider.Brave => WebSearchChoice.Brave,
            WebSearchProvider.SearXng => WebSearchChoice.SearXng,
            _ => WebSearchChoice.Off,
        };
        _address = settings.SearXngUrl?.AbsoluteUri ?? "";
        _hasKept = store.HasBraveKey;
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        ReplaceKeyCommand = new RelayCommand(ReplaceKey);
        RemoveKeyCommand = new RelayCommand(RemoveKey);
        _store.Changed += OnStoreChanged;
    }

    /// <summary>The sheet was saved or cancelled: the host closes it (the Tools view does; an app's own page leaves its page).</summary>
    public event EventHandler? Closed;

    /// <summary>The texts; a skin binds them and follows this when the language changes.</summary>
    public UiStrings Strings => _strings();

    public string Title => Strings.WebSearchTitle;

    // ---- the choice -----------------------------------------------------------------------------------------------------

    /// <summary>Where the searches go.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOff))]
    [NotifyPropertyChangedFor(nameof(IsBrave))]
    [NotifyPropertyChangedFor(nameof(IsSearXng))]
    private WebSearchChoice _choice;

    /// <summary>Off is picked. Setting it true picks it (false changes nothing: another choice was picked).</summary>
    public bool IsOff
    {
        get => Choice == WebSearchChoice.Off;
        set => Pick(WebSearchChoice.Off, value);
    }

    /// <summary>Brave is picked.</summary>
    public bool IsBrave
    {
        get => Choice == WebSearchChoice.Brave;
        set => Pick(WebSearchChoice.Brave, value);
    }

    /// <summary>SearXNG is picked.</summary>
    public bool IsSearXng
    {
        get => Choice == WebSearchChoice.SearXng;
        set => Pick(WebSearchChoice.SearXng, value);
    }

    partial void OnChoiceChanged(WebSearchChoice value)
    {
        _problem = Problem.None;
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowsKeyInput));
        OnPropertyChanged(nameof(ShowsKeptKey));
        OnPropertyChanged(nameof(ShowsRemovedKey));
    }

    // A radio button that is unchecked reports false after the one that was picked reported true: only a pick counts.
    private void Pick(WebSearchChoice choice, bool picked)
    {
        if (picked)
        {
            Choice = choice;
        }
        else
        {
            OnPropertyChanged(choice switch { WebSearchChoice.Off => nameof(IsOff), WebSearchChoice.Brave => nameof(IsBrave), _ => nameof(IsSearXng) });
        }
    }

    // ---- the key (Brave) ------------------------------------------------------------------------------------------------

    /// <summary>The new key while it is typed; never filled from the store, cleared when the sheet is saved, cancelled or disposed.</summary>
    [ObservableProperty]
    private string _key = "";

    /// <summary>The key box is shown: a key is being typed (none is kept, or Replace key was pressed).</summary>
    public bool ShowsKeyInput => !ShowsKeptKey && !ShowsRemovedKey;

    /// <summary>A key is kept: "A key is saved" with Replace key and Remove key; the key itself is never shown.</summary>
    public bool ShowsKeptKey => _hasKept && !_replacing && !_removeRequested;

    /// <summary>Remove key was pressed: the key goes when the sheet is saved; Replace key types a new one instead.</summary>
    public bool ShowsRemovedKey => _removeRequested && !_replacing;

    /// <summary>Replace key and Remove key's partner: Replace key is offered with a kept key and with a key about to be removed.</summary>
    public bool ShowsReplaceKey => ShowsKeptKey || ShowsRemovedKey;

    public IRelayCommand ReplaceKeyCommand { get; }

    public IRelayCommand RemoveKeyCommand { get; }

    private void ReplaceKey()
    {
        _replacing = true;
        _removeRequested = false;
        Key = "";
        RaiseKeyState();
    }

    private void RemoveKey()
    {
        if (!_hasKept)
        {
            return;
        }
        _removeRequested = true;
        _replacing = false;
        Key = "";
        RaiseKeyState();
    }

    private void RaiseKeyState()
    {
        OnPropertyChanged(nameof(ShowsKeyInput));
        OnPropertyChanged(nameof(ShowsKeptKey));
        OnPropertyChanged(nameof(ShowsRemovedKey));
        OnPropertyChanged(nameof(ShowsReplaceKey));
        _problem = Problem.None;
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
    }

    // ---- the address (SearXNG) ------------------------------------------------------------------------------------------

    /// <summary>The SearXNG instance's address.</summary>
    [ObservableProperty]
    private string _address = "";

    partial void OnAddressChanged(string value)
    {
        if (_problem is Problem.AddressMissing or Problem.AddressRefused)
        {
            _problem = Problem.None;
            OnPropertyChanged(nameof(ErrorText));
            OnPropertyChanged(nameof(HasError));
        }
    }

    // ---- lines ----------------------------------------------------------------------------------------------------------

    /// <summary>Where the key is kept: the key store, or this session only where the device has none.</summary>
    public string KeptWhereText => _store.KeepsKeyOnDisk ? Strings.WebSearchKeptStore : Strings.WebSearchKeptSession;

    /// <summary>The query leaves the organisation, and the assistant asks first unless it is set to Auto.</summary>
    public string PrivacyText => Strings.WebSearchPrivacy;

    /// <summary>Why the sheet cannot be saved; empty when nothing is wrong.</summary>
    public string ErrorText => _problem switch
    {
        Problem.KeyMissing => Strings.WebSearchKeyMissing,
        Problem.AddressMissing => Strings.WebSearchUrlMissing,
        Problem.AddressRefused => Strings.WebSearchUrlRefused,
        Problem.SaveFailed => Strings.WebSearchSaveFailed,
        _ => "",
    };

    public bool HasError => _problem != Problem.None;

    /// <summary>"Saved." after a save (the view on its own stays open; the Tools view's sheet closes).</summary>
    public string SavedText => _saved ? Strings.WebSearchSaved : "";

    public bool HasSaved => _saved;

    // ---- save and cancel ------------------------------------------------------------------------------------------------

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand CancelCommand { get; }

    private void Save()
    {
        if (_disposed)
        {
            return;
        }
        var typed = Key.Trim();
        WebSearchSettings settings;
        switch (Choice)
        {
            case WebSearchChoice.Brave:
                if (typed.Length == 0 && !(_hasKept && !_removeRequested))
                {
                    Fail(Problem.KeyMissing);
                    return;
                }
                settings = new WebSearchSettings { Provider = WebSearchProvider.Brave };
                break;
            case WebSearchChoice.SearXng:
                var text = Address.Trim();
                if (text.Length == 0)
                {
                    Fail(Problem.AddressMissing);
                    return;
                }
                if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || !WebSearchSettings.IsAllowedSearXngUrl(url))
                {
                    Fail(Problem.AddressRefused);
                    return;
                }
                settings = new WebSearchSettings { Provider = WebSearchProvider.SearXng, SearXngUrl = url };
                break;
            default:
                settings = WebSearchSettings.Off;
                break;
        }

        // A failed write (the disk, the key store) keeps the sheet open with what the person typed and says so, never why: the exception's
        // text could carry a path, and nothing here may carry the key.
        var before = _store.Settings;
        try
        {
            _store.Save(settings);
            if (Choice == WebSearchChoice.Brave && typed.Length > 0)
            {
                _store.SetBraveKey(typed);
            }
            else if (_removeRequested)
            {
                _store.SetBraveKey(null);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            try
            {
                _store.Save(before); // nothing half-written: the choice goes back to what it was
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            {
                // the disk is what failed: there is nothing more to do here
            }
            Fail(Problem.SaveFailed);
            return;
        }
        Key = ""; // the typed key goes
        _hasKept = _store.HasBraveKey;
        _replacing = false;
        _removeRequested = false;
        _problem = Problem.None;
        _saved = true;
        RaiseAll();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Fail(Problem problem)
    {
        _problem = problem;
        _saved = false;
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(SavedText));
        OnPropertyChanged(nameof(HasSaved));
    }

    private void Cancel()
    {
        if (_disposed)
        {
            return;
        }
        Key = "";
        // The sheet shows what the store keeps again; nothing was written.
        var settings = _store.Settings;
        Choice = settings.Provider switch
        {
            WebSearchProvider.Brave => WebSearchChoice.Brave,
            WebSearchProvider.SearXng => WebSearchChoice.SearXng,
            _ => WebSearchChoice.Off,
        };
        Address = settings.SearXngUrl?.AbsoluteUri ?? "";
        _hasKept = _store.HasBraveKey;
        _replacing = false;
        _removeRequested = false;
        _problem = Problem.None;
        _saved = false;
        RaiseAll();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseAll() => OnPropertyChanged(string.Empty);

    // The store changed (from any thread): the sheet knows again whether a key is kept, unless the person is changing it right now.
    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }
        if (_dispatcher.CheckAccess())
        {
            Follow();
        }
        else
        {
            _dispatcher.Post(Follow);
        }
    }

    private void Follow()
    {
        if (_disposed)
        {
            return;
        }
        var kept = _store.HasBraveKey;
        if (kept != _hasKept)
        {
            _hasKept = kept;
            if (!kept)
            {
                _removeRequested = false;
            }
            RaiseKeyState();
        }
    }

    /// <summary>The app's language changed: every text is said again in the new one.</summary>
    public void Retext() => RaiseAll();

    /// <summary>The sheet is saved, cancelled or replaced: no typed key stays here.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _store.Changed -= OnStoreChanged;
        Key = "";
    }
}
