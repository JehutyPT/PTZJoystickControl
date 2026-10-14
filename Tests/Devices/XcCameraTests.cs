using System.Diagnostics;
using System.Globalization;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

public class XcCameraTests
{
    // Long enough that a held or unanswered request is still outstanding when a test asserts on it.
    private static readonly TimeSpan LongRequestTimeout = TimeSpan.FromSeconds(30);

    // How long to keep collecting before asserting that nothing more was sent.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task APanMove_SendsOneCombinedRequestCarryingBothAxes()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);

        camera.Pan(0.5, PanDir.Right);

        var request = await fake.WaitForControlAsync(1);
        Assert.Equal("c.1.pan.speed.dir=5005&c.1.pan=right&c.1.tilt.speed.dir=10&c.1.tilt=stop", request.Query);
    }

    // A moving axis sends round(10 + f · 9990): fraction 0 still moves at 10, 1 is 10000, out-of-range
    // and NaN fractions are clamped, and a half rounds up (0.75 is 7502.5, sent as 7503).
    [Theory]
    [InlineData(0.0, 10)]
    [InlineData(1e-9, 10)]
    [InlineData(0.0001, 11)]
    [InlineData(0.5, 5005)]
    [InlineData(0.75, 7503)]
    [InlineData(1.0, 10000)]
    [InlineData(1.5, 10000)]
    [InlineData(-0.5, 10)]
    [InlineData(double.NaN, 10)]
    public async Task APanMoveAtASpeedFraction_SendsTheMatchingXcSpeed(double fraction, int expectedSpeed)
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);

        camera.Pan(fraction, PanDir.Right);

        var request = await fake.WaitForControlAsync(1);
        Assert.Equal(expectedSpeed.ToString(CultureInfo.InvariantCulture), request.Parameter("c.1.pan.speed.dir"));
        Assert.Equal("right", request.Pan);
    }

    [Fact]
    public async Task EachChange_CarriesBothAxesCurrentDirectionAndSpeed()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);

        camera.Pan(1.0, PanDir.Left);
        var first = await fake.WaitForControlAsync(1);
        camera.Tilt(0.25, TiltDir.Up);
        var second = await fake.WaitForControlAsync(2);
        camera.Tilt(0.0, TiltDir.Down);
        var third = await fake.WaitForControlAsync(3);

        Assert.Equal("c.1.pan.speed.dir=10000&c.1.pan=left&c.1.tilt.speed.dir=10&c.1.tilt=stop", first.Query);
        // 10 + 0.25 · 9990 = 2507.5, sent as 2508.
        Assert.Equal("c.1.pan.speed.dir=10000&c.1.pan=left&c.1.tilt.speed.dir=2508&c.1.tilt=up", second.Query);
        Assert.Equal("c.1.pan.speed.dir=10000&c.1.pan=left&c.1.tilt.speed.dir=10&c.1.tilt=down", third.Query);
    }

    [Fact]
    public async Task AZoomMove_SendsOnlyTheZoom_LeavingOutAPanThatDidNotChange()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Pan(0.5, PanDir.Right);
        await fake.WaitForControlAsync(1);

        camera.Zoom(0.5, ZoomDir.Tele);

        // 0 + floor(0.5 · 15 + 1e-6) = 7. The pan keeps moving but did not change, so the request leaves it out.
        var request = await fake.WaitForControlAsync(2);
        Assert.Equal("c.1.zoom.speed.dir=7&c.1.zoom=tele", request.Query);
    }

    // A moving focus sends floor(f · 2 + 1e-6) on the CR-N500's manual-focus speeds 0..2: fraction 0
    // still focuses at 0, a half is 1, 1 is 2, and out-of-range and NaN fractions are clamped. Far is
    // the command's high direction, near its low.
    [Theory]
    [InlineData(0.0, FocusDir.Far, "c.1.focus.speed=0&c.1.focus.action=far")]
    [InlineData(0.0, FocusDir.Near, "c.1.focus.speed=0&c.1.focus.action=near")]
    [InlineData(0.0001, FocusDir.Far, "c.1.focus.speed=0&c.1.focus.action=far")]
    [InlineData(0.0001, FocusDir.Near, "c.1.focus.speed=0&c.1.focus.action=near")]
    [InlineData(0.4999, FocusDir.Near, "c.1.focus.speed=0&c.1.focus.action=near")]
    [InlineData(0.5, FocusDir.Far, "c.1.focus.speed=1&c.1.focus.action=far")]
    [InlineData(0.5, FocusDir.Near, "c.1.focus.speed=1&c.1.focus.action=near")]
    [InlineData(1.0, FocusDir.Far, "c.1.focus.speed=2&c.1.focus.action=far")]
    [InlineData(1.0, FocusDir.Near, "c.1.focus.speed=2&c.1.focus.action=near")]
    [InlineData(1.5, FocusDir.Far, "c.1.focus.speed=2&c.1.focus.action=far")]
    [InlineData(-0.5, FocusDir.Near, "c.1.focus.speed=0&c.1.focus.action=near")]
    [InlineData(double.NaN, FocusDir.Far, "c.1.focus.speed=0&c.1.focus.action=far")]
    public async Task AFocusMoveAtASpeedFraction_SendsTheMatchingStepAndDirection(double fraction, FocusDir direction, string expectedQuery)
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);

        camera.Focus(fraction, direction);

        var request = await fake.WaitForControlAsync(1);
        Assert.Equal(expectedQuery, request.Query);
    }

    [Fact]
    public async Task PanAndZoomChangedTogether_GoOutInOneRequest_PanAndTiltFirst()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Tilt(0.5, TiltDir.Up);
        await fake.WaitForControlAsync(1);

        // Zoom changes before pan while the first request is held.
        camera.Zoom(1.0, ZoomDir.Wide);
        camera.Pan(0.5, PanDir.Right);
        fake.Release();
        var next = await fake.WaitForControlAsync(2);
        await Task.Delay(SettleTime);

        Assert.Equal("c.1.pan.speed.dir=5005&c.1.pan=right&c.1.tilt.speed.dir=5005&c.1.tilt=up&c.1.zoom.speed.dir=15&c.1.zoom=wide", next.Query);
        Assert.Equal(2, fake.ControlRequests.Count);
    }

    // A moving zoom sends floor(f · 15 + 1e-6) on the CR-N500's 0..15: fraction 0 still zooms at 0
    // rather than stopping, a fraction on a step (2/15) gets that step, 1 is 15, and out-of-range and
    // NaN fractions are clamped. Tele is the command's high direction, wide its low.
    [Theory]
    [InlineData(0.0, ZoomDir.Tele, "c.1.zoom.speed.dir=0&c.1.zoom=tele")]
    [InlineData(0.0, ZoomDir.Wide, "c.1.zoom.speed.dir=0&c.1.zoom=wide")]
    [InlineData(0.0001, ZoomDir.Tele, "c.1.zoom.speed.dir=0&c.1.zoom=tele")]
    [InlineData(0.0001, ZoomDir.Wide, "c.1.zoom.speed.dir=0&c.1.zoom=wide")]
    [InlineData(2.0 / 15, ZoomDir.Wide, "c.1.zoom.speed.dir=2&c.1.zoom=wide")]
    [InlineData(0.5, ZoomDir.Tele, "c.1.zoom.speed.dir=7&c.1.zoom=tele")]
    [InlineData(0.5, ZoomDir.Wide, "c.1.zoom.speed.dir=7&c.1.zoom=wide")]
    [InlineData(1.0, ZoomDir.Tele, "c.1.zoom.speed.dir=15&c.1.zoom=tele")]
    [InlineData(1.0, ZoomDir.Wide, "c.1.zoom.speed.dir=15&c.1.zoom=wide")]
    [InlineData(1.5, ZoomDir.Tele, "c.1.zoom.speed.dir=15&c.1.zoom=tele")]
    [InlineData(-0.5, ZoomDir.Wide, "c.1.zoom.speed.dir=0&c.1.zoom=wide")]
    [InlineData(double.NaN, ZoomDir.Tele, "c.1.zoom.speed.dir=0&c.1.zoom=tele")]
    public async Task AZoomMoveAtASpeedFraction_SendsTheMatchingStepAndDirection(double fraction, ZoomDir direction, string expectedQuery)
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);

        camera.Zoom(fraction, direction);

        var request = await fake.WaitForControlAsync(1);
        Assert.Equal(expectedQuery, request.Query);
    }

    [Fact]
    public async Task MovementAndStopCalls_ReturnWhileTheCameraStillHoldsTheRequest()
    {
        using var fake = new FakeXcCamera { Responder = r => r.IsControl ? XcReply.Held : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.5, PanDir.Right);
        await fake.WaitForControlAsync(1);

        // On another thread, so a call that waited on the held request fails the test instead of hanging it.
        var calls = Task.Run(() =>
        {
            camera.Tilt(0.5, TiltDir.Up);
            camera.Pan(0.2, PanDir.Left);
            camera.Stop();
        });

        // The fake is still holding the first request: the calls returned without waiting for its answer.
        await calls.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fake.ControlRequests);
    }

    [Fact]
    public async Task ChangesWhileARequestIsInFlight_SendOnlyTheNewestStateNext()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.1, PanDir.Right);
        await fake.WaitForControlAsync(1);

        camera.Pan(0.2, PanDir.Right);
        camera.Pan(0.3, PanDir.Left);
        camera.Tilt(0.5, TiltDir.Down);
        await Task.Delay(SettleTime);
        var whileHeld = fake.ControlRequests.Count;
        fake.Release();
        var next = await fake.WaitForControlAsync(2);
        await Task.Delay(SettleTime);

        Assert.Equal(1, whileHeld);
        // 10 + 0.3 · 9990 = 3007.
        Assert.Equal("c.1.pan.speed.dir=3007&c.1.pan=left&c.1.tilt.speed.dir=5005&c.1.tilt=down", next.Query);
        Assert.Equal(2, fake.ControlRequests.Count);
        Assert.Equal(1, fake.MaxInFlight);
    }

    [Fact]
    public async Task ConsecutiveRequests_StartAtLeastTheMinimumGapApart()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);

        // Faster than the gap, like a stick moved continuously at the input-loop rate.
        for (var i = 1; i <= 20; i++)
        {
            camera.Pan(i / 20.0, PanDir.Right);
            await Task.Delay(5);
        }
        var requests = await fake.WaitUntilAsync(r => r.Any(x => x.Parameter("c.1.pan.speed.dir") == "10000"), "the last pan speed");

        Assert.True(requests.Count >= 3, FakeXcCamera.Describe(requests));
        for (var i = 1; i < requests.Count; i++)
        {
            var gap = Stopwatch.GetElapsedTime(requests[i - 1].ArrivedAt, requests[i].ArrivedAt);
            Assert.True(gap >= XcCamera.MinRequestGap, $"Requests {i - 1} and {i} arrived {gap.TotalMilliseconds:0.0} ms apart. {FakeXcCamera.Describe(requests)}");
        }
    }

    [Fact]
    public async Task RequestsToOneCamera_ReuseOneConnection()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);

        camera.Pan(0.5, PanDir.Right);
        await fake.WaitForControlAsync(1);
        camera.Tilt(0.5, TiltDir.Up);
        await fake.WaitForControlAsync(2);
        camera.Pan(0.7, PanDir.Left);
        await fake.WaitForControlAsync(3);

        Assert.Equal(1, fake.Accepted);
    }

    [Fact]
    public async Task ACameraThatNeverAnswers_DoesNotDelayRequestsToAnotherCamera()
    {
        // Answers probes: a probe sent before the pan would otherwise hold the pan back.
        using var silent = new FakeXcCamera { Responder = r => r.IsControl ? XcReply.Never : XcReply.Ok };
        using var answering = new FakeXcCamera();
        using var stuck = XcTestCameras.CameraFor(silent, requestTimeout: LongRequestTimeout);
        using var other = XcTestCameras.CameraFor(answering, requestTimeout: LongRequestTimeout);
        stuck.Pan(0.5, PanDir.Right);
        await silent.WaitForControlAsync(1);

        other.Pan(0.5, PanDir.Right);

        // Arrives while the stuck camera's request is still outstanding (its timeout is far longer than this wait).
        await answering.WaitForControlAsync(1, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARunOfNoControlRightAnswers_LogsOneWarning_AndTheRecoveryOneLine()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var refusing = 1;
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsControl && Volatile.Read(ref refusing) == 1 ? XcReply.With(200, "301") : XcReply.Ok,
        };
        using var camera = XcTestCameras.CameraFor(fake, name);

        for (var i = 1; i <= 3; i++)
        {
            camera.Pan(i / 10.0, PanDir.Right);
            await fake.WaitForControlAsync(i);
        }
        await XcTestCameras.WaitForLogAsync(trace, name, "no camera control right");
        Volatile.Write(ref refusing, 0);
        camera.Pan(0.5, PanDir.Right);
        await fake.WaitForControlAsync(4);
        camera.Pan(0.6, PanDir.Right);
        await fake.WaitForControlAsync(5);
        await XcTestCameras.WaitForLogAsync(trace, name, "succeed again");
        await Task.Delay(SettleTime);

        var lines = XcTestCameras.LogFor(trace, name);
        var warning = Assert.Single(lines, l => l.Type == TraceEventType.Warning);
        Assert.Contains("no camera control right: another client may hold control", warning.Message);
        var afterWarning = lines.SkipWhile(l => l != warning).Skip(1).ToList();
        var recovery = Assert.Single(afterWarning);
        Assert.Equal(TraceEventType.Information, recovery.Type);
        Assert.Contains("succeed again", recovery.Message);
    }

    // Success is HTTP 200 with livescope-status 0; anything else is a failure, logged with its reason.
    [Theory]
    [InlineData(200, "301", "no camera control right: another client may hold control")]
    [InlineData(200, "403", "livescope-status 403")]
    [InlineData(200, null, "no livescope-status")]
    [InlineData(401, null, "HTTP 401")]
    [InlineData(500, "0", "HTTP 500")]
    public async Task AnAnswerOtherThanSuccess_IsAFailureLoggedWithItsReason(int status, string? livescope, string reason)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.IsControl ? XcReply.With(status, livescope) : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name);

        camera.Pan(0.5, PanDir.Right);

        var warning = await XcTestCameras.WaitForLogAsync(trace, name, "failed");
        Assert.Equal(TraceEventType.Warning, warning.Type);
        Assert.Contains(reason, warning.Message);
    }

    [Fact]
    public async Task ARequestWithNoAnswerWithinTheTimeout_IsAFailure()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.IsControl ? XcReply.Never : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name, requestTimeout: TimeSpan.FromMilliseconds(200));

        camera.Pan(0.5, PanDir.Right);

        var warning = await XcTestCameras.WaitForLogAsync(trace, name, "failed");
        Assert.Equal(TraceEventType.Warning, warning.Type);
        Assert.Contains("no answer within 200 ms", warning.Message);
    }

    [Fact]
    public async Task AFailureWhoseReasonChanges_IsLoggedAgain()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var answer = XcReply.With(200, "301");
        using var fake = new FakeXcCamera { Responder = r => r.IsControl ? Volatile.Read(ref answer) : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name);

        camera.Pan(0.2, PanDir.Right);
        await XcTestCameras.WaitForLogAsync(trace, name, "no camera control right");
        Volatile.Write(ref answer, XcReply.With(401, null));
        camera.Pan(0.4, PanDir.Right);
        await XcTestCameras.WaitForLogAsync(trace, name, "HTTP 401");
        camera.Pan(0.6, PanDir.Right);
        await fake.WaitForControlAsync(3);
        await Task.Delay(SettleTime);

        Assert.Equal(2, XcTestCameras.LogFor(trace, name).Count(l => l.Type == TraceEventType.Warning));
    }

    [Fact]
    public async Task FocusModeFocusLockPowerAndPresetSetOrReset_SendNothing_AndLogOncePerKind()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera();
        // The probe interval is long: the first probe is the only request the camera makes by itself.
        using var camera = XcTestCameras.CameraFor(fake, name, probeInterval: LongRequestTimeout);
        await fake.WaitForProbeAsync(1);

        var calls = Record.Exception(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                camera.FocusMode(FocusMode.Auto);
                camera.FocusLock(FocusLock.Lock);
                camera.Power(Power.On);
                camera.Preset(Preset.Set, 1);
                camera.Preset(Preset.Reset, 1);
            }
        });
        await Task.Delay(SettleTime);

        Assert.Null(calls);
        Assert.Single(fake.Requests);
        var unsupported = XcTestCameras.LogFor(trace, name).Where(l => l.Message.Contains("not supported")).ToList();
        Assert.Equal(5, unsupported.Count);
        Assert.All(unsupported, l => Assert.Equal(TraceEventType.Information, l.Type));
        Assert.Equal(5, unsupported.Select(l => l.Message).Distinct().Count());
    }
}
