using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Fadenstack.Storage;
using Microsoft.Extensions.Logging;

namespace Fadenstack.Logging;

/// <summary>How <see cref="FadenstackFileLoggerProvider"/> keeps its files.</summary>
public sealed class FadenstackFileLoggerOptions
{
    /// <summary>
    /// The folder, which must be <see cref="FadenstackPaths.LogRoot"/> or below it (anything else is refused: logs stay in the
    /// OS user's own folder). Default: <see cref="FadenstackPaths.Logs"/> without a user, i.e. the log root itself.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>A file is closed and numbered (<c>.1</c>, <c>.2</c>, ...) once it would pass this size. Default 5 MB.</summary>
    public long MaxFileBytes { get; set; } = 5L * 1024 * 1024;

    /// <summary>Files older than this are deleted. Default 14 days.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(14);

    /// <summary>The oldest files are deleted while all of the app's files together are bigger than this. Default 50 MB.</summary>
    public long MaxTotalBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Entries waiting for the writer; more are dropped (and counted) instead of holding up the caller. Default 4096.</summary>
    public int QueueCapacity { get; set; } = 4096;
}

/// <summary>
/// Writes log entries to rolling files in the OS user's own log folder (<see cref="FadenstackPaths.LogRoot"/>):
/// <c>&lt;app&gt;-&lt;yyyyMMdd&gt;.log</c>, one line per entry (UTC time, level, category, event id, message, and for an
/// exception its type, message and stack trace). A file that would pass <see cref="FadenstackFileLoggerOptions.MaxFileBytes"/>
/// is numbered and a new one started; files older than <see cref="FadenstackFileLoggerOptions.MaxAge"/>, and the oldest ones
/// beyond <see cref="FadenstackFileLoggerOptions.MaxTotalBytes"/>, are deleted at start and whenever a file rolls. Only this
/// app's files are touched: add-ins and the desktop agent share the folder.
/// <para>
/// Logging never holds up or fails the caller: entries go to a bounded queue and one background writer; when the queue is
/// full an entry is dropped and counted (<see cref="Dropped"/>, and a line in the file). Dispose writes what is queued.
/// </para>
/// <para>
/// The SDK's own entries carry no content: no prompts, answers, reasoning, attachments, tool arguments or results, tokens,
/// secrets, and not who the user is. What a host logs through the same <see cref="ILoggerFactory"/> is the host's
/// responsibility: this provider writes every entry it is given as it is.
/// </para>
/// </summary>
/// <example>
/// <code>
/// using var files = new FadenstackFileLoggerProvider("my-addin");
/// using var factory = LoggerFactory.Create(b => b.AddProvider(files));
/// </code>
/// </example>
public sealed class FadenstackFileLoggerProvider : ILoggerProvider
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly string _app;
    private readonly string _folder;
    private readonly long _maxFileBytes;
    private readonly TimeSpan _maxAge;
    private readonly long _maxTotalBytes;
    private readonly Regex _ownFiles;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ManualResetEventSlim? _gate;
    private readonly BlockingCollection<Entry> _queue;
    private readonly Thread _writer;
    private long _dropped;
    private long _reported;
    private int _disposed;
    private FileStream? _file;
    private string? _path;
    private long _size;

    private readonly record struct Entry(DateTimeOffset At, string Line);

    /// <param name="app">
    /// The app's name, in the file names (<c>&lt;app&gt;-20261028.log</c>); made safe as a file name with
    /// <see cref="FadenstackPaths.SafeName"/>.
    /// </param>
    /// <param name="options">Folder, size and age limits; the defaults if not given.</param>
    /// <exception cref="ArgumentException">The folder is not under <see cref="FadenstackPaths.LogRoot"/>.</exception>
    public FadenstackFileLoggerProvider(string app, FadenstackFileLoggerOptions? options = null)
        : this(app, options, null, null)
    {
    }

    /// <param name="app">The app's name.</param>
    /// <param name="options">Folder, size and age limits.</param>
    /// <param name="clock">The time, for tests.</param>
    /// <param name="writerGate">Holds the writer until set, for tests that fill the queue.</param>
    internal FadenstackFileLoggerProvider(string app, FadenstackFileLoggerOptions? options, Func<DateTimeOffset>? clock,
        ManualResetEventSlim? writerGate)
    {
        options ??= new FadenstackFileLoggerOptions();
        if (string.IsNullOrWhiteSpace(app))
        {
            throw new ArgumentException("An app name is required.", nameof(app));
        }
        _app = FadenstackPaths.SafeName(app);
        _folder = options.Folder is null ? FadenstackPaths.Logs(null) : FadenstackPaths.LogFolder(options.Folder);
        _maxFileBytes = Math.Max(1, options.MaxFileBytes);
        _maxAge = options.MaxAge;
        _maxTotalBytes = options.MaxTotalBytes;
        _ownFiles = new Regex($@"^{Regex.Escape(_app)}-\d{{8}}\.log(\.\d+)?$", RegexOptions.CultureInvariant);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _gate = writerGate;
        _queue = new BlockingCollection<Entry>(Math.Max(1, options.QueueCapacity));
        DeleteOld();
        _writer = new Thread(Run) { IsBackground = true, Name = "Fadenstack file logger" };
        _writer.Start();
    }

    /// <summary>How many entries were dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _queue.CompleteAdding();
        if (_writer.Join(TimeSpan.FromSeconds(5)))
        {
            _queue.Dispose();
        }
    }

    private void Enqueue(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        var at = _clock();
        var line = new StringBuilder()
            .Append(at.UtcDateTime.ToString("O")).Append(' ').Append(Abbreviation(level)).Append(' ').Append(category).Append(' ')
            .Append(eventId.Id).Append(' ').Append(OneLine(message));
        if (exception is not null)
        {
            line.Append(" | ").Append(exception.GetType().FullName).Append(": ").Append(OneLine(exception.Message))
                .Append(" | ").Append(OneLine(exception.StackTrace ?? ""));
        }
        try
        {
            if (!_queue.TryAdd(new Entry(at, line.ToString())))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        catch (InvalidOperationException)
        {
            // disposed: nothing to write to any more
        }
    }

    private static string OneLine(string text) => text.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "CRT",
    };

    private void Run()
    {
        while (true)
        {
            _gate?.Wait();
            if (!_queue.TryTake(out var entry, Timeout.Infinite))
            {
                break;
            }
            Write(entry);
            NoteDropped();
        }
        NoteDropped();
        CloseFile();
    }

    /// <summary>A line in the file for the entries dropped since the last one.</summary>
    private void NoteDropped()
    {
        var dropped = Dropped;
        if (dropped > _reported)
        {
            var now = _clock();
            Write(new Entry(now, $"{now.UtcDateTime:O} WRN Fadenstack.Logging 0 {dropped - _reported} entries were dropped: the queue was full"));
            _reported = dropped;
        }
    }

    private void Write(Entry entry)
    {
        try
        {
            var bytes = Utf8.GetBytes(entry.Line + Environment.NewLine);
            var path = Path.Combine(_folder, $"{_app}-{entry.At.UtcDateTime:yyyyMMdd}.log");
            if (_file is null || _path != path)
            {
                Open(path);
                DeleteOld();
            }
            else if (_size > 0 && _size + bytes.Length > _maxFileBytes)
            {
                Roll();
            }
            _file!.Write(bytes, 0, bytes.Length);
            _file.Flush();
            _size += bytes.Length;
        }
        catch (Exception)
        {
            CloseFile(); // the next entry tries again; a log never fails its caller
        }
    }

    private void Open(string path)
    {
        CloseFile();
        _file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _path = path;
        _size = _file.Length;
    }

    /// <summary>Closes the file under the next free number (<c>.1</c> is the oldest) and starts a new one.</summary>
    private void Roll()
    {
        var path = _path!;
        CloseFile();
        var last = new DirectoryInfo(_folder).EnumerateFiles(Path.GetFileName(path) + ".*")
            .Select(f => int.TryParse(f.Extension.TrimStart('.'), out var n) ? n : 0).DefaultIfEmpty(0).Max();
        File.Move(path, $"{path}.{last + 1}");
        Open(path);
        DeleteOld();
    }

    private void CloseFile()
    {
        _file?.Dispose();
        _file = null;
        _path = null;
    }

    /// <summary>Deletes this app's files that are too old, then the oldest while they are too big together; never the open one.</summary>
    private void DeleteOld()
    {
        try
        {
            var cutoff = _clock().UtcDateTime - _maxAge;
            var files = new DirectoryInfo(_folder).EnumerateFiles().Where(f => _ownFiles.IsMatch(f.Name))
                .OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var file in files.Where(f => f.FullName != _path))
            {
                if (file.LastWriteTimeUtc < cutoff || total > _maxTotalBytes)
                {
                    total -= file.Length;
                    file.Delete();
                }
            }
        }
        catch (Exception)
        {
            // what stays is deleted next time
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FadenstackFileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FadenstackFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NoScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            try
            {
                _provider.Enqueue(_category, logLevel, eventId, formatter(state, exception), exception);
            }
            catch (Exception)
            {
                // a log never fails its caller
            }
        }
    }

    private sealed class NoScope : IDisposable
    {
        internal static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}
