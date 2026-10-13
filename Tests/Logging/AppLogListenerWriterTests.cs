using System.Diagnostics;
using PtzJoystickControl.Application.Logging;
using Xunit;

namespace PtzJoystickControl.Tests.Logging;

// The file is written by a background writer; these tests slow down or block it through
// the writer-thread hook to prove that logging callers never wait on disk I/O.
public sealed class AppLogListenerWriterTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 13, 14, 3, 12, 345, TimeSpan.FromHours(1));
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptzlog-" + Guid.NewGuid().ToString("N"));
    private readonly ManualResetEventSlim _gate = new(initialState: true);
    private readonly ManualResetEventSlim _writerReached = new();
    private readonly List<AppLogListener> _listeners = new();

    public AppLogListenerWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _gate.Set();
        foreach (var listener in _listeners)
            listener.Dispose();
        // A writer a test left blocked past its bounded wait may still hold the file open
        // for a moment after the gate opens; Windows refuses to delete an open file.
        for (var watch = Stopwatch.StartNew(); ; Thread.Sleep(20))
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && watch.Elapsed < TimeSpan.FromSeconds(2)) { }
        }
    }

    private AppLogListener NewListener(int queueCapacity = AppLogListener.DefaultQueueCapacity,
        TimeSpan? flushTimeout = null, Action<string>? beforeFileWrite = null, Func<DateTimeOffset>? clock = null)
    {
        var listener = new AppLogListener(clock ?? (() => Now), capacity: 1000)
        {
            QueueCapacity = queueCapacity,
            FlushTimeout = flushTimeout ?? TimeSpan.FromSeconds(2),
            BeforeFileWrite = beforeFileWrite ?? (_ =>
            {
                _writerReached.Set();
                _gate.Wait();
            }),
            ResolveCaller = () => "Test",
        };
        _listeners.Add(listener);
        listener.AttachFile(new RollingLogFile(_dir));
        return listener;
    }

    private string[] FileLines()
    {
        var path = Path.Combine(_dir, "PTZJoystickControl.log");
        return File.Exists(path) ? SharedFile.ReadLines(path) : Array.Empty<string>();
    }

    private static string MessageOf(string line) => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..];

    [Fact]
    public void WithTheWriterBlocked_LoggingAndFlushReturnAtOnce_AndEntriesAreWrittenOnceItRecovers()
    {
        _gate.Reset();
        var listener = NewListener();
        var published = new List<string>();
        listener.Subscribe(published.Add);

        var watch = Stopwatch.StartNew();
        for (int i = 1; i <= 50; i++)
        {
            listener.WriteLine($"entry {i}");
            listener.Flush(); // Trace.AutoFlush calls this after every entry.
        }
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"logging took {watch.Elapsed} with the writer blocked");
        Assert.Equal(50, published.Count);
        Assert.Empty(FileLines());

        _gate.Set();
        Assert.True(listener.WaitForWriter(Patience));
        Assert.Equal(Enumerable.Range(1, 50).Select(i => $"entry {i}"), FileLines().Select(MessageOf));
    }

    [Fact]
    public void FullQueue_DropsInsteadOfBlocking_AndReportsTheCountAheadOfTheNextEntryThatFits()
    {
        _gate.Reset();
        var listener = NewListener(queueCapacity: 4);
        var published = new List<string>();
        listener.Subscribe(published.Add);

        listener.WriteLine("entry 0");
        Assert.True(_writerReached.Wait(Patience)); // the writer holds entry 0, the queue is empty
        var watch = Stopwatch.StartNew();
        for (int i = 1; i <= 10; i++)
            listener.WriteLine($"entry {i}"); // 1-4 fit, 5-10 are dropped
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"logging took {watch.Elapsed} with the queue full");
        Assert.Equal(11, published.Count); // the Log window misses nothing

        _gate.Set();
        Assert.True(listener.WaitForWriter(Patience));
        listener.WriteLine("after");
        Assert.True(listener.WaitForWriter(Patience));

        var lines = FileLines();
        Assert.Equal(
            new[] { "entry 0", "entry 1", "entry 2", "entry 3", "entry 4",
                "Dropped 6 log entries because the log file writer fell behind", "after" },
            lines.Select(MessageOf));
        Assert.Equal("2026-10-13T14:03:12.345+01:00 WARN [AppLogListener] Dropped 6 log entries because the log file writer fell behind", lines[5]);

        listener.WriteLine("later");
        Assert.True(listener.WaitForWriter(Patience));
        Assert.Equal("later", MessageOf(FileLines()[^1]));
        Assert.Single(FileLines(), line => line.Contains("Dropped"));
    }

    [Fact]
    public void Entries_KeepTheirOrderAndTheTimestampTakenWhenLogged()
    {
        long ticks = 0;
        var listener = NewListener(
            clock: () => Now.AddMilliseconds(Interlocked.Increment(ref ticks)),
            beforeFileWrite: _ => Thread.Sleep(2));

        var writers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 25; i++)
                listener.WriteLine($"thread {t} entry {i}");
        })).ToArray();
        Task.WaitAll(writers);

        Assert.True(listener.WaitForWriter(Patience));
        var lines = FileLines();
        Assert.Equal(listener.Snapshot(), lines);
        Assert.Equal(100, lines.Length);
        Assert.Equal(lines.OrderBy(l => l, StringComparer.Ordinal), lines);
        // Logged at Now + 1 ms ... Now + 100 ms, however late each was written.
        Assert.StartsWith("2026-10-13T14:03:12.346+01:00 ", lines[0]);
        Assert.StartsWith("2026-10-13T14:03:12.445+01:00 ", lines[^1]);
    }

    [Fact]
    public void FileIsWrittenOnlyOnTheWriterThread()
    {
        var threads = new List<int>();
        var listener = NewListener(beforeFileWrite: _ => { lock (threads) threads.Add(Environment.CurrentManagedThreadId); });

        listener.WriteLine("one");
        listener.TraceEvent(null, "x", TraceEventType.Error, 0, "two");
        Assert.True(listener.WaitForWriter(Patience));

        Assert.Equal(2, threads.Count);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, threads);
        Assert.Single(threads.Distinct());
    }

    [Fact]
    public void UnhandledException_WaitsForTheCrashEntryToReachTheFile()
    {
        var listener = NewListener(beforeFileWrite: _ => Thread.Sleep(20));
        for (int i = 0; i < 10; i++)
            listener.WriteLine($"entry {i}");

        listener.OnUnhandledException(null, new UnhandledExceptionEventArgs(new InvalidOperationException("boom"), isTerminating: true));

        // No WaitForWriter: the handler itself waited.
        var lines = FileLines();
        Assert.Equal(11, lines.Length);
        Assert.Contains("CRITICAL [AppDomain] Unhandled exception (terminating: true): System.InvalidOperationException: boom", lines[^1]);
    }

    [Fact]
    public void UnhandledException_WithTheQueueFull_WaitsForRoomInsteadOfDroppingTheCrashEntry()
    {
        _gate.Reset();
        var listener = NewListener(queueCapacity: 2, flushTimeout: TimeSpan.FromSeconds(5));
        listener.WriteLine("entry 0");
        Assert.True(_writerReached.Wait(Patience));
        listener.WriteLine("entry 1");
        listener.WriteLine("entry 2");
        listener.WriteLine("entry 3 (dropped)");

        using var release = new Timer(_ => _gate.Set(), null, 200, Timeout.Infinite);
        listener.OnUnhandledException(null, new UnhandledExceptionEventArgs(new InvalidOperationException("boom"), isTerminating: true));

        var lines = FileLines();
        Assert.Equal(
            new[] { "entry 0", "entry 1", "entry 2" },
            lines.Take(3).Select(MessageOf));
        Assert.Contains("Dropped 1 log entries", lines[3]);
        Assert.Contains("CRITICAL [AppDomain]", lines[4]);
    }

    [Fact]
    public void UnhandledException_WithTheWriterBlocked_ReturnsAfterABoundedWait()
    {
        _gate.Reset();
        var listener = NewListener(queueCapacity: 1, flushTimeout: TimeSpan.FromMilliseconds(200));
        listener.WriteLine("entry 0");
        Assert.True(_writerReached.Wait(Patience));
        listener.WriteLine("entry 1"); // fills the queue

        var watch = Stopwatch.StartNew();
        listener.OnUnhandledException(null, new UnhandledExceptionEventArgs(new InvalidOperationException("boom"), isTerminating: true));
        watch.Stop();

        // One bounded wait for room in the queue and one for the writer.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"the crash handler took {watch.Elapsed}");
    }

    [Fact]
    public void Dispose_WritesTheQueuedEntriesBeforeReturning()
    {
        var listener = NewListener(beforeFileWrite: _ => Thread.Sleep(5));
        for (int i = 1; i <= 20; i++)
            listener.WriteLine($"entry {i}");

        listener.Dispose();

        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"entry {i}"), FileLines().Select(MessageOf));
    }

    [Fact]
    public void Dispose_WithTheWriterBlocked_ReturnsAfterABoundedWait_AndLaterLoggingIsHarmless()
    {
        _gate.Reset();
        var listener = NewListener(flushTimeout: TimeSpan.FromMilliseconds(200));
        var published = new List<string>();
        listener.Subscribe(published.Add);
        listener.WriteLine("entry 1");

        var watch = Stopwatch.StartNew();
        listener.Dispose();
        watch.Stop();
        listener.WriteLine("after dispose");
        listener.Flush();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Dispose took {watch.Elapsed}");
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public void WriterFailure_DoesNotReachCallers_AndLaterEntriesAreStillWritten()
    {
        var listener = NewListener(beforeFileWrite: line =>
        {
            if (line.EndsWith("bad", StringComparison.Ordinal))
                throw new IOException("disk failed");
        });
        var published = new List<string>();
        listener.Subscribe(published.Add);

        listener.WriteLine("bad");
        listener.WriteLine("good");
        Assert.True(listener.WaitForWriter(Patience));

        Assert.Equal(new[] { "good" }, FileLines().Select(MessageOf));
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public void WithoutAFile_NothingIsQueued_AndWaitForWriterReturnsAtOnce()
    {
        using var listener = new AppLogListener(() => Now, capacity: 10);
        listener.WriteLine("window only");

        Assert.True(listener.WaitForWriter(TimeSpan.Zero));
        Assert.Single(listener.Snapshot());
    }
}
