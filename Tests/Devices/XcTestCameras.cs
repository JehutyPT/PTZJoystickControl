using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Tests.Skaarhoj;

namespace PtzJoystickControl.Tests.Devices;

// XC cameras addressed to a FakeXcCamera, and their log lines, for the XC camera tests.
internal static class XcTestCameras
{
    internal static XcCamera CameraFor(FakeXcCamera fake, string? name = null, TimeSpan? requestTimeout = null, TimeSpan? probeInterval = null)
    {
        var camera = new XcCamera(name ?? UniqueName(), requestTimeout ?? XcCamera.DefaultRequestTimeout, probeInterval ?? XcCamera.DefaultProbeInterval);
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        return camera;
    }

    // Trace listeners are global and test classes run in parallel: log assertions filter on this name.
    internal static string UniqueName() => $"XC test {Guid.NewGuid():N}";

    internal static List<(TraceEventType Type, string Message)> LogFor(TraceRecorder trace, string name) =>
        trace.Events.Where(e => e.Message.Contains(name)).ToList();

    /// <summary>Waits for the camera's first log line containing <paramref name="text"/> and returns it.</summary>
    internal static async Task<(TraceEventType Type, string Message)> WaitForLogAsync(TraceRecorder trace, string name, string text)
    {
        var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (true)
        {
            var lines = LogFor(trace, name);
            foreach (var line in lines)
            {
                if (line.Message.Contains(text))
                    return line;
            }
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"No log line with '{text}'. Logged: {string.Join(" | ", lines.Select(l => $"{l.Type}: {l.Message}"))}");
            await Task.Delay(5);
        }
    }
}
