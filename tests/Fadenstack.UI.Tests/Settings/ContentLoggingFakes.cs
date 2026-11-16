using Fadenstack.UI.Settings;

namespace Fadenstack.UI.Tests.Settings;

/// <summary>A content logging state for the setting and the band to show, and what was started: for the view models' and the skins' tests.</summary>
internal sealed class FakeContentLogging : IContentLoggingSource
{
    public FakeContentLogging(bool canStart, bool isOn = false, DateTimeOffset? until = null)
    {
        CanStart = canStart;
        IsOn = isOn;
        Until = until;
    }

    public bool CanStart { get; set; }

    public bool IsOn { get; private set; }

    public DateTimeOffset? Until { get; private set; }

    public List<TimeSpan> Started { get; } = new();

    public int Stops { get; private set; }

    public event EventHandler? Changed;

    public void Start(TimeSpan duration)
    {
        Started.Add(duration);
        IsOn = true;
        Until = DateTimeOffset.UtcNow + duration;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        Stops++;
        IsOn = false;
        Until = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The time ran out by itself.</summary>
    public void EndByItself() => Stop();

    /// <summary>The states the setting shows, by name.</summary>
    public static FakeContentLogging Of(string name) => name switch
    {
        "off" => new FakeContentLogging(true),
        "on" => new FakeContentLogging(true, true, DateTimeOffset.UtcNow.AddMinutes(90)),
        "unavailable" => new FakeContentLogging(false),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static IReadOnlyList<string> Names { get; } = new[] { "off", "on", "unavailable" };
}
