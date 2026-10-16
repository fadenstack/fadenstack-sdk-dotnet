using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Models;

namespace Fadenstack.UI.ViewModels;

/// <summary>Where a skill is asked for in the composer's text, and what follows the <c>$</c> (<see cref="Find"/>).</summary>
public sealed record SkillToken(string Query, int Start)
{
    // \z, not $: .NET's $ also matches before a final line break, and a $ there is not at the end of the text.
    private static readonly Regex Dollar = new(@"(?:^|\s)\$([\p{L}\p{N}_.-]*)\z", RegexOptions.CultureInvariant);
    private static readonly Regex Slash = new(@"^/([\p{L}\p{N}_.-]*)\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>$name</c> at the end of the text (after a space, or at its start), or <c>/name</c> as the whole of it; null for
    /// anything else, so a price ("costs $5 now"), "US$" or a path stays text. <see cref="Start"/> is where the token begins.
    /// </summary>
    public static SkillToken? Find(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }
        var dollar = Dollar.Match(input);
        if (dollar.Success)
        {
            var query = dollar.Groups[1].Value;
            return new SkillToken(query, input!.Length - query.Length - 1);
        }
        var slash = Slash.Match(input);
        return slash.Success ? new SkillToken(slash.Groups[1].Value, 0) : null;
    }
}

/// <summary>
/// The list that opens when the user types <c>$</c> (or <c>/</c> at the start) in the composer: the skills they can pick
/// for the message, narrowed as they type; the arrows move, Enter or Tab picks, Esc closes. The chat owns it
/// (<see cref="AgentChatViewModel.SkillPicker"/>) and opens and closes it as the text changes. It reads the list once,
/// and again after a few minutes or after it could not.
/// </summary>
public sealed class SkillPickerViewModel : ObservableObject
{
    /// <summary>Skills listed at most; typing narrows them.</summary>
    public const int Shown = 8;

    /// <summary>How long a list read is used before the picker reads it again.</summary>
    private static readonly TimeSpan ListLifetime = TimeSpan.FromMinutes(5);

    private static int _counter;

    private static readonly string[] State =
    {
        nameof(IsOpen), nameof(Query), nameof(IsLoading), nameof(Failed), nameof(Options), nameof(HasOptions), nameof(HasSkills),
        nameof(ActiveIndex), nameof(Note),
    };

    private readonly Func<CancellationToken, Task<IReadOnlyList<SkillOption>>> _load;
    private readonly Action<SkillOption> _onPick;
    private readonly Func<UiStrings> _strings;
    private readonly Func<DateTimeOffset> _clock;
    private IReadOnlyList<SkillOption>? _all;
    private DateTimeOffset _loadedAt;
    private bool _reading;
    private bool _failed;
    private bool _isOpen;
    private string _query = "";
    private int _active;
    private IReadOnlyList<SkillOption> _options = Array.Empty<SkillOption>();

