using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Storage;

namespace Fadenstack.Agents;

/// <summary>
/// What a person chose, in a standalone agent, for the old chats on their device: whether they are deleted automatically, and the
/// limits (how old, how many) when they are. Off keeps every chat until the person deletes it. The limits are whole numbers within
/// <see cref="MinDays"/>..<see cref="MaxDays"/> and <see cref="MinChats"/>..<see cref="MaxChats"/>.
/// </summary>
public sealed record ChatRetentionChoice
{
    /// <summary>The fewest days a chat may be kept.</summary>
    public const int MinDays = 1;

    /// <summary>The most days a limit may name (ten years).</summary>
    public const int MaxDays = 3650;

    /// <summary>The fewest chats a limit may keep.</summary>
    public const int MinChats = 1;

    /// <summary>The most chats a limit may name.</summary>
    public const int MaxChats = 10000;

    /// <param name="autoDelete">Delete old chats automatically (the default); false keeps every chat.</param>
    /// <param name="maxAgeDays">Chats older than this many days go (default 90).</param>
    /// <param name="maxSessions">Only the newest this many chats are kept (default 50).</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is outside its bounds (also when automatic deletion is off: the limits are kept for later).</exception>
    public ChatRetentionChoice(bool autoDelete = true, int maxAgeDays = 90, int maxSessions = 50)
    {
        if (!IsValidDays(maxAgeDays))
        {
            throw new ArgumentOutOfRangeException(nameof(maxAgeDays), $"The days must be whole numbers from {MinDays} to {MaxDays}.");
        }
        if (!IsValidChats(maxSessions))
        {
            throw new ArgumentOutOfRangeException(nameof(maxSessions), $"The chats must be whole numbers from {MinChats} to {MaxChats}.");
        }
        AutoDelete = autoDelete;
        MaxAgeDays = maxAgeDays;
        MaxSessions = maxSessions;
    }

    /// <summary>On, 90 days, 50 chats.</summary>
    public static ChatRetentionChoice Default { get; } = new();

    public bool AutoDelete { get; }

    public int MaxAgeDays { get; }

    public int MaxSessions { get; }

    public static bool IsValidDays(int days) => days is >= MinDays and <= MaxDays;

    public static bool IsValidChats(int chats) => chats is >= MinChats and <= MaxChats;

    /// <summary>The app's own limits as a choice that is on, brought within the bounds.</summary>
    internal static ChatRetentionChoice From(SessionRetention app) =>
        new(true, Math.Min(Math.Max(app.MaxAgeDays, MinDays), MaxDays), Math.Min(Math.Max(app.MaxSessions, MinChats), MaxChats));

    /// <summary>The limits to apply, or null when automatic deletion is off.</summary>
    internal SessionRetention? Limits => AutoDelete ? new SessionRetention(MaxSessions, MaxAgeDays) : null;
}

/// <summary>Who decides how long chats are kept on this device.</summary>
public enum ChatRetentionControl
{
    /// <summary>A standalone agent: the person, in their settings.</summary>
    Person,

    /// <summary>A governed agent: the organisation's server (<c>local_retention</c>), within the app's limits. The person has no setting.</summary>
    Organisation,

    /// <summary>A standalone agent on a computer whose machine policy fixes it (<c>chatRetention</c>). The person sees it and cannot change it.</summary>
    MachinePolicy,
}

/// <summary>What keeps old chats off a device now (<see cref="FadenstackAgent.ChatRetention"/>).</summary>
/// <param name="Control">Who decides.</param>
/// <param name="AutoDelete">Whether old chats are deleted automatically. Always true in governed mode, where the limits are the server's and the app's.</param>
/// <param name="MaxAgeDays">Chats older than this go.</param>
/// <param name="MaxSessions">Only the newest this many are kept.</param>
/// <param name="OrganisationSetsLimit">Governed mode only: whether the server set a limit (<c>local_retention</c>) at all.</param>
public sealed record ChatRetentionState(ChatRetentionControl Control, bool AutoDelete, int MaxAgeDays, int MaxSessions, bool OrganisationSetsLimit = false)
{
    /// <summary>Whether the person may change it: only in standalone, and only when the machine policy leaves it to them.</summary>
    public bool CanChange => Control == ChatRetentionControl.Person;

    /// <summary>The state as a choice (for a settings form to start from).</summary>
    public ChatRetentionChoice ToChoice() => new(AutoDelete,
        Math.Min(Math.Max(MaxAgeDays, ChatRetentionChoice.MinDays), ChatRetentionChoice.MaxDays),
        Math.Min(Math.Max(MaxSessions, ChatRetentionChoice.MinChats), ChatRetentionChoice.MaxChats));
}

/// <summary>
/// The choice each person made, on this device: one small file in the signed-in user's own folder
/// (<c>users\&lt;user&gt;\preferences\retention.json</c>; the shared <c>preferences</c> folder when the server names no user). It
/// holds a preference, not content, so it is not encrypted. Anything unreadable or out of bounds counts as no choice.
/// </summary>
internal sealed class ChatRetentionStore
{
    private readonly string _path;
    private readonly object _gate = new();

    internal ChatRetentionStore(string folder) => _path = Path.Combine(folder, "retention.json");

    internal ChatRetentionChoice? Get()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(_path));
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("autoDelete", out var auto) && auto.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && root.TryGetProperty("maxAgeDays", out var days) && days.ValueKind == JsonValueKind.Number && days.TryGetInt32(out var d)
                    && root.TryGetProperty("maxSessions", out var chats) && chats.ValueKind == JsonValueKind.Number && chats.TryGetInt32(out var c)
                    && ChatRetentionChoice.IsValidDays(d) && ChatRetentionChoice.IsValidChats(c))
                {
                    return new ChatRetentionChoice(auto.GetBoolean(), d, c);
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // damaged or unreadable: no choice
            }
            return null;
        }
    }

    internal void Set(ChatRetentionChoice choice)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!); // the user's data may have been deleted (FadenstackAgent.DeleteMyDataAsync)
                File.WriteAllText(_path, JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["autoDelete"] = choice.AutoDelete,
                    ["maxAgeDays"] = choice.MaxAgeDays,
                    ["maxSessions"] = choice.MaxSessions,
                }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the choice still holds for this run; it is just not remembered
            }
        }
    }
}
