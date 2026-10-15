using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Services;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// A disposed XC camera with stops pending sends only those, under the usual rules, and is then
// released; one with none is released at once. After the release no request, probes included,
// follows, and the camera logs nothing more (docs/live-safety.md §1). The probe interval and give-up
// bound are scaled down, so they run on their own.
[Collection(TimingBoundCollection.Name)]
public class XcCameraDisposeStopTests
{
    private const string GaveUp = "gave up stopping";

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan GiveUpBound = TimeSpan.FromMilliseconds(400);

    // Long enough that no request the fake answers at once can time out on a busy host.
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    // For a request already on its way when the camera was released.
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(50);

    // Several probe intervals: a camera still running would probe in each.
    private static readonly TimeSpan Quiet = 4 * ProbeInterval;

    [Fact]
    public async Task DisposingAfterAStop_SendsThePendingStops_ThenNothingMore()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);

        camera.Stop();
        camera.Dispose();

        await XcTestCameras.WaitForReleaseAsync(camera);
        var (requests, log) = await NothingMoreAsync(fake, trace, name);
        var afterThePan = requests.SkipWhile(r => !r.IsControl).Skip(1).ToList();
        XcTestCameras.AssertRepeatedStops(afterThePan, r => r.IsStop);
        Assert.All(afterThePan, r => Assert.True(r.IsStop, FakeXcCamera.Describe(requests)));
        // Exactly 3: the stop and its 2 repeats, all answered at once, so none is retried.
        Assert.True(afterThePan.Count == 3, FakeXcCamera.Describe(requests));
        Assert.DoesNotContain(log, l => l.Type is TraceEventType.Warning or TraceEventType.Error);
    }

    // Once released, the camera sends nothing, so it cannot fail: the failures the fake answers with
    // from then on would show as a WARN and a "not reachable" INFO on a camera still probing.
    [Fact]
    public async Task DisposingAnIdleCamera_ReleasesItAtOnce_ThenNothingMore()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var failing = 0;
        using var fake = new FakeXcCamera { Responder = _ => Volatile.Read(ref failing) == 1 ? XcReply.With(500, "0") : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval);
        await XcTestCameras.WaitForLogAsync(trace, name, "is connected");

        camera.Dispose();
        Volatile.Write(ref failing, 1);

        Assert.True(camera.Released);
        await NothingMoreAsync(fake, trace, name);
    }

    // As for a camera in the list (XcCameraStopGiveUpTests), except that the camera is then released
    // rather than probed again.
    [Fact]
    public async Task ADisposedCamerasStopThatFailsThroughout_IsGivenUpWithOneError_ThenNothingMore()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = _ => XcReply.Refused };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: GiveUpBound);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);

        camera.Stop();
        camera.Dispose();

        await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);
        await XcTestCameras.WaitForReleaseAsync(camera);
        var (requests, log) = await NothingMoreAsync(fake, trace, name);
        var afterThePan = requests.SkipWhile(r => !r.IsControl).Skip(1).ToList();
        XcTestCameras.AssertRepeatedStops(afterThePan, r => r.IsStop);
        Assert.All(afterThePan, r => Assert.True(r.IsStop, FakeXcCamera.Describe(requests)));
        Assert.Single(log, l => l.Type == TraceEventType.Error);
        Assert.Contains(GaveUp, log.Last().Message);
    }

    // Removal stops the camera a controller is driving, through the selection, before it disposes it.
    [Fact]
    public async Task RemovingTheDrivenCamera_SendsItsStops_ThenNothingMore()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval);
        var service = new CamerasService(new MemoryCameraStore(camera));
        SelectingGamepad.Driving(service, camera);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);

        service.RemoveCamera(camera);

        await XcTestCameras.WaitForReleaseAsync(camera);
        var (requests, log) = await NothingMoreAsync(fake, trace, name);
        var afterThePan = requests.SkipWhile(r => !r.IsControl).Skip(1).ToList();
        XcTestCameras.AssertRepeatedStops(afterThePan, r => r.IsStop);
        Assert.All(afterThePan, r => Assert.True(r.IsStop, FakeXcCamera.Describe(requests)));
        // Exactly 3: the stop the removal sends and its 2 repeats, all answered at once.
        Assert.True(afterThePan.Count == 3, FakeXcCamera.Describe(requests));
        Assert.DoesNotContain(log, l => l.Type is TraceEventType.Warning or TraceEventType.Error);
    }

    // Waits out a request already on its way, then asserts that nothing more arrives or is logged over
    // several probe intervals. Returns the requests and the camera's log lines.
    private static async Task<(IReadOnlyList<XcRequest> Requests, List<(TraceEventType Type, string Message)> Log)> NothingMoreAsync(
        FakeXcCamera fake, TraceRecorder trace, string name)
    {
        await Task.Delay(Grace);
        var requests = fake.Requests;
        var log = XcTestCameras.LogFor(trace, name);

        await Task.Delay(Quiet);

        Assert.True(fake.Requests.Count == requests.Count, $"A request arrived after the release. {FakeXcCamera.Describe(fake.Requests)}");
        var logged = XcTestCameras.LogFor(trace, name);
        Assert.True(logged.Count == log.Count,
            $"Logged after the release: {string.Join(" | ", logged.Skip(log.Count).Select(l => $"{l.Type}: {l.Message}"))}");
        return (requests, log);
    }
}
