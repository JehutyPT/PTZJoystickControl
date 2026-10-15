using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// Preset recall on an XC camera: the app's 0-based preset number goes out as the camera preset
// p = n + 1, in a request of its own, at the recall speed last set.
public class XcCameraPresetTests
{
    private const int MinStopSends = 3;

    // Long enough that the first probe is the only request the camera makes by itself.
    private static readonly TimeSpan LongProbeInterval = TimeSpan.FromSeconds(30);

    // Long enough that a held request is still outstanding when the test releases it.
    private static readonly TimeSpan LongRequestTimeout = TimeSpan.FromSeconds(30);

    // How long to keep collecting before asserting that nothing more was sent.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(300);

    [Theory]
    [InlineData(0, "p=1")]
    [InlineData(99, "p=100")]
    public async Task RecallingAPreset_SendsOnlyTheCameraPresetNumber(int preset, string expectedQuery)
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);

        camera.Preset(Preset.Recall, (byte)preset);

        var request = await fake.WaitForControlAsync(1);
        Assert.Equal(expectedQuery, request.Query);
    }

    [Fact]
    public async Task RecallingAPresetBeyondTheCamerasPresets_SendsNothing_AndLogsAWarning()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake, name, probeInterval: LongProbeInterval);
        await fake.WaitForProbeAsync(1);

        // App preset 100 is camera preset 101; the CR-N500 has 100.
        var call = Record.Exception(() => camera.Preset(Preset.Recall, 100));
        await Task.Delay(SettleTime);

        Assert.Null(call);
        Assert.Empty(fake.ControlRequests);
        var warning = Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
        Assert.Contains("preset 101", warning.Message);
    }

    // A controller may recall on every input: each camera preset beyond the camera's presets is logged
    // at most once a minute, not on every recall.
    [Fact]
    public async Task RepeatedRecallsBeyondTheCamerasPresets_SendNothing_AndLogOneWarningPerCameraPreset()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake, name, probeInterval: LongProbeInterval);
        await fake.WaitForProbeAsync(1);

        // Recall - 101 three times, Recall - 102 twice and Recall - 128 once.
        var calls = Record.Exception(() =>
        {
            foreach (var preset in new byte[] { 100, 101, 100, 127, 101, 100 })
                camera.Preset(Preset.Recall, preset);
        });
        await Task.Delay(SettleTime);

        Assert.Null(calls);
        Assert.Empty(fake.ControlRequests);
        var warnings = XcTestCameras.LogFor(trace, name).Where(l => l.Type == TraceEventType.Warning).Select(l => l.Message);
        Assert.Equal(new[]
        {
            $"Camera {name}: preset 101 is outside the XC camera's presets 1..100; nothing sent",
            $"Camera {name}: preset 102 is outside the XC camera's presets 1..100; nothing sent",
            $"Camera {name}: preset 128 is outside the XC camera's presets 1..100; nothing sent",
        }, warnings);
    }

    // Each camera keeps its own minute for each camera preset: one camera's warning hides none on another.
    [Fact]
    public async Task TwoCamerasRecallingAPresetBeyondTheirPresets_EachLogTheirOwnWarning()
    {
        using var trace = new TraceRecorder();
        var firstName = XcTestCameras.UniqueName();
        var secondName = XcTestCameras.UniqueName();
        using var firstFake = new FakeXcCamera();
        using var secondFake = new FakeXcCamera();
        using var first = XcTestCameras.CameraFor(firstFake, firstName, probeInterval: LongProbeInterval);
        using var second = XcTestCameras.CameraFor(secondFake, secondName, probeInterval: LongProbeInterval);
        await firstFake.WaitForProbeAsync(1);
        await secondFake.WaitForProbeAsync(1);

        // Recall - 101 on each camera in turn, twice.
        var calls = Record.Exception(() =>
        {
            for (var i = 0; i < 2; i++)
            {
                first.Preset(Preset.Recall, 100);
                second.Preset(Preset.Recall, 100);
            }
        });
        await Task.Delay(SettleTime);

        Assert.Null(calls);
        Assert.Empty(firstFake.ControlRequests);
        Assert.Empty(secondFake.ControlRequests);
        var firstWarning = Assert.Single(XcTestCameras.LogFor(trace, firstName), l => l.Type == TraceEventType.Warning);
        Assert.Equal($"Camera {firstName}: preset 101 is outside the XC camera's presets 1..100; nothing sent", firstWarning.Message);
        var secondWarning = Assert.Single(XcTestCameras.LogFor(trace, secondName), l => l.Type == TraceEventType.Warning);
        Assert.Equal($"Camera {secondName}: preset 101 is outside the XC camera's presets 1..100; nothing sent", secondWarning.Message);
    }

    // Preset Recall Speed v (1..24) goes out with each later recall as round(1 + (v − 1) · 99 / 23)
    // on the CR-N500's p.ptzspeed 1..100: 12 is 48.3, sent as 48.
    [Theory]
    [InlineData(1, "p=3&p.ptzspeed=1")]
    [InlineData(12, "p=3&p.ptzspeed=48")]
    [InlineData(24, "p=3&p.ptzspeed=100")]
    public async Task ARecallAfterARecallSpeedWasSet_CarriesTheMatchingPtzSpeed(int recallSpeed, string expectedQuery)
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake, probeInterval: LongProbeInterval);
        await fake.WaitForProbeAsync(1);

        camera.PresetRecallSpeed((byte)recallSpeed);
        await Task.Delay(SettleTime);
        camera.Preset(Preset.Recall, 2);

        // Setting the recall speed sent nothing by itself.
        var request = await fake.WaitForControlAsync(1);
        Assert.Equal(expectedQuery, request.Query);
    }

    [Fact]
    public async Task ARecall_GoesOutAheadOfAPendingMove()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.2, PanDir.Left);
        await fake.WaitForControlAsync(1);

        // While the first request is held, the stick moves on and a preset is recalled.
        camera.Pan(0.6, PanDir.Left);
        camera.Preset(Preset.Recall, 1);
        fake.Release();
        await fake.WaitForControlAsync(3);
        await Task.Delay(SettleTime);

        var requests = fake.ControlRequests;
        Assert.Equal(3, requests.Count);
        Assert.Equal("p=2", requests[1].Query);
        // 10 + 0.6 · 9990 = 6004.
        Assert.Equal("c.1.pan.speed.dir=6004&c.1.pan=left&c.1.tilt.speed.dir=10&c.1.tilt=stop", requests[2].Query);
    }

    // The recall is called while the stop's second send is in flight: it goes out next, at least the
    // normal gap after that send completes, and takes the stop's place.
    [Fact]
    public async Task ARecallMadeWhileAStopIsRepeated_IsTheNextRequest_AndNoStopFollowsIt()
    {
        var stops = 0;
        using var fake = new FakeXcCamera { Responder = r => r.IsStop && Interlocked.Increment(ref stops) == 2 ? XcReply.Held : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);
        camera.Pan(0.5, PanDir.Stop);
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 2, "the stop's second send");

        camera.Preset(Preset.Recall, 0);
        var releasedAt = Stopwatch.GetTimestamp();
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset != null), "the recall");
        await Task.Delay(SettleTime);

        // The move, the stop's first two sends, then the recall, and nothing after it.
        var requests = fake.ControlRequests;
        var described = FakeXcCamera.Describe(requests);
        Assert.True(requests.Count == 4, described);
        Assert.Equal("p=1", requests[3].Query);
        var gap = Stopwatch.GetElapsedTime(releasedAt, requests[3].ArrivedAt);
        Assert.True(gap >= XcCamera.MinRequestGap, $"The recall arrived {gap.TotalMilliseconds:0.0} ms after the held stop was released. {described}");
    }

    // The camera refuses the pan stop, so it is retried; a recall called while one of its sends is in
    // flight is the next request, and once it succeeds the stop is not sent again.
    [Fact]
    public async Task ARecallMadeWhileAStopIsRetried_IsTheNextRequest_AndNoStopFollowsIt()
    {
        var stops = 0;
        using var fake = new FakeXcCamera
        {
            Responder = r => !r.IsStop ? XcReply.Ok
                : Interlocked.Increment(ref stops) == 5 ? XcReply.Held with { Livescope = "301" }
                : XcReply.Refused,
        };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);
        camera.Pan(0.5, PanDir.Stop);
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 5, "the stop's fifth send");

        camera.Preset(Preset.Recall, 0);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset != null), "the recall");
        await Task.Delay(SettleTime);

        // The move, five refused sends of the stop, then the recall, and nothing after it.
        var requests = fake.ControlRequests;
        Assert.True(requests.Count == 7, FakeXcCamera.Describe(requests));
        Assert.Equal("p=1", requests[6].Query);
    }

    // The zoom stop is entered after the recall, before either was sent: the recall still goes first,
    // takes the stop's place, and once it succeeds the stop is never sent.
    [Fact]
    public async Task AStopEnteredAfterARecallBeforeEitherWasSent_IsReplacedByTheRecall()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Zoom(0.5, ZoomDir.Tele);
        await fake.WaitForControlAsync(1);

        // While the zoom request is held, a preset is recalled and then the zoom is released.
        camera.Preset(Preset.Recall, 4);
        camera.Zoom(0.5, ZoomDir.Stop);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset != null), "the recall");
        await Task.Delay(SettleTime);

        var afterHeld = fake.ControlRequests.Skip(1).ToList();
        Assert.True(afterHeld.Count == 1, FakeXcCamera.Describe(afterHeld));
        Assert.Equal("p=5", afterHeld[0].Query);
    }

    // Stop() keeps a recall not yet sent, whichever of the two came first: the recall goes out and
    // takes the place of Stop()'s stops, and once it succeeds none of them is sent.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndARecallNotYetSent_SendOnlyTheRecall(bool stopFirst)
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);

        // While the pan request is held, a preset is recalled and everything is stopped.
        if (stopFirst)
            camera.Stop();
        camera.Preset(Preset.Recall, 4);
        if (!stopFirst)
            camera.Stop();
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset != null), "the recall");
        await Task.Delay(SettleTime);

        var afterHeld = fake.ControlRequests.Skip(1).ToList();
        Assert.True(afterHeld.Count == 1, FakeXcCamera.Describe(afterHeld));
        Assert.Equal("p=5", afterHeld[0].Query);
    }

    // A Stop() made while the recall is in flight is a new stop: it goes out after the recall, as any
    // stop does.
    [Fact]
    public async Task AStopMadeWhileARecallIsInFlight_GoesOutAfterIt()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Preset(Preset.Recall, 4);
        await fake.WaitForControlAsync(1);

        camera.Stop();
        fake.Release();
        await fake.WaitUntilAsync(r => AfterTheRecall(r).Count(x => x.IsStop) >= MinStopSends, "three stops after the recall");

        Assert.Equal("p=5", fake.ControlRequests[0].Query);
        XcTestCameras.AssertRepeatedStops(AfterTheRecall(fake.Requests), r => r.IsStop);
    }

    // While a pan-and-tilt move is in flight, pan is released and a preset is recalled: once the
    // recall succeeds, the tilt the camera was already sent is not sent again, and a later tilt change
    // goes out as usual.
    [Fact]
    public async Task AMoveAlreadySentWhenARecallSucceeds_IsNotSentAgain()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirst(IsPanLeftTiltUp) };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.5, PanDir.Left);
        camera.Tilt(0.5, TiltDir.Up);
        await fake.WaitUntilAsync(r => r.Any(IsPanLeftTiltUp), "the pan-and-tilt move");

        camera.Pan(0.5, PanDir.Stop);
        camera.Preset(Preset.Recall, 4);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset != null), "the recall");
        await Task.Delay(SettleTime);

        var afterRecall = AfterTheHeldMove(fake);
        Assert.True(afterRecall.Count == 1, FakeXcCamera.Describe(afterRecall));
        Assert.Equal("p=5", afterRecall[0].Query);

        camera.Tilt(0.7, TiltDir.Up);
        await fake.WaitUntilAsync(r => AfterTheRecall(r).Count > 0, "the tilt change");
        await Task.Delay(SettleTime);

        var afterHeld = AfterTheHeldMove(fake);
        Assert.True(afterHeld.Count == 2, FakeXcCamera.Describe(afterHeld));
        // 10 + 0.7 · 9990 = 7003.
        Assert.Equal("c.1.pan.speed.dir=10&c.1.pan=stop&c.1.tilt.speed.dir=7003&c.1.tilt=up", afterHeld[1].Query);
    }

    // While a pan-and-tilt move is in flight, pan is released, tilt changes speed, and a preset is
    // recalled: the recall takes the pan stop's place, and once it succeeds the tilt change, which the
    // camera never got, goes out once, as a move.
    [Fact]
    public async Task AMoveNotYetSentWhenARecallSucceeds_GoesOutOnceAfterIt()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirst(IsPanLeftTiltUp) };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.5, PanDir.Left);
        camera.Tilt(0.5, TiltDir.Up);
        await fake.WaitUntilAsync(r => r.Any(IsPanLeftTiltUp), "the pan-and-tilt move");

        camera.Pan(0.5, PanDir.Stop);
        camera.Tilt(0.7, TiltDir.Up);
        camera.Preset(Preset.Recall, 4);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset != null), "the recall");
        await Task.Delay(SettleTime);

        var afterHeld = AfterTheHeldMove(fake);
        Assert.True(afterHeld.Count == 2, FakeXcCamera.Describe(afterHeld));
        Assert.Equal("p=5", afterHeld[0].Query);
        // 10 + 0.7 · 9990 = 7003.
        Assert.Equal("c.1.pan.speed.dir=10&c.1.pan=stop&c.1.tilt.speed.dir=7003&c.1.tilt=up", afterHeld[1].Query);
    }

    // The camera sends nothing until it has an address, so the recall is its first request and takes
    // the place of a pan stop never sent. The recall fails: refused, answered with HTTP 401 while no
    // login is set (#49), or not answered within the timeout. The stop is restored under the normal
    // stop rules, the recall is not sent again, and its failure is logged as any other.
    [Theory]
    [InlineData("refused", "no camera control right")]
    [InlineData("challenged", "requires a login")]
    [InlineData("unanswered", "no answer within")]
    public async Task AStopTheRecallReplaced_IsRestored_WhenTheRecallFails(string answer, string logged)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var recallReply = answer switch
        {
            "refused" => XcReply.Refused,
            "challenged" => XcReply.Unauthorized(),
            _ => XcReply.Never,
        };
        using var fake = new FakeXcCamera { Responder = r => r.Preset != null ? recallReply : XcReply.Ok };
        // Only the unanswered recall times out, after the 500 ms production timeout. The other cases take a long one,
        // so that no request the fake answers at once can time out on a busy host.
        var requestTimeout = answer == "unanswered" ? TimeSpan.FromMilliseconds(500) : LongRequestTimeout;
        using var camera = new XcCamera(name, requestTimeout, LongProbeInterval) { Port = fake.Port };
        camera.Pan(0.5, PanDir.Left);
        camera.Pan(0.5, PanDir.Stop);
        camera.Preset(Preset.Recall, 4);

        camera.Address = "127.0.0.1";
        await fake.WaitUntilAsync(r => AfterTheRecall(r).Count(x => x.IsStop) >= MinStopSends, "three stops after the recall");
        await Task.Delay(SettleTime);

        Assert.Equal("p=5", fake.ControlRequests[0].Query);
        Assert.Single(fake.ControlRequests, r => r.Preset != null);
        XcTestCameras.AssertRepeatedStops(AfterTheRecall(fake.Requests), r => r.IsStop);
        var warning = Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
        Assert.Contains(logged, warning.Message);
    }

    // The camera refuses the pan stop while it is retried, and refuses the recall too: after the
    // recall, the stop is retried until the camera answers it, and then nothing more is sent.
    [Fact]
    public async Task AStopRetriedWhenARecallFails_IsRetriedAfterIt_UntilAnswered()
    {
        var stops = 0;
        using var fake = new FakeXcCamera
        {
            Responder = r => r.Preset != null ? XcReply.Refused
                : !r.IsStop ? XcReply.Ok
                : Interlocked.Increment(ref stops) switch
                {
                    4 => XcReply.Held with { Livescope = "301" },
                    <= 8 => XcReply.Refused,
                    _ => XcReply.Ok,
                },
        };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);
        camera.Pan(0.5, PanDir.Stop);
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 4, "the stop's fourth send");

        camera.Preset(Preset.Recall, 4);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 9, "the stop's ninth send");
        await Task.Delay(SettleTime);

        // After the recall: four refused sends of the stop, then the one answered, then nothing.
        var afterRecall = AfterTheRecall(fake.Requests);
        Assert.True(afterRecall.Count == 5 && afterRecall.All(r => r.IsStop), FakeXcCamera.Describe(fake.ControlRequests));
        Assert.Single(fake.ControlRequests, r => r.Preset != null);
    }

    // The recall takes the place of a pan stop whose first send succeeded, and is refused: the stop is
    // restored as a new stop, so it goes out at least 3 more times.
    [Fact]
    public async Task AStopWithASuccessfulSend_IsRestoredInFull_WhenTheRecallFails()
    {
        var stops = 0;
        using var fake = new FakeXcCamera
        {
            Responder = r => r.Preset != null ? XcReply.Refused
                : r.IsStop && Interlocked.Increment(ref stops) == 1 ? XcReply.Held
                : XcReply.Ok,
        };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);
        camera.Pan(0.5, PanDir.Stop);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsStop), "the stop's first send");

        camera.Preset(Preset.Recall, 4);
        fake.Release();
        await fake.WaitUntilAsync(r => AfterTheRecall(r).Count(x => x.IsStop) >= MinStopSends, "three stops after the recall");
        await Task.Delay(SettleTime);

        Assert.Single(fake.ControlRequests, r => r.Preset != null);
        XcTestCameras.AssertRepeatedStops(AfterTheRecall(fake.Requests), r => r.IsStop);
    }

    // While the recall that took the zoom stop's place is in flight, the zoom moves again, and then
    // the recall is refused: the move took the stop's place, so it goes out once and no stop follows.
    [Fact]
    public async Task AZoomMoveMadeWhileTheRecallIsInFlight_TakesTheZoomStopsPlace_WhenTheRecallFails()
    {
        using var fake = new FakeXcCamera { Responder = HoldingTheMoveThenARefusedRecall() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Zoom(0.5, ZoomDir.Tele);
        await fake.WaitForControlAsync(1);
        camera.Zoom(0.5, ZoomDir.Stop);
        camera.Preset(Preset.Recall, 4);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset == "5"), "the recall");

        camera.Zoom(0.8, ZoomDir.Tele);
        fake.Release();
        await fake.WaitUntilAsync(r => AfterTheRecall(r).Count > 0, "a request after the recall");
        await Task.Delay(SettleTime);

        var afterRecall = AfterTheRecall(fake.Requests);
        Assert.True(afterRecall.Count == 1, FakeXcCamera.Describe(fake.ControlRequests));
        // 0 + floor(0.8 · 15) = 12.
        Assert.Equal("c.1.zoom.speed.dir=12&c.1.zoom=tele", afterRecall[0].Query);
    }

    // While the recall that took the pan stop's place is in flight, pan and tilt both move, and then
    // the recall is refused: the move took the stop's place, so it goes out once and no stop follows.
    [Fact]
    public async Task AMoveOnPanAndTiltMadeWhileTheRecallIsInFlight_TakesTheStopsPlace_WhenTheRecallFails()
    {
        using var fake = new FakeXcCamera { Responder = HoldingTheMoveThenARefusedRecall() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        await ReleasePanAndRecallWhileTheMoveIsHeldAsync(fake, camera);

        camera.Pan(0.6, PanDir.Right);
        camera.Tilt(0.6, TiltDir.Up);
        fake.Release();
        await fake.WaitUntilAsync(r => AfterTheRecall(r).Count > 0, "a request after the recall");
        await Task.Delay(SettleTime);

        var afterRecall = AfterTheRecall(fake.Requests);
        Assert.True(afterRecall.Count == 1, FakeXcCamera.Describe(fake.ControlRequests));
        Assert.Equal("c.1.pan.speed.dir=6004&c.1.pan=right&c.1.tilt.speed.dir=6004&c.1.tilt=up", afterRecall[0].Query);
    }

    // While the recall that took the pan stop's place is in flight, tilt alone moves, and then the
    // recall is refused: a move of one axis does not end a pan/tilt stop, so the stop is restored and
    // its sends carry the tilt move with pan at stop.
    [Fact]
    public async Task ATiltMoveMadeWhileTheRecallIsInFlight_IsCarriedByTheRestoredStop_WhenTheRecallFails()
    {
        using var fake = new FakeXcCamera { Responder = HoldingTheMoveThenARefusedRecall() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        await ReleasePanAndRecallWhileTheMoveIsHeldAsync(fake, camera);

        camera.Tilt(0.6, TiltDir.Up);
        fake.Release();
        await fake.WaitUntilAsync(r => AfterTheRecall(r).Count(IsPanStopTiltUp) >= MinStopSends, "three sends of the restored stop");

        XcTestCameras.AssertRepeatedStops(AfterTheRecall(fake.Requests), IsPanStopTiltUp);
    }

    // While the recall that took the pan stop's place is in flight, Stop() is called, and then the
    // recall is refused: Stop()'s stop is the group's only stop, so it goes out exactly 3 times, as any
    // stop the camera answers.
    [Fact]
    public async Task StopMadeWhileTheRecallIsInFlight_IsTheOnlyStopSent_WhenTheRecallFails()
    {
        using var fake = new FakeXcCamera { Responder = HoldingTheMoveThenARefusedRecall() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        await ReleasePanAndRecallWhileTheMoveIsHeldAsync(fake, camera);

        camera.Stop();
        fake.Release();
        await fake.WaitUntilAsync(r => AfterTheRecall(r).Count(x => x.IsStop) >= MinStopSends, "three stops after the recall");
        await Task.Delay(SettleTime);

        var afterRecall = AfterTheRecall(fake.Requests);
        Assert.True(afterRecall.Count == MinStopSends && afterRecall.All(r => r.IsStop), FakeXcCamera.Describe(fake.ControlRequests));
    }

    // While the recall that took the pan stop's place is in flight, a newer recall is called, and then
    // the first is refused: the newer recall is the next request, with no stop before it, and takes the
    // restored stop's place. If it succeeds no stop follows; if it is refused too, the stop does.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewerRecallMadeWhileTheRecallIsInFlight_GoesNext_AndTheStopWaitsForIt(bool newerAnswered)
    {
        var holding = HoldingTheMoveThenARefusedRecall();
        using var fake = new FakeXcCamera { Responder = r => r.Preset == "8" ? newerAnswered ? XcReply.Ok : XcReply.Refused : holding(r) };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        await ReleasePanAndRecallWhileTheMoveIsHeldAsync(fake, camera);

        camera.Preset(Preset.Recall, 7);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset == "8"), "the newer recall");
        if (!newerAnswered)
            await fake.WaitUntilAsync(r => ControlRequestsAfter(r, x => x.Preset == "8").Count(x => x.IsStop) >= MinStopSends, "three stops after the newer recall");
        await Task.Delay(SettleTime);

        var requests = fake.ControlRequests;
        var described = FakeXcCamera.Describe(requests);
        var afterRecall = AfterTheRecall(requests);
        Assert.True(afterRecall.Count > 0 && afterRecall[0].Preset == "8", described);
        Assert.Single(requests, r => r.Preset == "5");
        Assert.Single(requests, r => r.Preset == "8");
        var afterNewer = ControlRequestsAfter(requests, r => r.Preset == "8");
        if (newerAnswered)
            Assert.True(afterNewer.Count == 0, described);
        else
            XcTestCameras.AssertRepeatedStops(afterNewer, r => r.IsStop);
    }

    // A Preset Recall Speed change after the recall was called does not change the speed it carries.
    [Fact]
    public async Task ARecall_CarriesTheRecallSpeedSetWhenItWasCalled()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);

        camera.PresetRecallSpeed(1);
        camera.Preset(Preset.Recall, 2);
        camera.PresetRecallSpeed(24);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset != null), "the recall");

        Assert.Equal("p=3&p.ptzspeed=1", fake.ControlRequests.Single(r => r.Preset != null).Query);
    }

    [Fact]
    public async Task ANewerRecall_ReplacesOneNotYetSent()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);

        camera.Preset(Preset.Recall, 0);
        camera.Preset(Preset.Recall, 7);
        fake.Release();
        await fake.WaitForControlAsync(2);
        await Task.Delay(SettleTime);

        Assert.Equal(new[] { "8" }, fake.ControlRequests.Where(r => r.Preset != null).Select(r => r.Preset));
    }

    [Fact]
    public async Task AFailedRecall_IsLogged_AndNotRetried()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.Preset != null ? XcReply.With(200, "301") : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name, probeInterval: LongProbeInterval);
        await fake.WaitForProbeAsync(1);

        camera.Preset(Preset.Recall, 2);
        await XcTestCameras.WaitForLogAsync(trace, name, "no camera control right");
        await Task.Delay(SettleTime);

        Assert.Single(fake.ControlRequests);
        Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
    }

    [Fact]
    public async Task APresetSetThenARecallOfTheSamePreset_SendsOnlyTheRecall()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake, probeInterval: LongProbeInterval);
        await fake.WaitForProbeAsync(1);

        camera.Preset(Preset.Set, 4);
        camera.Preset(Preset.Recall, 4);
        await fake.WaitForControlAsync(1);
        await Task.Delay(SettleTime);

        var request = Assert.Single(fake.ControlRequests);
        Assert.Equal("p=5", request.Query);
    }

    // The pan-and-tilt move the tests hold, rather than the first control request: the loop may first
    // send the pan alone, before the tilt is wanted.
    private static bool IsPanLeftTiltUp(XcRequest request) => request.Pan == "left" && request.Tilt == "up";

    // The control requests after the held pan-and-tilt move.
    private static List<XcRequest> AfterTheHeldMove(FakeXcCamera fake) => ControlRequestsAfter(fake.ControlRequests, IsPanLeftTiltUp);

    private static bool IsPanStopTiltUp(XcRequest request) => request.Pan == "stop" && request.Tilt == "up";

    // Holds the first control request, a move, and then every recall, each until the test calls
    // Release; a recall is refused once released. Answers every other request at once.
    private static Func<XcRequest, XcReply> HoldingTheMoveThenARefusedRecall()
    {
        var holding = XcTestCameras.HoldingTheFirstControlRequest();
        return r => r.Preset != null ? XcReply.Held with { Livescope = "301" } : holding(r);
    }

    // Pans left and, while the fake holds that move, releases pan and recalls camera preset 5; then
    // releases the move and waits for the recall, which goes out next and takes the pan/tilt stop's
    // place.
    private static async Task ReleasePanAndRecallWhileTheMoveIsHeldAsync(FakeXcCamera fake, XcCamera camera)
    {
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);
        camera.Pan(0.5, PanDir.Stop);
        camera.Preset(Preset.Recall, 4);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset == "5"), "the recall");
    }

    // The control requests after the first one that matches.
    private static List<XcRequest> ControlRequestsAfter(IEnumerable<XcRequest> requests, Func<XcRequest, bool> first) =>
        requests.Where(r => r.IsControl).SkipWhile(r => !first(r)).Skip(1).ToList();

    // The control requests after the first recall.
    private static List<XcRequest> AfterTheRecall(IEnumerable<XcRequest> requests) => ControlRequestsAfter(requests, r => r.Preset != null);
}
