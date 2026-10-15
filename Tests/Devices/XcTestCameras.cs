using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// XC cameras addressed to a FakeXcCamera, their log lines, and the stops they send, for the XC camera
// tests.
internal static class XcTestCameras
{
    // Every stop goes out at least this many times (docs/live-safety.md §1).
    private const int MinStopSends = 3;

    // A camera addressed to the fake, which releases it when the fake is disposed (FakeXcCamera.Serves).
    internal static XcCamera CameraFor(FakeXcCamera fake, string? name = null, TimeSpan? requestTimeout = null, TimeSpan? probeInterval = null, string username = "", string password = "", TimeSpan? stopGiveUpAfter = null)
    {
        var camera = new XcCamera(name ?? UniqueName(), requestTimeout ?? XcCamera.DefaultRequestTimeout, probeInterval ?? XcCamera.DefaultProbeInterval,
            stopGiveUpAfter ?? XcCamera.DefaultStopGiveUpAfter);
        fake.Serves(camera);
        // The login and port first: setting the address starts the camera's requests.
        camera.Username = username;
        camera.Password = password;
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        return camera;
    }

    // A FakeXcCamera responder that holds the first control request until the test calls Release, and
    // answers every other request at once.
    internal static Func<XcRequest, XcReply> HoldingTheFirstControlRequest() => HoldingTheFirst(r => r.IsControl);

    // A FakeXcCamera responder that holds the first request that matches until the test calls Release,
    // and answers every other request at once.
    internal static Func<XcRequest, XcReply> HoldingTheFirst(Func<XcRequest, bool> matches)
    {
        var held = 0;
        return r => matches(r) && Interlocked.Exchange(ref held, 1) == 0 ? XcReply.Held : XcReply.Ok;
    }

    /// <summary>Asserts at least 3 requests match, consecutive matches at least StopRepeatInterval apart.</summary>
    internal static void AssertRepeatedStops(IReadOnlyList<XcRequest> requests, Func<XcRequest, bool> isStop)
    {
        var stops = requests.Where(isStop).ToList();
        Assert.True(stops.Count >= MinStopSends, FakeXcCamera.Describe(requests));
        for (var i = 1; i < stops.Count; i++)
        {
            var gap = Stopwatch.GetElapsedTime(stops[i - 1].ArrivedAt, stops[i].ArrivedAt);
            Assert.True(gap >= XcCamera.StopRepeatInterval,
                $"Stops {i - 1} and {i} arrived {gap.TotalMilliseconds:0.0} ms apart. {FakeXcCamera.Describe(requests)}");
        }
    }

    /// <summary>
    /// Waits until the disposed camera is released: it sends nothing more. Throws a
    /// <see cref="TimeoutException"/> after 5 s. FakeXcCamera.Dispose also blocks on it, so it never
    /// resumes on the caller's context.
    /// </summary>
    internal static async Task WaitForReleaseAsync(XcCamera camera)
    {
        var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (!camera.Released)
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"Camera {camera.Name} was not released.");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    // Trace listeners are global and test classes run in parallel: log assertions filter on this name.
    internal static string UniqueName() => $"XC test {Guid.NewGuid():N}";

    internal static List<(TraceEventType Type, string Message)> LogFor(TraceRecorder trace, string name) =>
        trace.Events.Where(e => e.Message.Contains(name)).ToList();

    /// <summary>Waits for the camera's <paramref name="count"/>th log line containing <paramref name="text"/> and returns it.</summary>
    internal static async Task<(TraceEventType Type, string Message)> WaitForLogAsync(TraceRecorder trace, string name, string text, int count = 1)
    {
        var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (true)
        {
            var lines = LogFor(trace, name);
            var found = 0;
            foreach (var line in lines)
            {
                if (line.Message.Contains(text) && ++found == count)
                    return line;
            }
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"No log line with '{text}'. Logged: {string.Join(" | ", lines.Select(l => $"{l.Type}: {l.Message}"))}");
            await Task.Delay(5);
        }
    }
}
