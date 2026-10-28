using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Client.Tests;

/// <summary>One log entry as a logger received it: the message and every value of its state, and the exception.</summary>
public sealed record LoggedEntry(string Category, LogLevel Level, int EventId, string Message, IReadOnlyList<string> Values, string? Exception);

/// <summary>A logger factory that keeps everything it is given -- messages, state values, scopes, exceptions -- for tests.</summary>
public sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly List<LoggedEntry> _entries = new();
    private readonly List<string> _scopes = new();

    public IReadOnlyList<LoggedEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>Every string any logger saw: messages, state values, scopes and exceptions.</summary>
    public IReadOnlyList<string> Texts
    {
        get
        {
            lock (_entries)
            {
                return _entries.SelectMany(e => new[] { e.Message, e.Exception ?? "" }.Concat(e.Values)).Concat(_scopes).ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private static IReadOnlyList<string> Values(object? state) => state is IEnumerable<KeyValuePair<string, object?>> pairs
        ? pairs.Select(p => $"{p.Key}={p.Value}").ToList()
        : new[] { state?.ToString() ?? "" };

    private sealed class Logger : ILogger
    {
        private readonly CapturingLoggerFactory _factory;
        private readonly string _category;

        public Logger(CapturingLoggerFactory factory, string category)
        {
            _factory = factory;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            lock (_factory._entries)
            {
                _factory._scopes.AddRange(Values(state));
            }
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = new LoggedEntry(_category, logLevel, eventId.Id, formatter(state, exception), Values(state), exception?.ToString());
            lock (_factory._entries)
            {
                _factory._entries.Add(entry);
            }
        }
    }
}

/// <summary>The SDK's spans and metrics as a test sees them: every name and tag value as text.</summary>
public sealed class TelemetryCapture : IDisposable
{
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters = new();
    private readonly List<string> _texts = new();
    private readonly List<string> _spans = new();
    private readonly List<string> _measurements = new();

    public TelemetryCapture()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("Fadenstack.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Keep,
        };
        ActivitySource.AddActivityListener(_activities);
        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Fadenstack.Agents")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Keep(instrument.Name, value, tags));
        _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Keep(instrument.Name, value, tags));
        _meters.Start();
    }

    /// <summary>Every span name, tag, event and baggage item, and every metric name and tag, as text.</summary>
    public IReadOnlyList<string> Texts
    {
        get
        {
            lock (_texts)
            {
                return _texts.ToList();
            }
        }
    }

    /// <summary>The finished spans as <c>name | tag=value | ...</c>.</summary>
    public IReadOnlyList<string> Spans
    {
        get
        {
            lock (_texts)
            {
                return _spans.ToList();
            }
        }
    }

    /// <summary>The metric measurements as <c>name | tag=value | ...</c>.</summary>
    public IReadOnlyList<string> Measurements
    {
        get
        {
            lock (_texts)
            {
                return _measurements.ToList();
            }
        }
    }

    private void Keep(Activity activity)
    {
        var tags = activity.TagObjects.Select(t => $"{t.Key}={t.Value}")
            .Concat(activity.Events.SelectMany(e => new[] { e.Name }.Concat(e.Tags.Select(t => $"{t.Key}={t.Value}"))))
            .Concat(activity.Baggage.Select(b => $"{b.Key}={b.Value}")).ToList();
        lock (_texts)
        {
            _spans.Add(string.Join(" | ", new[] { activity.DisplayName }.Concat(tags)));
            _texts.Add(activity.DisplayName);
            _texts.AddRange(tags);
        }
    }

    private void Keep(string name, object value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var text = tags.ToArray().Select(t => $"{t.Key}={t.Value}").ToList();
        lock (_texts)
        {
            _measurements.Add(string.Join(" | ", new[] { name }.Concat(text)));
            _texts.Add(name);
            _texts.AddRange(text);
        }
    }

    public void Dispose()
    {
        _activities.Dispose();
        _meters.Dispose();
    }
}
