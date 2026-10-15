using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// Disposing an XC camera, as removing it from the camera list does: Dispose returns at once, a camera
// with no stop pending is released at once, and from then on every command is ignored, while the
// pending stops still go out (docs/live-safety.md §1). XcCameraDisposeStopTests times those stops.
public class XcCameraDisposeTests
{
    // What Stop() sends: every axis stopped, each at its slowest speed.
    private const string StopAll =
        "c.1.pan.speed.dir=10&c.1.pan=stop&c.1.tilt.speed.dir=10&c.1.tilt=stop&c.1.zoom.speed.dir=0&c.1.zoom=stop&c.1.focus.speed=0&c.1.focus.action=stop";

    // Long enough that no request the fake answers at once can time out on a busy host, and that a
    // held request is still outstanding when the test releases it.
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(100);

    // Several probe intervals: a camera still running would probe in each.
    private static readonly TimeSpan Quiet = 4 * ProbeInterval;

    // Bounds a call that must return at once: only one blocked on the network takes this long.
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // Held until the test calls Release, then answered refusing control (301).
    private static readonly XcReply HeldThenRefused = XcReply.Held with { Livescope = "301" };

    [Fact]
    public async Task Dispose_ReturnsWhileTheRequestInFlightIsHeld()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);
        camera.Stop();

        await Task.Run(camera.Dispose).WaitAsync(Wait);

        // The pan is still held, and the stops wait behind it.
        Assert.Single(fake.ControlRequests);
        Assert.False(camera.Released);
        fake.Release();
        await XcTestCameras.WaitForReleaseAsync(camera);
        XcTestCameras.AssertRepeatedStops(fake.ControlRequests.Skip(1).ToList(), r => r.IsStop);
    }

    [Fact]
    public async Task DisposingAnIdleCamera_ReleasesItAtOnce_CancellingTheProbeInFlight()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirst(r => r.IsProbe) };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval);
        await fake.WaitForProbeAsync(1);

        camera.Dispose();

        Assert.True(camera.Released);
        // Answered too late: the camera no longer waits for it.
        fake.Release();
        await Task.Delay(Quiet);
        Assert.Single(fake.Requests);
        Assert.False(camera.Connected);
        Assert.Empty(XcTestCameras.LogFor(trace, name));
    }

    [Fact]
    public async Task ADisposedCamera_RecordsANewAddress_AndSendsNothing()
    {
        using var fake = new FakeXcCamera();
        using var camera = new XcCamera(XcTestCameras.UniqueName(), LongTimeout, ProbeInterval);
        camera.Dispose();

        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        camera.Pan(0.5, PanDir.Left);
        await Task.Delay(Quiet);

        Assert.Equal("127.0.0.1", camera.Address);
        Assert.Equal(fake.Port, camera.Port);
        Assert.Equal(0, fake.Accepted);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task CommandsAfterDispose_AreIgnored_AndThePendingStopsStillCarryStop()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);
        camera.Stop();
        camera.Dispose();

        camera.Pan(0.5, PanDir.Right);
        camera.Tilt(0.5, TiltDir.Up);
        camera.Zoom(0.5, ZoomDir.Tele);
        camera.Focus(0.5, FocusDir.Far);
        camera.PresetRecallSpeed(12);
        camera.Preset(Preset.Recall, 4);
        fake.Release();
        await XcTestCameras.WaitForReleaseAsync(camera);

        Assert.False(camera.IsMoving);
        var stops = fake.ControlRequests.Skip(1).ToList();
        Assert.All(stops, r => Assert.Equal(StopAll, r.Query));
        XcTestCameras.AssertRepeatedStops(stops, r => r.IsStop);
    }

    // Each of these is logged on a camera in use (an unsupported operation, a camera preset out of
    // range); a disposed camera logs nothing.
    [Fact]
    public void ADisposedCamera_LogsNothing_ForOperationsItWouldLog()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var camera = new XcCamera(name);
        camera.Dispose();

        camera.FocusMode(FocusMode.Auto);
        camera.FocusLock(FocusLock.Lock);
        camera.Power(Power.On);
        camera.Preset(Preset.Set, 0);
        camera.Preset(Preset.Recall, 200);

        Assert.Empty(XcTestCameras.LogFor(trace, name));
    }

    // A recall made while a pan is in flight goes out next and takes the place of the stops (#54); once
    // the camera is disposed it is dropped, and the stops go out instead.
    [Fact]
    public async Task ARecallNotYetSent_IsDropped_AndTheStopsGoOutInstead()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);
        camera.Preset(Preset.Recall, 4);
        camera.Stop();
        camera.Dispose();

        fake.Release();
        await XcTestCameras.WaitForReleaseAsync(camera);

        var requests = fake.ControlRequests;
        Assert.DoesNotContain(requests, r => r.Preset != null);
        XcTestCameras.AssertRepeatedStops(requests.Skip(1).ToList(), r => r.IsStop);
    }

    // The recall went out before the camera was disposed and took the place of the stops; it fails, so
    // it restores them, and they go out.
    [Fact]
    public async Task ARecallInFlightAtDispose_ThatFails_RestoresTheStops_WhichGoOut()
    {
        var heldPan = 0;
        using var fake = new FakeXcCamera
        {
            Responder = r => r.Preset != null ? HeldThenRefused
                : r.IsControl && Interlocked.Exchange(ref heldPan, 1) == 0 ? XcReply.Held : XcReply.Ok,
        };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);
        camera.Stop();
        camera.Preset(Preset.Recall, 4);
        fake.Release();
        var recall = await fake.WaitForControlAsync(2);
        camera.Dispose();

        fake.Release();
        await XcTestCameras.WaitForReleaseAsync(camera);

        Assert.Equal("5", recall.Preset);
        var requests = fake.ControlRequests;
        Assert.Single(requests, r => r.Preset != null);
        XcTestCameras.AssertRepeatedStops(requests.Skip(2).ToList(), r => r.IsStop);
    }
}
