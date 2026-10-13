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
/// trace source or category, then to <c>App</c>. Each line is appended to the attached
/// <see cref="RollingLogFile"/>, kept in a bounded in-memory history
/// (<see cref="Snapshot"/>) and published to subscribers (the Log window). Never throws to
/// the caller. Subscribers run on the logging thread while a lock is held, so they must
/// only hand off (e.g. post to the UI thread).
/// </summary>
public sealed class AppLogListener : TraceListener
{
    public const int DefaultCapacity = 1000;

    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _capacity;
    private readonly Queue<string> _recent;
    private readonly List<Action<string>> _subscribers = new();
    private readonly StringBuilder _pending = new();
    private RollingLogFile? _file;

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

    /// <summary>
    /// Starts writing every following entry to <paramref name="file"/>, first rolling it
    /// if an earlier run left it oversized.
    /// </summary>
    public void AttachFile(RollingLogFile file)
    {
        try
        {
            lock (_lock)
            {
                _file?.Dispose();
                _file = file;
                file.RollIfOversized();
            }
        }
        catch { }
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
    /// Handler for <see cref="AppDomain.UnhandledException"/>: logs CRITICAL and flushes to
    /// disk before returning. Never throws.
    /// </summary>
    public void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            Log(TraceEventType.Critical, "AppDomain",
                $"Unhandled exception (terminating: {(e.IsTerminating ? "true" : "false")}): {e.ExceptionObject?.ToString() ?? "(null)"}");
            lock (_lock)
                _file?.Flush(toDisk: true);
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

    public override void Flush()
    {
        try
        {
            lock (_lock)
                _file?.Flush();
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _file?.Dispose();
                _file = null;
            }
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
    private void Log(TraceEventType level, string? source, string? message, bool joinPending = false, string? fallbackSource = null)
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
                Emit(FormatLine(_clock(), level, source, message));
            }
        }
        catch
        {
            // Logging must never take down the caller (SDL loop, sockets, UI).
        }
    }

    private void Emit(string line)
    {
        _file?.Append(line);

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
