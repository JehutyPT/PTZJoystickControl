using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace PtzJoystickControl.Application.Logging;

/// <summary>
/// The app's single logging path. Registered in <see cref="Trace.Listeners"/>, it turns
/// every <see cref="Trace"/> call (and <c>Debug</c> calls in Debug builds) into one line:
/// <c>2026-10-13T14:03:12.345+01:00 ERROR [CallingType] message</c>, with CR/LF in the
/// message escaped as <c>\n</c>. The source is the calling type, falling back to the
/// trace source or category, then to <c>App</c>. Each line is kept in a bounded in-memory
/// history (<see cref="Snapshot"/>), published to subscribers (the Log window) and queued
/// for the attached <see cref="RollingLogFile"/>. Never throws to the caller. Subscribers
/// run on the logging thread while a lock is held, so they must only hand off (e.g. post
/// to the UI thread).
/// <para>
/// Disk I/O never happens on the logging thread (it may be the SDL input loop or the VISCA
/// send path). One background writer thread owns the file: it writes, flushes and rolls
/// it, in the order entries were logged, with the timestamp taken when they were logged.
/// The queue is bounded: when it is full, entries are dropped rather than waited for, and
/// one WARN entry with the count is written ahead of the next entry that fits. The
/// exceptions are an unhandled exception, whose CRITICAL entry waits a bounded time for
/// room and for the writer to put it on disk (the process is terminating anyway), and
/// process exit or <see cref="TraceListener.Dispose()"/>, which give the writer a bounded
/// time to finish the queue.
/// </para>
/// </summary>
public sealed class AppLogListener : TraceListener
{
    public const int DefaultCapacity = 1000;
    public const int DefaultQueueCapacity = 10_000;

    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _capacity;
    private readonly Queue<string> _recent;
    private readonly List<Action<string>> _subscribers = new();
    private readonly StringBuilder _pending = new();
    private FileWriter? _writer;      // guarded by _lock
    private long _lastQueued;         // guarded by _lock: sequence number of the newest queued entry
    private int _dropped;             // guarded by _lock: entries dropped since the last notice
    private bool _processExitHooked;  // guarded by _lock
    private readonly object _progressLock = new();
    private long _written;            // guarded by _progressLock: newest sequence number the writer finished

    public AppLogListener() : this(() => DateTimeOffset.Now) { }

    public AppLogListener(Func<DateTimeOffset> clock, int capacity = DefaultCapacity)
    {
        _clock = clock;
        _capacity = Math.Max(1, capacity);
        _recent = new Queue<string>(_capacity);
    }

    public override bool IsThreadSafe => true;

    /// <summary>
    /// Names the type that logged, or null when none can be found. Settable so tests can
    /// exercise the fallback, which a managed caller never reaches.
    /// </summary>
    internal Func<string?> ResolveCaller { get; init; } = CallerName;

    /// <summary>Most entries waiting for the file writer before new ones are dropped.</summary>
    internal int QueueCapacity { get; init; } = DefaultQueueCapacity;

    /// <summary>
    /// How long an unhandled exception, process exit or <see cref="TraceListener.Dispose()"/>
    /// waits for the file writer.
    /// </summary>
    internal TimeSpan FlushTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs on the writer thread before each line is written. Lets tests slow down, block
    /// or fail the writer.
    /// </summary>
    internal Action<string>? BeforeFileWrite { get; init; }

