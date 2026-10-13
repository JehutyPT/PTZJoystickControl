using System.Diagnostics;
using PtzJoystickControl.Application.Logging;
using Xunit;

namespace PtzJoystickControl.Tests.Logging;

// These tests drive the listener directly instead of through the global
// Trace.Listeners, which other test classes populate (including a throwing listener).
public sealed class AppLogListenerTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 13, 14, 3, 12, 345, TimeSpan.FromHours(1));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptzlog-" + Guid.NewGuid().ToString("N"));
    private readonly AppLogListener _listener;
    private readonly List<string> _published = new();

    public AppLogListenerTests()
    {
        Directory.CreateDirectory(_dir);
        _listener = new AppLogListener(() => Now, capacity: 1000);
        _listener.AttachFile(new RollingLogFile(_dir));
        _listener.Subscribe(_published.Add);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string[] FileLines() => ReadShared(Path.Combine(_dir, "PTZJoystickControl.log"));

    internal static string[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void WriteLine_IsOneInfoEntry_WithTimestampAndCallingType_InFileAndPublished()
    {
        _listener.WriteLine("Companion listener: failed to accept request");

        const string expected = "2026-10-13T14:03:12.345+01:00 INFO [AppLogListenerTests] Companion listener: failed to accept request";
        Assert.Equal(new[] { expected }, FileLines());
        Assert.Equal(new[] { expected }, _published);
    }

    [Theory]
    [InlineData(TraceEventType.Critical, "CRITICAL")]
    [InlineData(TraceEventType.Error, "ERROR")]
    [InlineData(TraceEventType.Warning, "WARN")]
    [InlineData(TraceEventType.Information, "INFO")]
    [InlineData(TraceEventType.Verbose, "VERBOSE")]
    public void TraceEvent_MapsEventTypeToLevel(TraceEventType type, string level)
    {
        _listener.TraceEvent(new TraceEventCache(), "testhost", type, 0, "something happened");

        Assert.Equal($"2026-10-13T14:03:12.345+01:00 {level} [AppLogListenerTests] something happened", Assert.Single(FileLines()));
    }

    [Fact]
    public void TraceEvent_WithFormat_FormatsTheMessage()
    {
        _listener.TraceEvent(new TraceEventCache(), "testhost", TraceEventType.Warning, 0, "camera {0} at {1}", 2, "10.0.0.2");

        Assert.EndsWith("WARN [AppLogListenerTests] camera 2 at 10.0.0.2", Assert.Single(FileLines()));
    }

    [Fact]
    public void MultiLineMessages_StayOnOneLine()
    {
        _listener.WriteLine("first\r\nsecond\nthird\rfourth");

        Assert.EndsWith(@"INFO [AppLogListenerTests] first\nsecond\nthird\nfourth", Assert.Single(FileLines()));
    }

    [Fact]
    public void WriteFragments_JoinTheNextWriteLine()
    {
        _listener.Write("part one, ");
        _listener.Write("part two, ");
        _listener.WriteLine("end");

        Assert.EndsWith("INFO [AppLogListenerTests] part one, part two, end", Assert.Single(FileLines()));
    }

    [Fact]
    public async Task Source_FromAnAsyncMethod_IsTheDeclaringType()
    {
        await Task.Yield();
        _listener.WriteLine("from async");

        Assert.EndsWith("INFO [AppLogListenerTests] from async", Assert.Single(FileLines()));
    }

    [Fact]
    public void Source_FromALambda_IsTheDeclaringType()
    {
        Action log = () => _listener.WriteLine("from lambda");
        log();

        Assert.EndsWith("INFO [AppLogListenerTests] from lambda", Assert.Single(FileLines()));
    }

    [Fact]
    public void Subscribe_ReturnsHistory_AndRecentEntriesAreBounded()
    {
        var listener = new AppLogListener(() => Now, capacity: 3);
        for (int i = 1; i <= 5; i++)
            listener.WriteLine($"entry {i}");

        var live = new List<string>();
        var history = listener.Subscribe(live.Add);
        listener.WriteLine("entry 6");

        Assert.Equal(new[] { "entry 3", "entry 4", "entry 5" }, history.Select(MessageOf));
        Assert.Equal(new[] { "entry 6" }, live.Select(MessageOf));

        listener.Unsubscribe(live.Add);
        listener.WriteLine("entry 7");
        Assert.Single(live);
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotStopLoggingOrOtherSubscribers()
    {
        var listener = new AppLogListener(() => Now, capacity: 10);
        var live = new List<string>();
        listener.Subscribe(_ => throw new InvalidOperationException("subscriber failed"));
        listener.Subscribe(live.Add);

        listener.WriteLine("still logged");

        Assert.Single(live);
    }

    [Fact]
    public void UnwritableFile_DoesNotThrow_AndStillPublishes()
    {
        var blockedDir = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(blockedDir, "a file where the log directory should be");
        var listener = new AppLogListener(() => Now, capacity: 10);
        listener.AttachFile(new RollingLogFile(blockedDir));
        var live = new List<string>();
        listener.Subscribe(live.Add);

        listener.WriteLine("cannot be written to disk");
        listener.TraceEvent(null, "x", TraceEventType.Error, 0, "neither can this");
        listener.Flush();
        listener.Dispose();

        Assert.Equal(2, live.Count);
    }

    [Fact]
    public void LogUnhandledException_IsCritical_WithTheFullException_OnDisk()
    {
        Exception thrown;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception e) { thrown = e; }

        _listener.LogUnhandledException(thrown, isTerminating: true);

        var line = Assert.Single(FileLines());
        Assert.StartsWith("2026-10-13T14:03:12.345+01:00 CRITICAL [AppDomain] Unhandled exception (terminating): System.InvalidOperationException: boom", line);
        Assert.Contains(@"\n   at ", line);
    }

    [Fact]
    public void LogUnhandledException_AcceptsNonExceptionObjects()
    {
        _listener.LogUnhandledException("not an exception", isTerminating: false);
        _listener.LogUnhandledException(null, isTerminating: false);

        Assert.Equal(new[]
        {
            "2026-10-13T14:03:12.345+01:00 CRITICAL [AppDomain] Unhandled exception: not an exception",
            "2026-10-13T14:03:12.345+01:00 CRITICAL [AppDomain] Unhandled exception: (null)",
        }, FileLines());
    }

    [Fact]
    public void LogUnobservedTaskException_IsAnError()
    {
        _listener.LogUnobservedTaskException(new AggregateException(new TimeoutException("late")));

        var line = Assert.Single(FileLines());
        Assert.StartsWith("2026-10-13T14:03:12.345+01:00 ERROR [TaskScheduler] Unobserved task exception: System.AggregateException:", line);
        Assert.Contains("TimeoutException: late", line);
    }

    private static string MessageOf(string line) => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..];
}
