using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// The XC counterpart of ViscaIpDeviceStopRepeatTests: stops beat pending moves, are sent at least
// three times at least StopRepeatInterval apart, and are retried until one succeeds.
public class XcCameraStopTests
{
    private const int MinStopSends = 3;

    // Long enough for the whole repeat window (3 sends ~50 ms apart) plus scheduling jitter on a busy CI host.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(600);

    // Long enough that a held request is still outstanding when the test releases it.
    private static readonly TimeSpan LongRequestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task APanTiltStop_IsSentAtLeastThreeTimes_StopRepeatIntervalApart()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Pan(0.4, PanDir.Left);
        camera.Tilt(0.5, TiltDir.Up);
        await fake.WaitUntilAsync(r => r.Any(x => x.Pan == "left" && x.Tilt == "up"), "the move");

        camera.Pan(0.4, PanDir.Stop);
        camera.Tilt(0.5, TiltDir.Stop);
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= MinStopSends, "three stops");
        await Task.Delay(SettleTime);

        AssertRepeatedStops(fake.ControlRequests, r => r.IsStop);
    }

    [Fact]
    public async Task AZoomStop_IsSentAtLeastThreeTimes_StopRepeatIntervalApart_AtSpeed0()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Zoom(0.5, ZoomDir.Tele);
        await fake.WaitUntilAsync(r => r.Any(x => x.Zoom == "tele"), "the zoom");

        camera.Zoom(0.5, ZoomDir.Stop);
        await fake.WaitUntilAsync(r => r.Count(x => x.Zoom == "stop") >= MinStopSends, "three zoom stops");
        await Task.Delay(SettleTime);

        AssertRepeatedStops(fake.ControlRequests, r => r.Zoom == "stop");
        Assert.All(fake.ControlRequests.Where(r => r.Zoom == "stop"),
            r => Assert.Equal("c.1.zoom.speed.dir=0&c.1.zoom=stop", r.Query));
    }

    [Fact]
    public async Task AStoppedAxis_SendsStopAtSpeed10_WhateverTheFraction()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);

        camera.Pan(0.7, PanDir.Stop);
        var stop = await fake.WaitForControlAsync(2);

        Assert.Equal("c.1.pan.speed.dir=10&c.1.pan=stop&c.1.tilt.speed.dir=10&c.1.tilt=stop", stop.Query);
    }

    [Fact]
    public async Task AStopBehindAPendingMove_GoesOutFirst()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Pan(0.4, PanDir.Left);
        await fake.WaitForControlAsync(1);

        // A move waits behind the held request, then the stick is released.
        camera.Pan(0.8, PanDir.Left);
        camera.Pan(0, PanDir.Stop);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= MinStopSends, "three stops");
        await Task.Delay(SettleTime);

        var afterHeld = fake.ControlRequests.Skip(1).ToList();
        Assert.True(afterHeld.All(r => r.IsStop), FakeXcCamera.Describe(fake.Requests));
    }

    [Fact]
    public async Task AZoomStopBehindAPendingPanMove_GoesOutInTheNextRequest()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Zoom(0.5, ZoomDir.Tele);
        await fake.WaitForControlAsync(1);

        // While the zoom request is held, the pan starts and the zoom is released.
        camera.Pan(0.5, PanDir.Left);
        camera.Zoom(0.5, ZoomDir.Stop);
        fake.Release();
        var next = await fake.WaitForControlAsync(2);
        await fake.WaitUntilAsync(r => r.Count(x => x.Zoom == "stop") >= MinStopSends, "three zoom stops");
        await Task.Delay(SettleTime);

        Assert.Equal("c.1.pan.speed.dir=5005&c.1.pan=left&c.1.tilt.speed.dir=10&c.1.tilt=stop&c.1.zoom.speed.dir=0&c.1.zoom=stop", next.Query);
        AssertRepeatedStops(fake.ControlRequests, r => r.Zoom == "stop");
    }

    [Fact]
    public async Task AStopAnsweredWithFailures_IsRetriedUntilOneSucceeds()
    {
        var refusedStops = 0;
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsStop && Interlocked.Increment(ref refusedStops) <= 4 ? XcReply.With(200, "301") : XcReply.Ok,
        };
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Pan(0.4, PanDir.Left);
        await fake.WaitForControlAsync(1);

        camera.Pan(0, PanDir.Stop);
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 5, "four refused stops and a fifth");
        await Task.Delay(SettleTime);

        // Four refused, then the fifth succeeded: nothing more after it.
        Assert.Equal(5, fake.ControlRequests.Count(r => r.IsStop));
        AssertRepeatedStops(fake.ControlRequests, r => r.IsStop);
    }

    [Fact]
    public async Task AFocusStopAnsweredWithFailures_IsRetriedUntilOneSucceeds_AtSpeed0()
    {
        var refusedStops = 0;
        using var fake = new FakeXcCamera
        {
            Responder = r => r.Focus == "stop" && Interlocked.Increment(ref refusedStops) <= 4 ? XcReply.With(200, "301") : XcReply.Ok,
        };
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Focus(0.5, FocusDir.Near);
        await fake.WaitForControlAsync(1);

        camera.Focus(0.5, FocusDir.Stop);
        await fake.WaitUntilAsync(r => r.Count(x => x.Focus == "stop") >= 5, "four refused focus stops and a fifth");
        await Task.Delay(SettleTime);

        // Four refused, then the fifth succeeded: nothing more after it.
        var stops = fake.ControlRequests.Where(r => r.Focus == "stop").ToList();
        Assert.Equal(5, stops.Count);
        Assert.All(stops, r => Assert.Equal("c.1.focus.speed=0&c.1.focus.action=stop", r.Query));
        AssertRepeatedStops(fake.ControlRequests, r => r.Focus == "stop");
    }

    [Fact]
    public async Task AFocusMove_SupersedesARefusedFocusStop()
    {
        using var fake = new FakeXcCamera { Responder = r => r.Focus == "stop" ? XcReply.With(200, "301") : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Focus(0.5, FocusDir.Near);
        await fake.WaitForControlAsync(1);
        camera.Focus(0.5, FocusDir.Stop);
        await fake.WaitUntilAsync(r => r.Count(x => x.Focus == "stop") >= MinStopSends, "three refused focus stops");

        camera.Focus(1.0, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.Focus == "far"), "the new focus move");
        await Task.Delay(SettleTime);

        // The move left no focus stop to deliver: nothing more after it.
        Assert.Equal("c.1.focus.speed=2&c.1.focus.action=far", fake.ControlRequests.Last().Query);
    }

    [Fact]
    public async Task AStopThatTimesOut_IsRetried()
    {
        var silentStops = 0;
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsStop && Interlocked.Increment(ref silentStops) <= MinStopSends ? XcReply.Never : XcReply.Ok,
        };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: TimeSpan.FromMilliseconds(150));
        camera.Pan(0.4, PanDir.Left);
        await fake.WaitForControlAsync(1);

        camera.Pan(0, PanDir.Stop);

        // Three unanswered stops, then one more that is answered.
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) > MinStopSends, "a stop after three timed out");
    }

    [Fact]
    public async Task RedeflectingWithinTheRepeatWindow_EndsWithTheMove()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Pan(0.4, PanDir.Left);
        await fake.WaitForControlAsync(1);

        camera.Pan(0.4, PanDir.Stop);
        await Task.Delay(10);
        camera.Pan(0.31, PanDir.Right);
        await Task.Delay(SettleTime);

        // 10 + 0.31 · 9990 = 3106.9, sent as 3107.
        Assert.Equal("c.1.pan.speed.dir=3107&c.1.pan=right&c.1.tilt.speed.dir=10&c.1.tilt=stop", fake.ControlRequests.Last().Query);
    }

    [Fact]
    public async Task PanStoppingWhileTiltMoves_RepeatsTheStopAndKeepsTilting()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Pan(0.4, PanDir.Left);
        camera.Tilt(0.5, TiltDir.Up);
        await fake.WaitUntilAsync(r => r.Any(x => x.Pan == "left" && x.Tilt == "up"), "the move");

        camera.Pan(0.4, PanDir.Stop);
        await fake.WaitUntilAsync(r => r.Count(x => x.Pan == "stop" && x.Tilt == "up") >= MinStopSends, "three pan stops");
        await Task.Delay(SettleTime);

        var requests = fake.ControlRequests;
        var sinceStop = requests.SkipWhile(r => r.Pan != "stop").ToList();
        AssertRepeatedStops(requests, r => r.Pan == "stop" && r.Tilt == "up");
        Assert.DoesNotContain(sinceStop, r => r.Tilt == "stop");
        Assert.Equal("c.1.pan.speed.dir=10&c.1.pan=stop&c.1.tilt.speed.dir=5005&c.1.tilt=up", requests.Last().Query);
    }

    [Fact]
    public async Task Stop_SendsRepeatedStops_EvenWhenAlreadyStopped()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);

        camera.Stop();
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= MinStopSends, "three stops");
        await Task.Delay(SettleTime);

        AssertRepeatedStops(fake.ControlRequests, r => r.IsStop);
    }

    [Fact]
    public async Task Stop_StopsAMovingCamera()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Pan(0.4, PanDir.Left);
        camera.Tilt(0.5, TiltDir.Down);
        await fake.WaitUntilAsync(r => r.Any(x => x.Pan == "left" && x.Tilt == "down"), "the move");

        camera.Stop();
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= MinStopSends, "three stops");
        await Task.Delay(SettleTime);

        AssertRepeatedStops(fake.ControlRequests, r => r.IsStop);
        Assert.True(fake.ControlRequests.Last().IsStop, FakeXcCamera.Describe(fake.Requests));
    }

    [Fact]
    public async Task Stop_StopsAZoomingAndFocusingCamera_OnAllFourAxes()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Zoom(0.5, ZoomDir.Tele);
        camera.Focus(0.5, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.Focus == "far"), "the focus move");

        camera.Stop();
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= MinStopSends, "three stops");
        await Task.Delay(SettleTime);

        var stops = fake.ControlRequests.Where(r => r.IsStop).ToList();
        Assert.All(stops, r => Assert.Equal(
            "c.1.pan.speed.dir=10&c.1.pan=stop&c.1.tilt.speed.dir=10&c.1.tilt=stop&c.1.zoom.speed.dir=0&c.1.zoom=stop&c.1.focus.speed=0&c.1.focus.action=stop",
            r.Query));
        AssertRepeatedStops(fake.ControlRequests, r => r.IsStop);
        Assert.True(fake.ControlRequests.Last().IsStop, FakeXcCamera.Describe(fake.Requests));
    }

    [Fact]
    public void IsMoving_FollowsTheWantedZoomAndFocus()
    {
        using var camera = new XcCamera(XcTestCameras.UniqueName());

        camera.Zoom(0.5, ZoomDir.Wide);
        Assert.True(camera.IsMoving);
        camera.Zoom(0.5, ZoomDir.Stop);
        Assert.False(camera.IsMoving);

        camera.Focus(0.5, FocusDir.Near);
        Assert.True(camera.IsMoving);
        camera.Focus(0.5, FocusDir.Stop);
        Assert.False(camera.IsMoving);

        // Fraction 0 is still a move.
        camera.Zoom(0, ZoomDir.Tele);
        camera.Focus(0, FocusDir.Far);
        Assert.True(camera.IsMoving);
        camera.Stop();
        Assert.False(camera.IsMoving);
    }

    [Fact]
    public void IsMoving_FollowsTheWantedPanAndTilt()
    {
        using var camera = new XcCamera(XcTestCameras.UniqueName());
        Assert.False(camera.IsMoving);

        camera.Pan(0.4, PanDir.Left);
        Assert.True(camera.IsMoving);
        camera.Pan(0.4, PanDir.Stop);
        Assert.False(camera.IsMoving);

        camera.Tilt(0.5, TiltDir.Down);
        Assert.True(camera.IsMoving);
        camera.Stop();
        Assert.False(camera.IsMoving);
    }

    [Fact]
    public async Task ACameraWithNoAddress_SendsNothing()
    {
        using var fake = new FakeXcCamera();
        using var camera = new XcCamera(XcTestCameras.UniqueName()) { Port = fake.Port };

        camera.Pan(0.4, PanDir.Left);
        camera.Tilt(0.5, TiltDir.Up);
        camera.Stop();
        await Task.Delay(SettleTime);

        Assert.Empty(fake.Requests);
        Assert.False(camera.Connected);
    }

    /// <summary>Asserts at least 3 requests match, consecutive matches at least StopRepeatInterval apart.</summary>
    private static void AssertRepeatedStops(IReadOnlyList<XcRequest> requests, Func<XcRequest, bool> isStop)
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
}
