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
/// message escaped as <c>\n</c>. Each line is appended to the attached
/// <see cref="RollingLogFile"/>, kept in a bounded in-memory history and published to
/// subscribers (the Log window). Never throws to the caller. Subscribers run on the
/// logging thread while a lock is held, so they must only hand the line off
/// (e.g. post it to the UI thread).
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

    /// <summary>Starts writing every following entry to <paramref name="file"/>.</summary>
    public void AttachFile(RollingLogFile file)
    {
        lock (_lock)
        {
            _file?.Dispose();
            _file = file;
        }
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

    /// <summary>For <see cref="AppDomain.UnhandledException"/>. Flushes to disk before returning.</summary>
    public void LogUnhandledException(object? exceptionObject, bool isTerminating)
    {
        Log(TraceEventType.Critical, "AppDomain",
            $"Unhandled exception{(isTerminating ? " (terminating)" : "")}: {exceptionObject?.ToString() ?? "(null)"}");
        lock (_lock)
            _file?.Flush(toDisk: true);
    }

    /// <summary>For <see cref="TaskScheduler.UnobservedTaskException"/>.</summary>
    public void LogUnobservedTaskException(Exception? exception)
    {
        Log(TraceEventType.Error, "TaskScheduler", $"Unobserved task exception: {exception?.ToString() ?? "(null)"}");
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

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id)
        => Log(eventType, null, string.Empty);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        => Log(eventType, null, message);

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
        Log(eventType, null, message);
    }

    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, object? data)
        => Log(eventType, null, data?.ToString());

    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, params object?[]? data)
        => Log(eventType, null, data == null ? null : string.Join(", ", data));

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

    /// <summary>
    /// Formats one entry. Public so the format has one definition; see docs/development.md.
    /// </summary>
    public static string FormatLine(DateTimeOffset timestamp, TraceEventType level, string source, string? message)
    {
        string text = (message ?? string.Empty)
            .Replace("\r\n", "\\n")
            .Replace('\r', '\n')
            .Replace("\n", "\\n");
        return $"{timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)} {LevelName(level)} [{source}] {text}";
    }

    public static string LevelName(TraceEventType level) => level switch
    {
        TraceEventType.Critical => "CRITICAL",
        TraceEventType.Error => "ERROR",
        TraceEventType.Warning => "WARN",
        TraceEventType.Verbose => "VERBOSE",
        _ => "INFO",
    };

    private void Log(TraceEventType level, string? source, string? message, bool joinPending = false)
    {
        try
        {
            // Resolve the caller outside the lock; it walks the stack.
            source ??= CallerName();
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
    /// Name of the first type on the stack outside tracing and logging infrastructure.
    /// Compiler-generated types (async state machines, lambdas) resolve to the type that
    /// declares them. In Release builds the JIT may inline the true caller, in which case
    /// the nearest non-inlined caller is named.
    /// </summary>
    private static string CallerName()
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
        return "App";
    }
}
