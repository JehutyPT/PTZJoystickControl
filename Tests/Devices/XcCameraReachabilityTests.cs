using System.ComponentModel;
using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// Reachability with scaled-down timings: a probe after ProbeInterval without a completed request,
// unreachable after a failure with no success within the interval.
public class XcCameraReachabilityTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ACameraAnsweringProbes_TurnsConnected_AndRaisesTheChange()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera();
        using var camera = new XcCamera(name, ShortTimeout, Interval);
        var connected = ConnectedBecomes(camera, true);

        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";

        await connected.WaitAsync(Wait);
        Assert.True(camera.Connected);
        Assert.Equal("item=s", fake.Probes[0].Query);
        await XcTestCameras.WaitForLogAsync(trace, name, "connected");
        Assert.Single(XcTestCameras.LogFor(trace, name));
    }

    [Fact]
    public async Task AnIdleCamera_IsProbedNoMoreOftenThanTheInterval()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: ShortTimeout, probeInterval: Interval);

        var probes = (await fake.WaitUntilAsync(r => r.Count(x => x.IsProbe) >= 3, "three probes")).Where(r => r.IsProbe).ToList();

        for (var i = 1; i < probes.Count; i++)
        {
            var gap = Stopwatch.GetElapsedTime(probes[i - 1].ArrivedAt, probes[i].ArrivedAt);
            Assert.True(gap >= Interval, $"Probes {i - 1} and {i} arrived {gap.TotalMilliseconds:0.0} ms apart. {FakeXcCamera.Describe(probes)}");
        }
    }

    [Fact]
    public async Task ACameraThatStopsAnswering_TurnsUnreachable_LoggedOnce()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var answering = 1;
        using var fake = new FakeXcCamera { Responder = _ => Volatile.Read(ref answering) == 1 ? XcReply.Ok : XcReply.Never };
        using var camera = new XcCamera(name, ShortTimeout, Interval);
        var connected = ConnectedBecomes(camera, true);
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        await connected.WaitAsync(Wait);
        var unreachable = ConnectedBecomes(camera, false);

        Volatile.Write(ref answering, 0);

        await unreachable.WaitAsync(Wait);
        Assert.False(camera.Connected);
        await XcTestCameras.WaitForLogAsync(trace, name, "not reachable");
        await Task.Delay(3 * (Interval + ShortTimeout));
        Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Message.Contains("not reachable"));
        Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
    }

    [Fact]
    public async Task NoProbeStarts_WhileAControlRequestIsInFlight()
    {
        using var fake = new FakeXcCamera { Responder = r => r.IsControl ? XcReply.Held : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout, probeInterval: Interval);
        camera.Pan(0.5, PanDir.Right);
        var control = await fake.WaitForControlAsync(1);

        await Task.Delay(4 * Interval);

        Assert.DoesNotContain(fake.Probes, p => p.ArrivedAt > control.ArrivedAt);
        Assert.Equal(1, fake.MaxInFlight);
    }

    [Fact]
    public async Task AStopWantedWhileAProbeIsHeld_GoesOutOnceTheProbeReturns()
    {
        using var fake = new FakeXcCamera { Responder = r => r.IsProbe ? XcReply.Held : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout, probeInterval: Interval);
        await fake.WaitForProbeAsync(1);

        camera.Stop();
        await Task.Delay(2 * Interval);
        var whileHeld = fake.ControlRequests.Count;
        fake.Release();

        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 3, "three stops");
        Assert.Equal(0, whileHeld);
    }

    [Fact]
    public async Task AStopWantedWhileAProbeGoesUnanswered_GoesOutOnceTheProbeTimesOut()
    {
        using var fake = new FakeXcCamera { Responder = r => r.IsProbe ? XcReply.Never : XcReply.Ok };
        // The probe interval is long: only the probe's timeout can free the stop.
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: TimeSpan.FromMilliseconds(300), probeInterval: LongTimeout);
        await fake.WaitForProbeAsync(1);

        camera.Stop();

        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 3, "three stops");
    }

    [Fact]
    public async Task PointingTheCameraAtAnotherPort_SendsTheNextRequestThere_AndRaisesThePersistentChange()
    {
        using var first = new FakeXcCamera();
        // Holds its probes, so the camera cannot reconnect before the asserts on the reset.
        using var second = new FakeXcCamera { Responder = r => r.IsProbe ? XcReply.Held : XcReply.Ok };
        // The probe interval is long: a prompt probe to the new port comes from the change itself.
        using var camera = new XcCamera(XcTestCameras.UniqueName(), ShortTimeout, LongTimeout);
        var connected = ConnectedBecomes(camera, true);
        camera.Port = first.Port;
        camera.Address = "127.0.0.1";
        await connected.WaitAsync(Wait);
        var persistent = new List<string?>();
        camera.PersistentPropertyChanged += (_, e) => persistent.Add(e.PropertyName);
        var reconnected = ConnectedBecomes(camera, true);

        camera.Port = second.Port;

        Assert.False(camera.Connected);
        Assert.Equal(new[] { nameof(XcCamera.Port) }, persistent);
        await second.WaitForProbeAsync(1);
        second.Release();
        await reconnected.WaitAsync(Wait);
        var firstCount = first.Requests.Count;
        camera.Pan(0.5, PanDir.Right);
        await second.WaitForControlAsync(1);
        Assert.Equal(firstCount, first.Requests.Count);
    }

    // Completes when the camera raises a Connected change to the given value.
    private static Task ConnectedBecomes(XcCamera camera, bool value)
    {
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (e.PropertyName == nameof(ICamera.Connected) && camera.Connected == value)
            {
                camera.PropertyChanged -= handler;
                raised.TrySetResult();
            }
        };
        camera.PropertyChanged += handler;
        return raised.Task;
    }
}
