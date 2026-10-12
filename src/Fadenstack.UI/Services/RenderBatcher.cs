using System.Text;

namespace Fadenstack.UI.Services;

/// <summary>
/// Moves work from the agent's thread to the UI thread in batches: at most one flush per interval, whatever
/// the rate of events. Consecutive text for the same target is merged into one append, so a fast stream costs
/// one update per frame, not one per token. Order is kept.
/// </summary>
internal sealed class RenderBatcher
{
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private readonly object _flushing = new();
    private List<object> _queue = new();
    private bool _scheduled;

    public RenderBatcher(IUiDispatcher dispatcher, TimeSpan interval)
    {
        _dispatcher = dispatcher;
        _interval = interval;
    }

    public void Enqueue(Action action)
    {
        lock (_gate)
        {
            _queue.Add(action);
        }
        Schedule();
    }

    /// <summary>Appends <paramref name="text"/> for <paramref name="target"/>, merged with text already waiting for it.</summary>
    public void EnqueueText(object target, string text, Action<string> apply)
    {
        lock (_gate)
        {
            if (_queue.Count > 0 && _queue[_queue.Count - 1] is TextOp last && ReferenceEquals(last.Target, target))
            {
                last.Text.Append(text);
                return;
            }
            _queue.Add(new TextOp(target, new StringBuilder(text), apply));
        }
        Schedule();
    }

    /// <summary>Completes once everything queued before it has run on the UI thread.</summary>
    public Task WhenFlushedAsync()
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() => done.TrySetResult(true));
        return done.Task;
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_scheduled)
            {
                return;
            }
            _scheduled = true;
        }
        if (_interval <= TimeSpan.Zero)
        {
            _dispatcher.Post(Flush);
        }
        else
        {
            _ = Task.Delay(_interval).ContinueWith(_ => _dispatcher.Post(Flush), TaskScheduler.Default);
        }
    }

    private void Flush()
    {
        lock (_flushing) // an inline dispatcher can flush from several threads
        {
            List<object> batch;
            lock (_gate)
            {
                batch = _queue;
                _queue = new List<object>();
                _scheduled = false;
            }
            foreach (var op in batch)
            {
                switch (op)
                {
                    case TextOp text:
                        text.Apply(text.Text.ToString());
                        break;
                    case Action action:
                        action();
                        break;
                }
            }
        }
    }

    private sealed class TextOp
    {
        public TextOp(object target, StringBuilder text, Action<string> apply)
        {
            Target = target;
            Text = text;
            Apply = apply;
        }

        public object Target { get; }

        public StringBuilder Text { get; }

        public Action<string> Apply { get; }
    }
}
