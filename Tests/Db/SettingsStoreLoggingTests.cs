using System.Collections.Concurrent;
using System.Diagnostics;
using PtzJoystickControl.Application.Db;
using Xunit;

namespace PtzJoystickControl.Tests.Db;

// Settings load failures must reach the release log (Trace), not only Debug output.
public sealed class SettingsStoreLoggingTests : IDisposable
{
    private readonly string _dir;
    private readonly RecordingTraceListener _trace = new();

    public SettingsStoreLoggingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PtzSettingsStoreLoggingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Trace.Listeners.Add(_trace);
    }

    public void Dispose()
    {
        Trace.Listeners.Remove(_trace);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string CorruptFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "{ not json");
        return path;
    }

    [Fact]
    public void GamepadStore_LoadingCorruptFile_LogsAnError()
    {
        var path = CorruptFile("Gamepads.json");

        Assert.Empty(new GamepadSettingsStore(path).GetAllGamepadSettings());

        Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Error && e.Message.Contains(path));
    }

    [Fact]
    public void CameraStore_LoadingCorruptFile_LogsAnError()
    {
        var path = CorruptFile("Cameras.json");

        Assert.Empty(new CameraSettingsStore(path).GetAllCameras());

        Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Error && e.Message.Contains(path));
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<(TraceEventType Type, string Message)> _events = new();

        public IEnumerable<(TraceEventType Type, string Message)> Events => _events;

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
            _events.Enqueue((eventType, message ?? string.Empty));

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
            _events.Enqueue((eventType, args == null ? format ?? string.Empty : string.Format(format ?? string.Empty, args)));

        public override void Write(string? message) { }

        public override void WriteLine(string? message) { }
    }
}
