using System.Collections.Concurrent;
using System.Diagnostics;

namespace PtzJoystickControl.Tests.Skaarhoj;

// Records Trace output while attached. Trace listeners are global and xUnit runs test classes in
// parallel, so assertions filter on text this test produced (a path, a port) rather than counting all lines.
internal sealed class TraceRecorder : TraceListener
{
    private readonly ConcurrentQueue<(TraceEventType Type, string Message)> _events = new();

    public TraceRecorder() => Trace.Listeners.Add(this);

    public IEnumerable<(TraceEventType Type, string Message)> Events => _events;

    public IEnumerable<string> Messages => _events.Select(e => e.Message);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
        _events.Enqueue((eventType, message ?? string.Empty));

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
        _events.Enqueue((eventType, args == null ? format ?? string.Empty : string.Format(format ?? string.Empty, args)));

    public override void Write(string? message) => _events.Enqueue((TraceEventType.Information, message ?? string.Empty));

    public override void WriteLine(string? message) => _events.Enqueue((TraceEventType.Information, message ?? string.Empty));

    protected override void Dispose(bool disposing)
    {
        Trace.Listeners.Remove(this);
        base.Dispose(disposing);
    }
}
