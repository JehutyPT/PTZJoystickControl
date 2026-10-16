using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;
using static PtzJoystickControl.Tests.Devices.XcCameraFocusStepTests;

namespace PtzJoystickControl.Tests.Devices;

// Focus steps on an XC camera (#62) among the camera's other requests: what makes the next step read the
// focus position again, where focus requests go in the order (a recall, then stops, then turns with
// moves, then the probe), and what drops the steps not yet sent.
public class XcCameraFocusStepOrderTests
{
    // Each of these makes the next step read the focus position first, though it comes within the
    // pause: an address, port or login change, a failed request of any kind, a recall going out, and a
    // Manual focus move. (The pause and an answer of not available are in XcCameraFocusStepTests.) The
    // camera answers 1000 throughout, so a step from a known 1010 would write 1020.
    [Theory]
    [InlineData("login change")]
    [InlineData("failed pan move")]
    [InlineData("recall")]
    [InlineData("manual focus")]
    public async Task AfterIt_TheNextStepReadsThePositionAgain(string trigger)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera
        {
            // A pan move fails; every other request succeeds.
            Responder = r => r.IsControl && r.Pan == "right" ? XcReply.With(500, "0") : AnsweringFocusAt(1000)(r),
        };
        using var camera = await CameraAsync(fake, name);
        camera.FocusStep(0.005, FocusDir.Far);
        await FocusRequestsAsync(fake, 2);

        switch (trigger)
        {
            case "login change":
                camera.Username = "operator";
                break;
            case "failed pan move":
                camera.Pan(0.5, PanDir.Right);
                await XcTestCameras.WaitForLogAsync(trace, name, "failed: HTTP 500");
                break;
            case "recall":
                camera.Preset(Preset.Recall, 0);
                await fake.WaitUntilAsync(r => r.Any(x => x.Preset == "1"), "the recall");
                break;
            case "manual focus":
                camera.Focus(0.5, FocusDir.Far);
                await fake.WaitUntilAsync(r => r.Any(x => x.Focus == "far"), "the focus move");
                camera.Focus(0.0, FocusDir.Stop);
                await fake.WaitUntilAsync(r => r.Count(x => x.Focus == "stop") >= 3, "the focus stops");
                break;
        }
        camera.FocusStep(0.005, FocusDir.Far);

