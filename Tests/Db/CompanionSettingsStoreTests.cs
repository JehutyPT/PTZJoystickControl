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

    // Filtered to this test's file: other test classes log through the same global Trace in parallel.
    private IEnumerable<string> Informations =>
        _trace.Events.Where(e => e.Type == TraceEventType.Information && e.Message.Contains(_path)).Select(e => e.Message);

    private IEnumerable<string> WarningsAboutThisFile => Warnings.Where(m => m.Contains(_path));

    private static void AssertDefaults(CompanionSettings settings)
    {
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
        var settings = Load("""{ "ListenerPort": 12346 }""");

        Assert.Equal(12346, settings.ListenerPort);
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Load_PropertyNamesAreCaseInsensitive()
    {
        var settings = Load("""{ "listenerport": 12346 }""");

        Assert.Equal(12346, settings.ListenerPort);
    }

    [Fact]
    public void Load_EmptyObject_KeepsTheDefaultsWithoutWarning()
    {
        AssertDefaults(Load("{ }"));
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Load_CommentsAndTrailingCommas_AreAccepted()
    {
        var settings = Load("""
            {
              // Companion's HTTP module calls this port
              "ListenerPort": 12346,
            }
            """);

        Assert.Equal(12346, settings.ListenerPort);
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Load_UnknownKey_IsIgnoredWithAWarning()
    {
        var settings = Load("""{ "ListnerPort": 12346 }""");

        AssertDefaults(settings);
        Assert.Contains(Warnings, m => m.Contains(_path) && m.Contains("ListnerPort"));
    }

    // Host and Port only configured the app's outbound calls to Companion, which were removed (#15).
    // Companion.json on the show PC may still have them: they are ignored with one INFO line, not a WARN.
    [Theory]
    [InlineData("""{ "Host": "192.168.68.110", "ListenerPort": 12346 }""", "Host")]
    [InlineData("""{ "Port": 8001, "ListenerPort": 12346 }""", "Port")]
    [InlineData("""{ "host": "192.168.68.110", "ListenerPort": 12346 }""", "host")]
    [InlineData("""{ "Host": 42, "ListenerPort": 12346 }""", "Host")]
    [InlineData("""{ "Port": 70000, "ListenerPort": 12346 }""", "Port")]
    public void Load_RetiredHostOrPort_IsIgnoredWithAnInfoNotAWarning(string contents, string key)
    {
        var settings = Load(contents);

        Assert.Equal(12346, settings.ListenerPort);
        Assert.Empty(WarningsAboutThisFile);
        var info = Assert.Single(Informations, m => m.Contains(key));
        Assert.Contains("no longer used", info);
    }

    [Fact]
    public void Load_RetiredHostAndPort_LogOneInfoLine()
    {
        var settings = Load("""{ "Host": "192.168.68.110", "Port": 8000, "ListenerPort": 12346 }""");

        Assert.Equal(12346, settings.ListenerPort);
        Assert.Empty(WarningsAboutThisFile);
        var info = Assert.Single(Informations);
        Assert.Contains("Host", info);
        Assert.Contains("Port", info);
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
    [InlineData("""{ "ListenerPort": 0 }""")]
    [InlineData("""{ "ListenerPort": 65536 }""")]
    [InlineData("""{ "ListenerPort": "12346" }""")]
    [InlineData("""{ "ListenerPort": 12346.5 }""")]
    [InlineData("""{ "ListenerPort": -1 }""")]
    [InlineData("""{ "ListenerPort": null }""")]
    public void Load_InvalidListenerPort_FallsBackToTheDefaultAndWarns(string contents)
    {
        var settings = Load(contents);

        AssertDefaults(settings);
        Assert.Contains(Warnings, m => m.Contains(_path) && m.Contains("ListenerPort"));
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
