using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Tests.Skaarhoj;

namespace PtzJoystickControl.Tests.Devices;

// XC cameras addressed to a FakeXcCamera, and their log lines, for the XC camera tests.
internal static class XcTestCameras
{
    internal static XcCamera CameraFor(FakeXcCamera fake, string? name = null, TimeSpan? requestTimeout = null, TimeSpan? probeInterval = null, string username = "", string password = "")
    {
        var camera = new XcCamera(name ?? UniqueName(), requestTimeout ?? XcCamera.DefaultRequestTimeout, probeInterval ?? XcCamera.DefaultProbeInterval);
        // The login and port first: setting the address starts the camera's requests.
        camera.Username = username;
        camera.Password = password;
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        return camera;
    }

    // A FakeXcCamera responder that holds the first control request until the test calls Release, and
    // answers every other request at once.
    internal static Func<XcRequest, XcReply> HoldingTheFirstControlRequest()
    {
        var held = 0;
        return r => r.IsControl && Interlocked.Exchange(ref held, 1) == 0 ? XcReply.Held : XcReply.Ok;
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