    /// <summary>
    /// Starts writing every following entry to <paramref name="file"/> on a background
    /// writer thread, which first rolls the file if an earlier run left it oversized. Also
    /// hooks <see cref="AppDomain.ProcessExit"/> so the queue is written before the process
    /// ends.
    /// </summary>
    public void AttachFile(RollingLogFile file)
    {
        try
        {
            FileWriter? previous;
            lock (_lock)
            {
                previous = _writer;
                _writer = StartWriter(file);
                if (!_processExitHooked)
                {
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                    _processExitHooked = true;
                }
            }
            previous?.Stop(FlushTimeout);
        }
        catch { }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> until the file writer has handled every entry
    /// queued so far. True if it has (or there is no file). Never called on the input path.
    /// </summary>
    internal bool WaitForWriter(TimeSpan timeout)
    {
        long target;
        lock (_lock)
            target = _lastQueued;
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_progressLock)
        {
            while (_written < target)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0)
                    return false;
                Monitor.Wait(_progressLock, (int)Math.Min(left, int.MaxValue));
            }
        }
        return true;
    }

    /// <summary>
    /// Registers <paramref name="onEntry"/> for every following entry and returns the
    /// recent entries logged before it, atomically, so nothing is missed or repeated.
    /// </summary>
    public IReadOnlyList<string> Subscribe(Action<string> onEntry)
    {
        lock (_lock)
        {
            _subscribers.Add(onEntry);
            return _recent.ToArray();
        }
    }

    public void Unsubscribe(Action<string> onEntry)
    {
        lock (_lock)
            _subscribers.Remove(onEntry);
    }

    /// <summary>The recent entries, oldest first, bounded to the listener's capacity.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
            return _recent.ToArray();
    }

    /// <summary>Empties the recent entries (the Log window's Clear). The file is unchanged.</summary>
    public void ClearRecent()
    {
        lock (_lock)
            _recent.Clear();
    }

    /// <summary>
    /// Handler for <see cref="AppDomain.UnhandledException"/>: logs CRITICAL and waits up to
    /// <see cref="FlushTimeout"/> in all for room in the queue and for the writer to flush
    /// the entry to disk. Never throws.
    /// </summary>
    public void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            long deadline = Environment.TickCount64 + (long)FlushTimeout.TotalMilliseconds;
            Log(TraceEventType.Critical, "AppDomain",
                $"Unhandled exception (terminating: {(e.IsTerminating ? "true" : "false")}): {e.ExceptionObject?.ToString() ?? "(null)"}",
                crashDeadline: deadline);
            WaitForWriter(TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64)));
        }
        catch { }
    }

    /// <summary>
    /// Handler for <see cref="TaskScheduler.UnobservedTaskException"/>: logs ERROR and marks
    /// the exception observed, even if logging fails. Never throws.
    /// </summary>
    public void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            Log(TraceEventType.Error, "TaskScheduler", $"Unobserved task exception: {e.Exception?.ToString() ?? "(null)"}");
        }
        catch { }
        try
        {
            e.SetObserved();
        }
        catch { }
    }

    public override void Write(string? message)
    {
        try
        {
            lock (_lock)
                _pending.Append(message);
        }
        catch { }
    }

    public override void WriteLine(string? message) => Log(TraceEventType.Information, null, message, joinPending: true);

    public override void WriteLine(string? message, string? category)
        => Log(TraceEventType.Information, null, message, joinPending: true, fallbackSource: category);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id)
        => Log(eventType, null, string.Empty, fallbackSource: source);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        => Log(eventType, null, message, fallbackSource: source);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
    {
        string? message;
        try
        {
            message = args == null || args.Length == 0 || format == null
                ? format
                : string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            message = format;
        }
        Log(eventType, null, message, fallbackSource: source);
    }

    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, object? data)
        => Log(eventType, null, data?.ToString(), fallbackSource: source);

    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, params object?[]? data)
        => Log(eventType, null, data == null ? null : string.Join(", ", data), fallbackSource: source);

    /// <summary>
    /// Does nothing: the writer flushes every line it writes. <see cref="Trace.AutoFlush"/>
    /// calls this after every entry, so it must never wait on the writer.
    /// </summary>
    public override void Flush() { }

    /// <summary>Stops the file writer, giving it up to <see cref="FlushTimeout"/> to finish the queue.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                FileWriter? writer;
                lock (_lock)
                {
                    writer = _writer;
                    _writer = null;
                    if (_processExitHooked)
                    {
                        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                        _processExitHooked = false;
                    }
                }
                writer?.Stop(FlushTimeout);
            }
            catch { }
        }
        base.Dispose(disposing);
    }

    /// <summary>Formats one entry; see docs/development.md for the format.</summary>
    internal static string FormatLine(DateTimeOffset timestamp, TraceEventType level, string source, string? message)
    {
        string text = (message ?? string.Empty)
            .Replace("\r\n", "\\n")
            .Replace('\r', '\n')
            .Replace("\n", "\\n");
        return $"{timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)} {LevelName(level)} [{source}] {text}";
    }

    internal static string LevelName(TraceEventType level) => level switch
    {
        TraceEventType.Critical => "CRITICAL",
        TraceEventType.Error => "ERROR",
        TraceEventType.Warning => "WARN",
        TraceEventType.Verbose => "VERBOSE",
        _ => "INFO",
    };

    /// <param name="source">Fixed source (e.g. <c>AppDomain</c>); otherwise the calling type.</param>
    /// <param name="fallbackSource">Trace source or category, used when the caller is unknown.</param>
    /// <param name="crashDeadline">
    /// For the unhandled-exception entry: the <see cref="Environment.TickCount64"/> until
    /// which it waits for room in the queue.
    /// </param>
    private void Log(TraceEventType level, string? source, string? message, bool joinPending = false, string? fallbackSource = null,
        long? crashDeadline = null)
    {
        try
        {
            // Resolve the caller outside the lock; it walks the stack.
            source ??= ResolveCaller()
                ?? (string.IsNullOrWhiteSpace(fallbackSource) ? null : fallbackSource)
                ?? "App";
            lock (_lock)
            {
                if (_pending.Length > 0)
                {
                    // Write fragments belong to the next WriteLine. A TraceEvent never
                    // completes them, so they are flushed as their own entry first.
                    string fragment = _pending.ToString();
                    _pending.Clear();
                    if (joinPending)
                        message = fragment + message;
                    else
                        Emit(FormatLine(_clock(), TraceEventType.Information, source, fragment));
                }
                Emit(FormatLine(_clock(), level, source, message), crashDeadline);
            }
        }
        catch
        {
            // Logging must never take down the caller (SDL loop, sockets, UI).
        }
    }

    private void Emit(string line, long? crashDeadline = null)
    {
        if (_writer != null)
            QueueForFile(_writer, line, crashDeadline);

        if (_recent.Count >= _capacity)
            _recent.Dequeue();
        _recent.Enqueue(line);

        foreach (var subscriber in _subscribers.ToArray())
        {
            try { subscriber(line); }
            catch { }
        }
    }

    /// <summary>
    /// Queues <paramref name="line"/> for the writer, preceded by a notice if entries were
    /// dropped. Called under <see cref="_lock"/>, which keeps the queue in logging order.
    /// Never blocks, except for the crash entry, which waits until
    /// <paramref name="crashDeadline"/> for room.
    /// </summary>
    private void QueueForFile(FileWriter writer, string line, long? crashDeadline)
    {
        if (_dropped > 0)
        {
            string notice = FormatLine(_clock(), TraceEventType.Warning, nameof(AppLogListener),
                $"Dropped {_dropped} log entries because the log file writer fell behind");
            if (!TryQueue(writer, notice, crashDeadline, flushToDisk: false))
            {
                _dropped++;
                return;
            }
            _dropped = 0;
        }
        if (!TryQueue(writer, line, crashDeadline, flushToDisk: crashDeadline != null))
            _dropped++;
    }

    /// <returns>False only when the queue is full.</returns>
    private bool TryQueue(FileWriter writer, string line, long? crashDeadline, bool flushToDisk)
    {
        var entry = new FileEntry(_lastQueued + 1, line, flushToDisk);
        bool queued;
        try
        {
            queued = crashDeadline is long deadline
                ? writer.Queue.TryAdd(entry, (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue))
                : writer.Queue.TryAdd(entry);
        }
        catch (InvalidOperationException)
        {
            return true; // The writer is stopping: not a drop to report.
        }
        if (queued)
            _lastQueued = entry.Sequence;
        return queued;
    }

    private FileWriter StartWriter(RollingLogFile file)
    {
        var writer = new FileWriter(file, new BlockingCollection<FileEntry>(Math.Max(1, QueueCapacity)));
        writer.Thread = new Thread(() => RunWriter(writer))
        {
            IsBackground = true, // never keeps the process alive; OnProcessExit gives it time to finish
            Name = "Log file writer",
        };
        writer.Thread.Start();
        return writer;
    }

    /// <summary>The writer thread: the only place the file is written, flushed or rolled.</summary>
    private void RunWriter(FileWriter writer)
    {
        try
        {
            writer.File.RollIfOversized();
            foreach (var entry in writer.Queue.GetConsumingEnumerable())
            {
                try
                {
                    BeforeFileWrite?.Invoke(entry.Line);
                    writer.File.Append(entry.Line);
                    if (entry.FlushToDisk)
                        writer.File.Flush(toDisk: true);
                }
                catch { }
                lock (_progressLock)
                {
                    _written = Math.Max(_written, entry.Sequence);
                    Monitor.PulseAll(_progressLock);
                }
            }
        }
        catch { }
        finally
        {
            try
            {
                writer.File.Flush(toDisk: true);
                writer.File.Dispose();
            }
            catch { }
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        try { Dispose(); }
        catch { }
    }

    private readonly record struct FileEntry(long Sequence, string Line, bool FlushToDisk);

    private sealed class FileWriter
    {
        public FileWriter(RollingLogFile file, BlockingCollection<FileEntry> queue)
        {
            File = file;
            Queue = queue;
        }

        public RollingLogFile File { get; }
        public BlockingCollection<FileEntry> Queue { get; }
        public Thread? Thread { get; set; }

        /// <summary>Lets the writer finish the queue, waiting up to <paramref name="timeout"/>.</summary>
        public void Stop(TimeSpan timeout)
        {
            try
            {
                Queue.CompleteAdding();
                Thread?.Join(timeout);
            }
            catch { }
        }
    }

    /// <summary>
    /// Name of the first type on the stack outside tracing and logging infrastructure, or
    /// null if there is none.
    /// Compiler-generated types (async state machines, lambdas) resolve to the type that
    /// declares them. In Release builds the JIT may inline the true caller, in which case
    /// the nearest non-inlined caller is named.
    /// </summary>
    private static string? CallerName()
    {
        try
        {
            var frames = new StackTrace(1, fNeedFileInfo: false).GetFrames();
            foreach (var frame in frames)
            {
                Type? type = frame.GetMethod()?.DeclaringType;
                if (type == null || type == typeof(AppLogListener))
                    continue;
                string ns = type.Namespace ?? string.Empty;
                if (ns.StartsWith("System.Diagnostics", StringComparison.Ordinal))
                    continue;
                if (ns.StartsWith("Avalonia", StringComparison.Ordinal))
                    return "Avalonia";

                while (type.DeclaringType != null && type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                    type = type.DeclaringType;
                return type.Name;
            }
        }
        catch { }
        return null;
    }
}
