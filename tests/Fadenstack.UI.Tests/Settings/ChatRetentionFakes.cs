using Fadenstack.Agents;
using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Tests.Settings;

/// <summary>A state for the setting for old chats to show, and what was saved: for the view model's and the skins' tests.</summary>
internal sealed class FakeChatRetention : IChatRetentionSource
{
    public FakeChatRetention(ChatRetentionState state) => State = state;

    public ChatRetentionState State { get; private set; }

    public List<ChatRetentionChoice> Saved { get; } = new();

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ChatRetentionState> SetAsync(ChatRetentionChoice choice, CancellationToken cancellationToken)
    {
        if (State.CanChange)
        {
            Saved.Add(choice);
            State = new ChatRetentionState(ChatRetentionControl.Person, choice.AutoDelete, choice.MaxAgeDays, choice.MaxSessions);
        }
        return Task.FromResult(State);
    }

    /// <summary>The four states the setting shows, by name.</summary>
    public static FakeChatRetention Of(string name) => new(name switch
    {
        "person" => new ChatRetentionState(ChatRetentionControl.Person, true, 90, 50),
        "person-off" => new ChatRetentionState(ChatRetentionControl.Person, false, 90, 50),
        "organisation" => new ChatRetentionState(ChatRetentionControl.Organisation, true, 14, 30, OrganisationSetsLimit: true),
        "organisation-none" => new ChatRetentionState(ChatRetentionControl.Organisation, true, 90, 50),
        "policy" => new ChatRetentionState(ChatRetentionControl.MachinePolicy, true, 30, 100),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    });

    public static IReadOnlyList<string> Names { get; } = new[] { "person", "person-off", "organisation", "organisation-none", "policy" };
}