        var focus = await FocusRequestsAsync(fake, 4);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010), FocusRead, FocusWrite(1010) }, focus.Select(r => r.Target));
    }

    // A step in the moment the port changes, once the camera has the new port but before it reacts to
    // the change, reads at the new port: it never writes there from the position the old one reported
    // (which would be 1020). That read's answer, held until the camera has reacted, is ignored; then a
    // read and a write from what the new port reports.
    [Fact]
    public async Task AStepAsThePortChanges_ReadsAtTheNewPort_NeverWritesFromTheOldPosition()
    {
        using var first = new FakeXcCamera { Responder = AnsweringFocusAt(1000) };
        using var second = new FakeXcCamera { Responder = HoldingTheFirstFocusReadAt(1000) };
        using var camera = new EndpointChangeHookXcCamera(XcTestCameras.UniqueName());
        first.Serves(camera);
        camera.Port = first.Port;
        camera.Address = "127.0.0.1";
        await first.WaitForProbeAsync(1);
        camera.FocusStep(0.005, FocusDir.Far);
        await FocusRequestsAsync(first, 2);

        camera.BeforeEndpointChanged = () =>
        {
            camera.FocusStep(0.005, FocusDir.Far);
            // Waits off the test's context, so the wait never needs the thread it blocks.
            Task.Run(() => second.WaitUntilAsync(r => r.Any(x => x.IsFocus), "a focus request at the new port")).GetAwaiter().GetResult();
        };
        camera.Port = second.Port;
        second.Release();

        var focus = await FocusRequestsAsync(second, 3);
        Assert.Equal(new[] { FocusRead, FocusRead, FocusWrite(1010) }, focus.Select(r => r.Target));
    }

    // A recall wanted with focus steps goes first, and drops the steps not yet sent.
    [Fact]
    public async Task ARecall_GoesFirst_AndDropsTheStepsNotYetSent()
    {
        using var fake = new FakeXcCamera { Responder = HoldingTheFirstFocusReadAt(1000) };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusRead), "the focus read");
        camera.FocusStep(0.005, FocusDir.Far);
        camera.Preset(Preset.Recall, 0);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset == "1"), "the recall");
        await Task.Delay(SettleTime);

        Assert.Equal(new[] { FocusRead, "/-wvhttp-01-/control.cgi?p=1" }, fake.Requests.Where(r => !r.IsProbe).Select(r => r.Target));
    }

    // A pending stop goes before any focus request: all its sends, then the focus read and write.
    [Fact]
    public async Task APendingStop_GoesFirst_AndNoFocusRequestUntilItIsDone()
    {
        var held = XcTestCameras.HoldingTheFirstControlRequest();
        using var fake = new FakeXcCamera { Responder = r => r.IsFocusRead ? XcReply.Ok with { Body = FocusAt(1000) } : held(r) };
        using var camera = await CameraAsync(fake);

        camera.Pan(0.5, PanDir.Right);
        await fake.WaitForControlAsync(1);
        camera.Pan(0.0, PanDir.Stop);
        camera.FocusStep(0.005, FocusDir.Far);
        fake.Release();
        await FocusRequestsAsync(fake, 2);

        var sent = fake.Requests.Where(r => !r.IsProbe).Select(r => r.IsFocus ? r.Target : r.IsStop ? "stop" : "move");
        Assert.Equal(new[] { "move", "stop", "stop", "stop", FocusRead, FocusWrite(1010) }, sent);
    }

    // Steps held behind a stop that keeps failing are dropped by its failures, so they never go out
    // late, not even once the stop is given up.
    [Fact]
    public async Task StepsBehindAFailingStop_AreDropped_NeverSentLate()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.IsControl ? XcReply.With(500, "0") : AnsweringFocusAt(1000)(r) };
        using var camera = XcTestCameras.CameraFor(fake, name, requestTimeout: LongRequestTimeout, probeInterval: LongProbeInterval,
            stopGiveUpAfter: TimeSpan.FromMilliseconds(300), focusStepPause: LongPause);
        await fake.WaitForProbeAsync(1);

        camera.Pan(0.5, PanDir.Right);
        camera.Pan(0.0, PanDir.Stop);
        camera.FocusStep(0.005, FocusDir.Far);
        await XcTestCameras.WaitForLogAsync(trace, name, "gave up stopping");
        await Task.Delay(SettleTime);

        Assert.Empty(fake.FocusRequests);
    }

    // A request that ends in an unexpected error drops the steps not yet sent, as a failed request
    // does: none goes out after the loop's pause, and the next step reads the position first. Here the
    // whole info.cgi read ends that way: its answer names a charset the client does not know.
    [Fact]
    public async Task AnUnexpectedError_DropsTheStepsNotYetSent()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var held = XcTestCameras.HoldingTheFirst(r => r.IsFullInfo);
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsFullInfo
                ? held(r) with { ContentType = "text/plain; charset=not-a-charset", Body = "c.1.focus.value.min:=0\r\nc.1.focus.value.max:=2048\r\n" }
                : AnsweringFocusAt(1000)(r),
        };
        using var camera = await CameraAsync(fake, name);

        camera.FocusStep(0.005, FocusDir.Far);
        fake.Release();
        var error = await XcTestCameras.WaitForLogAsync(trace, name, "unexpected error");
        camera.FocusStep(0.005, FocusDir.Far);

        var focus = await FocusRequestsAsync(fake, 2);
        Assert.Equal(TraceEventType.Error, error.Type);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010) }, focus.Select(r => r.Target));
    }

    // After an unexpected error the next step reads the focus position first, as after a failed request,
    // though the position was known and the step comes within the pause. Every whole info.cgi read ends
    // that way here; with a short probe interval, one comes soon after the read and the write that made
    // the position known. The camera answers 1000 throughout, so a step from a known 1010 would write
    // 1020.
    [Fact]
    public async Task AfterAnUnexpectedError_TheNextStepReadsThePositionAgain()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsFullInfo
                ? XcReply.Ok with { ContentType = "text/plain; charset=not-a-charset", Body = "c.1.focus.value.min:=0\r\nc.1.focus.value.max:=2048\r\n" }
                : AnsweringFocusAt(1000)(r),
        };
        using var camera = XcTestCameras.CameraFor(fake, name, requestTimeout: LongRequestTimeout, probeInterval: TimeSpan.FromMilliseconds(100),
            focusStepPause: LongPause);
        await XcTestCameras.WaitForLogAsync(trace, name, "unexpected error");
        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusWrite), "the first write");
        // Every error before the write was logged before it went out: wait for the first after it.
        var errorsBefore = XcTestCameras.LogFor(trace, name).Count(l => l.Message.Contains("unexpected error"));
        await XcTestCameras.WaitForLogAsync(trace, name, "unexpected error", errorsBefore + 1);
        camera.FocusStep(0.005, FocusDir.Far);

        var focus = await FocusRequestsAsync(fake, 4);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010), FocusRead, FocusWrite(1010) }, focus.Select(r => r.Target));
    }

    // While a pan move keeps changing, focus requests and moves take turns: neither holds the other
    // back. Each request is held until the test releases it, and the pan changes while it is held.
    [Fact]
    public async Task FocusRequestsAndAChangingPanMove_TakeTurns()
    {
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsProbe ? XcReply.Ok : XcReply.Held with { Body = r.IsFocusRead ? FocusAt(1000) : "" },
        };
        using var camera = await CameraAsync(fake);

        camera.Pan(0.3, PanDir.Right);
        await WaitForSentAsync(fake, 1);
        camera.Pan(0.4, PanDir.Right);
        camera.FocusStep(0.005, FocusDir.Far);
        fake.Release();
        await WaitForSentAsync(fake, 2);
        camera.Pan(0.5, PanDir.Right);
        fake.Release();
        await WaitForSentAsync(fake, 3);
        camera.Pan(0.6, PanDir.Right);
        fake.Release();
        await WaitForSentAsync(fake, 4);
        fake.Release();
        await WaitForSentAsync(fake, 5);
        fake.Release();
        await Task.Delay(SettleTime);

        // round(10 + f · 9990): 0.3 is 3007, 0.5 is 5005 and 0.6 is 6004; 0.4 was never sent.
        var sent = fake.Requests.Where(r => !r.IsProbe).Select(r => r.IsFocus ? r.Target : $"pan {r.Parameter("c.1.pan.speed.dir")}");
        Assert.Equal(new[] { "pan 3007", FocusRead, "pan 5005", FocusWrite(1010), "pan 6004" }, sent);
    }

    // A probe waits while a focus request is wanted: after a login change, which wants a probe as soon
    // as the gap allows, the focus read and write go first.
    [Fact]
    public async Task AProbe_WaitsWhileAFocusRequestIsWanted()
    {
        var held = XcTestCameras.HoldingTheFirst(r => r.IsFocusWrite);
        using var fake = new FakeXcCamera { Responder = r => r.IsFocusRead ? XcReply.Ok with { Body = FocusAt(1000) } : held(r) };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusWrite), "the first write");
        camera.Username = "operator";
        camera.FocusStep(0.005, FocusDir.Far);
        fake.Release();
        await FocusRequestsAsync(fake, 4);

        // After the held write: the read the login change makes due, then the write, and no probe.
        var sent = fake.Requests.SkipWhile(r => !r.IsFocusWrite).Skip(1).ToList();
        Assert.Equal(new[] { FocusRead, FocusWrite(1010) }, sent.Select(r => r.Target));
    }

    // Stop() drops the steps not yet sent; the read in flight completes, and the stops go out.
    [Fact]
    public async Task Stop_DropsTheStepsNotYetSent()
    {
        using var fake = new FakeXcCamera { Responder = HoldingTheFirstFocusReadAt(1000) };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusRead), "the focus read");
        camera.FocusStep(0.005, FocusDir.Far);
        camera.Stop();
        fake.Release();
        var requests = await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 3, "the stops");
        await Task.Delay(SettleTime);

        Assert.Equal(new[] { FocusRead }, fake.FocusRequests.Select(r => r.Target));
        XcTestCameras.AssertRepeatedStops(requests, r => r.IsStop);
    }

    // A disposed camera drops the steps not yet sent, ignores new ones and sends no focus request; its
    // pending stops go out as before.
    [Fact]
    public async Task Dispose_DropsTheSteps_AndSendsOnlyThePendingStops()
    {
        using var fake = new FakeXcCamera
        {
            Responder = XcTestCameras.HoldingTheFirstControlRequest(),
        };
        using var camera = await CameraAsync(fake);

        camera.Pan(0.5, PanDir.Right);
        await fake.WaitForControlAsync(1);
        camera.Pan(0.0, PanDir.Stop);
        camera.FocusStep(0.005, FocusDir.Far);
        camera.Dispose();
        camera.FocusStep(0.005, FocusDir.Far);
        fake.Release();
        await XcTestCameras.WaitForReleaseAsync(camera);

        Assert.Empty(fake.FocusRequests);
        XcTestCameras.AssertRepeatedStops(fake.Requests, r => r.IsStop);
    }

    // A disposed camera with no stop pending is released at once: the read in flight is cancelled, and
    // no focus request follows.
    [Fact]
    public async Task Dispose_WithNoStopPending_SendsNoMoreFocusRequests()
    {
        using var fake = new FakeXcCamera { Responder = HoldingTheFirstFocusReadAt(1000) };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusRead), "the focus read");
        camera.FocusStep(0.005, FocusDir.Far);
        camera.Dispose();
        camera.FocusStep(0.005, FocusDir.Far);
        fake.Release();
        await Task.Delay(SettleTime);

        Assert.True(camera.Released);
        Assert.Equal(new[] { FocusRead }, fake.FocusRequests.Select(r => r.Target));
    }

    // A Manual focus move drops the steps not yet sent, and steps that arrive while it moves are
    // ignored.
    [Fact]
    public async Task AManualFocusMove_DropsTheSteps_AndStepsWhileItMovesAreIgnored()
    {
        using var fake = new FakeXcCamera { Responder = HoldingTheFirstFocusReadAt(1000) };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusRead), "the focus read");
        camera.FocusStep(0.005, FocusDir.Far);
        camera.Focus(0.5, FocusDir.Far);
        camera.FocusStep(0.005, FocusDir.Far);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Focus == "far"), "the focus move");
        camera.FocusStep(0.005, FocusDir.Near);
        camera.Focus(0.0, FocusDir.Stop);
        await fake.WaitUntilAsync(r => r.Count(x => x.Focus == "stop") >= 3, "the focus stops");
        await Task.Delay(SettleTime);

        Assert.Equal(new[] { FocusRead }, fake.FocusRequests.Select(r => r.Target));
    }

    // Waits until the camera sent this many requests other than probes.
    private static Task<IReadOnlyList<XcRequest>> WaitForSentAsync(FakeXcCamera fake, int count) =>
        fake.WaitUntilAsync(r => r.Count(x => !x.IsProbe) >= count, $"{count} request(s) other than probes");

    // An XC camera, with the long timings of CameraAsync, that runs a hook once, the next time its
    // address or port changes: after the base class has the new one, before the camera reacts to it.
    private sealed class EndpointChangeHookXcCamera : XcCamera
    {
        public EndpointChangeHookXcCamera(string name) : base(name, LongRequestTimeout, LongProbeInterval, DefaultStopGiveUpAfter, LongPause) { }

        public Action? BeforeEndpointChanged { get; set; }

        protected override void OnEndpointChanged()
        {
            var hook = BeforeEndpointChanged;
            BeforeEndpointChanged = null;
            hook?.Invoke();
            base.OnEndpointChanged();
        }
    }
}
