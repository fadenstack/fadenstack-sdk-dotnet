using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace Fadenstack.UI.Services;

/// <summary>
/// What a view model does to tell its views that its commands may have changed: every command is told, one at a time, and a handler that throws
/// is logged and does not stop the rest (a loop over the commands that a single throw cuts short leaves the later commands, and the buttons
/// bound to them, as they were).
/// </summary>
internal static class CommandNotify
{
    /// <summary>Raises <c>CanExecuteChanged</c> of each command; the ones that are null are skipped.</summary>
    public static void All(params IRelayCommand?[] commands)
    {
        foreach (var command in commands)
        {
            One(command);
        }
    }

    /// <summary>Raises <c>CanExecuteChanged</c> of the command; a handler that throws is logged, never swallowed silently.</summary>
    public static void One(IRelayCommand? command)
    {
        if (command is null)
        {
            return;
        }
        try
        {
            command.NotifyCanExecuteChanged();
        }
        catch (Exception e)
        {
            Log(command, e);
        }
    }

    internal static void Log(object command, Exception e) =>
        Trace.TraceError($"Fadenstack.UI: a CanExecuteChanged handler of {command.GetType().Name} threw {e.GetType().Name}: {e.Message}");
}

/// <summary>The handlers of one command's <c>CanExecuteChanged</c>, each called on its own so that one that throws cannot stop the others.</summary>
internal sealed class CanExecuteChangedHandlers
{
    private readonly object _owner;
    private readonly object _gate = new();
    private EventHandler[] _handlers = Array.Empty<EventHandler>();

    public CanExecuteChangedHandlers(object owner) => _owner = owner;

    public void Add(EventHandler handler)
    {
        lock (_gate)
        {
            _handlers = _handlers.Concat(new[] { handler }).ToArray();
        }
    }

    public void Remove(EventHandler handler)
    {
        lock (_gate)
        {
            var at = Array.LastIndexOf(_handlers, handler);
            if (at >= 0)
            {
                _handlers = _handlers.Where((_, i) => i != at).ToArray();
            }
        }
    }

    public void Raise()
    {
        EventHandler[] handlers;
        lock (_gate)
        {
            handlers = _handlers;
        }
        foreach (var handler in handlers)
        {
            try
            {
                handler(_owner, EventArgs.Empty);
            }
            catch (Exception e)
            {
                CommandNotify.Log(_owner, e);
            }
        }
    }

    /// <summary>The command's parameter as a <typeparamref name="T"/>, or false when it is not one (WPF asks with <c>{DisconnectedItem}</c> for the container of an item that is gone).</summary>
    public static bool TryConvert<T>(object? parameter, out T? value)
    {
        if (parameter is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return parameter is null && default(T) is null;
    }
}

/// <summary>
/// A command for the items of a list (a button in each row, its row as the parameter), as <see cref="RelayCommand{T}"/> is but tolerant: asked with a
/// parameter that is not a <typeparamref name="T"/>, it answers false and does nothing. WPF asks that way with <c>{DisconnectedItem}</c>
/// (<c>BindingOperations.DisconnectedSource</c>) for the container of an item that is gone; <see cref="RelayCommand{T}"/> throws, which stopped the
/// loop that raised <c>CanExecuteChanged</c> for the view model's commands and left the later ones, "Use this" among them, as they were.
/// </summary>
internal sealed class ItemCommand<T> : IRelayCommand<T>
{
    private readonly RelayCommand<T> _inner;
    private readonly CanExecuteChangedHandlers _handlers;

    public ItemCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
    {
        _handlers = new CanExecuteChangedHandlers(this);
        _inner = canExecute is null ? new RelayCommand<T>(execute) : new RelayCommand<T>(execute, canExecute);
        _inner.CanExecuteChanged += (_, _) => _handlers.Raise();
    }

    public event EventHandler? CanExecuteChanged
    {
        add => _handlers.Add(value!);
        remove => _handlers.Remove(value!);
    }

    public void NotifyCanExecuteChanged() => _inner.NotifyCanExecuteChanged();

    public bool CanExecute(T? parameter) => _inner.CanExecute(parameter);

    public void Execute(T? parameter) => _inner.Execute(parameter);

    public bool CanExecute(object? parameter) => CanExecuteChangedHandlers.TryConvert<T>(parameter, out var value) && _inner.CanExecute(value);

    public void Execute(object? parameter)
    {
        if (CanExecuteChangedHandlers.TryConvert<T>(parameter, out var value))
        {
            _inner.Execute(value);
        }
    }
}

/// <summary>The same for a command that runs a task (<see cref="AsyncRelayCommand{T}"/>).</summary>
internal sealed class AsyncItemCommand<T> : IAsyncRelayCommand<T>
{
    private readonly AsyncRelayCommand<T> _inner;
    private readonly CanExecuteChangedHandlers _handlers;

    public AsyncItemCommand(Func<T?, Task> execute, Predicate<T?>? canExecute = null)
        : this(canExecute is null ? new AsyncRelayCommand<T>(execute) : new AsyncRelayCommand<T>(execute, canExecute))
    {
    }

    public AsyncItemCommand(Func<T?, CancellationToken, Task> execute, Predicate<T?>? canExecute = null)
        : this(canExecute is null ? new AsyncRelayCommand<T>(execute) : new AsyncRelayCommand<T>(execute, canExecute))
    {
    }

    private AsyncItemCommand(AsyncRelayCommand<T> inner)
    {
        _handlers = new CanExecuteChangedHandlers(this);
        _inner = inner;
        _inner.CanExecuteChanged += (_, _) => _handlers.Raise();
    }

    public event EventHandler? CanExecuteChanged
    {
        add => _handlers.Add(value!);
        remove => _handlers.Remove(value!);
    }

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add => _inner.PropertyChanged += value;
        remove => _inner.PropertyChanged -= value;
    }

    public Task? ExecutionTask => _inner.ExecutionTask;

    public bool CanBeCanceled => _inner.CanBeCanceled;

    public bool IsCancellationRequested => _inner.IsCancellationRequested;

    public bool IsRunning => _inner.IsRunning;

    public void Cancel() => _inner.Cancel();

    public void NotifyCanExecuteChanged() => _inner.NotifyCanExecuteChanged();

    public bool CanExecute(T? parameter) => _inner.CanExecute(parameter);

    public void Execute(T? parameter) => _inner.Execute(parameter);

    public Task ExecuteAsync(T? parameter) => _inner.ExecuteAsync(parameter);

    public bool CanExecute(object? parameter) => CanExecuteChangedHandlers.TryConvert<T>(parameter, out var value) && _inner.CanExecute(value);

    public void Execute(object? parameter)
    {
        if (CanExecuteChangedHandlers.TryConvert<T>(parameter, out var value))
        {
            _inner.Execute(value);
        }
    }

    public Task ExecuteAsync(object? parameter) =>
        CanExecuteChangedHandlers.TryConvert<T>(parameter, out var value) ? _inner.ExecuteAsync(value) : Task.CompletedTask;
}