    /// <param name="load">Reads the skills the user can pick (<see cref="Agents.FadenstackAgent.ListSkillsAsync"/>).</param>
    /// <param name="onPick">Takes the skill the user picked.</param>
    /// <param name="strings">The chat's texts, for <see cref="Note"/>; the current UI language's by default.</param>
    /// <param name="clock">For the list's lifetime.</param>
    public SkillPickerViewModel(Func<CancellationToken, Task<IReadOnlyList<SkillOption>>> load, Action<SkillOption> onPick,
        Func<UiStrings>? strings = null, Func<DateTimeOffset>? clock = null)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _onPick = onPick ?? throw new ArgumentNullException(nameof(onPick));
        var current = UiStrings.Current;
        _strings = strings ?? (() => current);
        _clock = clock ?? (() => DateTimeOffset.Now);
        Key = "fdn-skills-" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture);
        PickCommand = new RelayCommand<SkillOption>(skill => Pick(skill));
    }

    /// <summary>Unique among the pickers of the process: for the automation ids a skin gives the list and its options.</summary>
    public string Key { get; }

    public bool IsOpen => _isOpen;

    /// <summary>What follows the <c>$</c>.</summary>
    public string Query => _query;

    /// <summary>The list is being read for the first time.</summary>
    public bool IsLoading => _reading && _all is null;

    /// <summary>The list could not be read (the picker says so; typing on tries again).</summary>
    public bool Failed => _failed;

    /// <summary>What the list shows: the matches, the best first, <see cref="Shown"/> at most.</summary>
    public IReadOnlyList<SkillOption> Options => _options;

    public bool HasOptions => _options.Count > 0;

    /// <summary>Whether the user can pick any skill at all, once the list is read.</summary>
    public bool HasSkills => _all is { Count: > 0 };

    /// <summary>The option Enter picks.</summary>
    public int ActiveIndex => Math.Min(_active, Math.Max(0, _options.Count - 1));

    /// <summary>What the picker says instead of options: loading, could not load, nothing matches, none to pick; else empty.</summary>
    public string Note
    {
        get
        {
            var strings = _strings();
            return IsLoading ? strings.SkillsLoading
                : _failed ? strings.SkillsFailed
                : _options.Count > 0 ? ""
                : HasSkills ? strings.NoSkillMatches(_query)
                : strings.NoSkills;
        }
    }

    /// <summary>Picks the skill clicked (or the active one, without a parameter).</summary>
    public IRelayCommand<SkillOption> PickCommand { get; }

    /// <summary>The skills that match <paramref name="query"/>: by name or slug first, then by description, without regard to case or accents; all of them for an empty one.</summary>
    public static IReadOnlyList<SkillOption> Match(IEnumerable<SkillOption> skills, string? query)
    {
        var q = Fold((query ?? "").Trim());
        var all = skills.ToList();
        if (q.Length == 0)
        {
            return all;
        }
        var named = new List<SkillOption>();
        var described = new List<SkillOption>();
        foreach (var skill in all)
        {
            if (Fold(skill.Name).Contains(q) || Fold(skill.Slug).Contains(q))
            {
                named.Add(skill);
            }
            else if (Fold(skill.Description).Contains(q))
            {
                described.Add(skill);
            }
        }
        return named.Concat(described).ToList();
    }

    /// <summary>Opens the list (or narrows it) for what follows the <c>$</c>.</summary>
    public void Show(string query)
    {
        query ??= "";
        var changed = !_isOpen || query != _query;
        _isOpen = true;
        if (query != _query)
        {
            _active = 0;
        }
        _query = query;
        Recompute();
        if (_all is null || _clock() - _loadedAt > ListLifetime || _failed)
        {
            Read();
        }
        if (changed)
        {
            Changed();
        }
    }

    public void Close()
    {
        if (!_isOpen)
        {
            return;
        }
        _isOpen = false;
        _query = "";
        _active = 0;
        Recompute();
        Changed();
    }

    /// <summary>Moves the choice down (1) or up (-1), round the list.</summary>
    public void Move(int step)
    {
        var count = _options.Count;
        if (count == 0)
        {
            return;
        }
        _active = ((ActiveIndex + step) % count + count) % count;
        OnPropertyChanged(nameof(ActiveIndex));
    }

    /// <summary>Picks a skill (the active one when none is given); the chat takes it for the message.</summary>
    public void Pick(SkillOption? skill = null)
    {
        skill ??= _options.Count > 0 ? _options[ActiveIndex] : null;
        if (skill is null)
        {
            return;
        }
        Close();
        _onPick(skill);
    }

    /// <summary>The chat's language changed: the note is said in the new one.</summary>
    internal void Retext() => OnPropertyChanged(nameof(Note));

    private void Read()
    {
        if (_reading)
        {
            return;
        }
        // Set before the read starts, cleared when it ends: also for a list that comes at once.
        _reading = true;
        _failed = false;
        Changed();
        _ = LoadAsync();
    }

    /// <summary>Reads the list; never throws (a failure is <see cref="Failed"/>).</summary>
    private async Task LoadAsync()
    {
        try
        {
            _all = await _load(CancellationToken.None);
            _loadedAt = _clock();
        }
        catch (Exception)
        {
            _failed = _all is null; // an older list still serves
        }
        finally
        {
            _reading = false;
            Recompute();
            Changed();
        }
    }

    private void Recompute() => _options = _all is null ? Array.Empty<SkillOption>() : Match(_all, _query).Take(Shown).ToList();

    private void Changed()
    {
        foreach (var name in State)
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Lower case without accents, so "vertragspruf" finds "Vertragsprüfung".</summary>
    private static string Fold(string? text)
    {
        var decomposed = (text ?? "").Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                builder.Append(c);
            }
        }
        return builder.ToString().ToLowerInvariant();
    }
}
