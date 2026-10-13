using System.Collections.Concurrent;
using System.Diagnostics;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Core.Model;
using Xunit;

namespace PtzJoystickControl.Tests.Db;

// Companion.json is optional and hand-edited: anything wrong in it falls back to the defaults
// with a WARN, and loading never throws (docs/live-safety.md §2).
public sealed class CompanionSettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly RecordingTraceListener _trace = new();

    public CompanionSettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PtzCompanionSettingsStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "Companion.json");
        Trace.Listeners.Add(_trace);
    }

    public void Dispose()
    {
        Trace.Listeners.Remove(_trace);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private CompanionSettings Load(string? contents)
    {
        if (contents != null)
            File.WriteAllText(_path, contents);
        CompanionSettings? settings = null;
        var thrown = Record.Exception(() => settings = new CompanionSettingsStore(_path).Load());
        Assert.Null(thrown);
        return settings!;
    }

    private IEnumerable<string> Warnings =>
        _trace.Events.Where(e => e.Type == TraceEventType.Warning).Select(e => e.Message);

    private static void AssertDefaults(CompanionSettings settings)
    {
        Assert.Equal("localhost", settings.Host);
        Assert.Equal(8000, settings.Port);
        Assert.Equal(12345, settings.ListenerPort);
    }

    [Fact]
    public void Defaults_AreTheValuesUsedBeforeTheyWereConfigurable()
    {
        AssertDefaults(new CompanionSettings());
    }

    [Fact]
    public void Load_NoFile_ReturnsDefaultsWithoutWarning()
    {
        AssertDefaults(Load(null));
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Load_ValidFile_ReturnsItsValues()
    {
        var settings = Load("""{ "Host": "192.168.68.110", "Port": 8001, "ListenerPort": 12346 }""");

        Assert.Equal("192.168.68.110", settings.Host);
        Assert.Equal(8001, settings.Port);
        Assert.Equal(12346, settings.ListenerPort);
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Load_PropertyNamesAreCaseInsensitive()
    {
        var settings = Load("""{ "host": "companion.local", "port": 8001, "listenerport": 12346 }""");

        Assert.Equal("companion.local", settings.Host);
        Assert.Equal(8001, settings.Port);
        Assert.Equal(12346, settings.ListenerPort);
    }

    [Fact]
    public void Load_OmittedValues_KeepTheirDefaultsWithoutWarning()
    {
        var settings = Load("""{ "Port": 8001 }""");

        Assert.Equal("localhost", settings.Host);
        Assert.Equal(8001, settings.Port);
        Assert.Equal(12345, settings.ListenerPort);
        Assert.Empty(Warnings);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public void Load_UnusableFile_ReturnsDefaultsAndWarns(string contents)
    {
        AssertDefaults(Load(contents));

        Assert.Contains(Warnings, m => m.Contains(_path));
    }

    [Theory]
    [InlineData("""{ "Host": "192.168.68.110", "Port": 0, "ListenerPort": 12346 }""", "Port")]
    [InlineData("""{ "Host": "192.168.68.110", "Port": 65536, "ListenerPort": 12346 }""", "Port")]
    [InlineData("""{ "Host": "192.168.68.110", "Port": "8001", "ListenerPort": 12346 }""", "Port")]
    [InlineData("""{ "Host": "192.168.68.110", "Port": 8001.5, "ListenerPort": 12346 }""", "Port")]
    [InlineData("""{ "Host": "192.168.68.110", "Port": 8001, "ListenerPort": -1 }""", "ListenerPort")]
    [InlineData("""{ "Host": "192.168.68.110", "Port": 8001, "ListenerPort": null }""", "ListenerPort")]
    [InlineData("""{ "Host": "http://192.168.68.110", "Port": 8001, "ListenerPort": 12346 }""", "Host")]
    [InlineData("""{ "Host": "192.168.68.110:8000", "Port": 8001, "ListenerPort": 12346 }""", "Host")]
    [InlineData("""{ "Host": "", "Port": 8001, "ListenerPort": 12346 }""", "Host")]
    [InlineData("""{ "Host": "two words", "Port": 8001, "ListenerPort": 12346 }""", "Host")]
    [InlineData("""{ "Host": 42, "Port": 8001, "ListenerPort": 12346 }""", "Host")]
    public void Load_InvalidValue_FallsBackForThatValueOnlyAndWarns(string contents, string invalid)
    {
        var settings = Load(contents);

        Assert.Equal(invalid == "Host" ? "localhost" : "192.168.68.110", settings.Host);
        Assert.Equal(invalid == "Port" ? 8000 : 8001, settings.Port);
        Assert.Equal(invalid == "ListenerPort" ? 12345 : 12346, settings.ListenerPort);
        Assert.Contains(Warnings, m => m.Contains(_path) && m.Contains(invalid));
    }

    [Fact]
    public void OutboundBaseAddress_UsesHostAndPort()
    {
        Assert.Equal(new Uri("http://192.168.68.110:8001/"),
            new CompanionSettings { Host = "192.168.68.110", Port = 8001 }.OutboundBaseAddress);
        Assert.Equal(new Uri("http://[::1]:8000/"),
            new CompanionSettings { Host = "::1" }.OutboundBaseAddress);
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
