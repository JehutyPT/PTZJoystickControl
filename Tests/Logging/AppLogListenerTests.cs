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

    private string[] FileLines() => SharedFile.ReadLines(Path.Combine(_dir, "PTZJoystickControl.log"));

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
    public void OnUnhandledException_IsCritical_WithTheFullException_OnDisk()
    {
        Exception thrown;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception e) { thrown = e; }

        _listener.OnUnhandledException(null, new UnhandledExceptionEventArgs(thrown, isTerminating: true));

        var line = Assert.Single(FileLines());
        Assert.StartsWith("2026-10-13T14:03:12.345+01:00 CRITICAL [AppDomain] Unhandled exception (terminating: true): System.InvalidOperationException: boom", line);
        Assert.Contains(@"\n   at ", line);
    }

    [Fact]
    public void OnUnhandledException_StatesTheTerminatingFlag_AndAcceptsNonExceptionObjects()
    {
        _listener.OnUnhandledException(null, new UnhandledExceptionEventArgs("not an exception", isTerminating: false));
        _listener.OnUnhandledException(null, new UnhandledExceptionEventArgs(null!, isTerminating: false));

        Assert.Equal(new[]
        {
            "2026-10-13T14:03:12.345+01:00 CRITICAL [AppDomain] Unhandled exception (terminating: false): not an exception",
            "2026-10-13T14:03:12.345+01:00 CRITICAL [AppDomain] Unhandled exception (terminating: false): (null)",
        }, FileLines());
    }

    [Fact]
    public void OnUnobservedTaskException_IsAnError_AndMarksTheExceptionObserved()
    {
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new TimeoutException("late")));

        _listener.OnUnobservedTaskException(null, args);

        Assert.True(args.Observed);
        var line = Assert.Single(FileLines());
        Assert.StartsWith("2026-10-13T14:03:12.345+01:00 ERROR [TaskScheduler] Unobserved task exception: System.AggregateException:", line);
        Assert.Contains("TimeoutException: late", line);
    }

    [Fact]
    public void OnUnobservedTaskException_MarksObserved_EvenWhenLoggingFails()
    {
        var listener = new AppLogListener(() => throw new InvalidOperationException("clock failed"), capacity: 10);
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new TimeoutException("late")));

        listener.OnUnobservedTaskException(null, args);

        Assert.True(args.Observed);
    }

    [Fact]
    public void Source_WhenTheCallerIsUnknown_FallsBackToTheTraceSource()
    {
        var listener = new AppLogListener(() => Now, capacity: 10) { ResolveCaller = () => null };

        listener.TraceEvent(new TraceEventCache(), "Companion", TraceEventType.Warning, 0, "with message");
        listener.TraceEvent(new TraceEventCache(), "Companion", TraceEventType.Warning, 0, "camera {0}", 2);
        listener.TraceEvent(new TraceEventCache(), "Companion", TraceEventType.Warning, 0);
        listener.TraceData(new TraceEventCache(), "Companion", TraceEventType.Warning, 0, "data");
        listener.TraceData(new TraceEventCache(), "Companion", TraceEventType.Warning, 0, "a", "b");

        Assert.All(listener.Snapshot(), line => Assert.Contains(" WARN [Companion] ", line));
        Assert.Equal(5, listener.Snapshot().Count);
    }

    [Fact]
    public void Source_WhenTheCallerIsUnknown_FallsBackToTheCategory()
    {
        var listener = new AppLogListener(() => Now, capacity: 10) { ResolveCaller = () => null };

        listener.WriteLine("connected", "Camera1");

        Assert.Equal("2026-10-13T14:03:12.345+01:00 INFO [Camera1] connected", Assert.Single(listener.Snapshot()));
    }

    [Fact]
    public void Source_WithNoCallerSourceOrCategory_IsApp()
    {
        var listener = new AppLogListener(() => Now, capacity: 10) { ResolveCaller = () => null };

        listener.WriteLine("plain");
        listener.WriteLine("no category", null);
        listener.TraceEvent(null, "", TraceEventType.Error, 0, "no source");

        Assert.Equal(new[]
        {
            "2026-10-13T14:03:12.345+01:00 INFO [App] plain",
            "2026-10-13T14:03:12.345+01:00 INFO [App] no category",
            "2026-10-13T14:03:12.345+01:00 ERROR [App] no source",
        }, listener.Snapshot());
    }

    [Fact]
    public void Source_CallingTypeWinsOverCategory()
    {
        _listener.WriteLine("connected", "Camera1");

        Assert.Equal("2026-10-13T14:03:12.345+01:00 INFO [AppLogListenerTests] connected", Assert.Single(FileLines()));
    }

    [Fact]
    public void AttachFile_RollsAnOversizedFileFromAnEarlierRun_BeforeTheFirstEntry()
    {
        var dir = Path.Combine(_dir, "earlier-run");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "PTZJoystickControl.log"), new string('o', 100));
        using var listener = new AppLogListener(() => Now, capacity: 10);

        listener.AttachFile(new RollingLogFile(dir, maxBytes: 50, maxArchives: 1));

        Assert.Equal(100, new FileInfo(Path.Combine(dir, "PTZJoystickControl.1.log")).Length);
        Assert.False(File.Exists(Path.Combine(dir, "PTZJoystickControl.log")));
    }

    [Fact]
    public void Snapshot_ReturnsTheRecentEntries_AndClearRecentEmptiesThem()
    {
        var listener = new AppLogListener(() => Now, capacity: 2);
        listener.WriteLine("one");
        listener.WriteLine("two");
        listener.WriteLine("three");

        Assert.Equal(new[] { "two", "three" }, listener.Snapshot().Select(MessageOf));

        listener.ClearRecent();
        Assert.Empty(listener.Snapshot());
        listener.WriteLine("four");
        Assert.Equal(new[] { "four" }, listener.Snapshot().Select(MessageOf));
    }

    private static string MessageOf(string line) => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..];
}
