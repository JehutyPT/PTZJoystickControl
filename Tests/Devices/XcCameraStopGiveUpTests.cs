using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// An XC stop none of whose sends succeeds is given up once the bound has passed since it started and
// it was sent at least three times: no more of it is sent, one ERROR is logged, and the idle probe
// runs again (docs/live-safety.md §1). The bound, request timeout and probe interval are scaled down.
[Collection(TimingBoundCollection.Name)]
public class XcCameraStopGiveUpTests
{
    private const int MinStopSends = 3;
    private const string GaveUp = "gave up stopping";
    private const string User = "fake-xc-user";
    private const string Secret = "fake-xc-password";

    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(200);

    // Long enough that no request the fake answers at once can time out on a busy host.
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    // For requests the fake never answers.
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);

    // How far past the bound a held send is released, so it completes past the bound.
    private static readonly TimeSpan BoundMargin = TimeSpan.FromMilliseconds(50);

    // How long before the bound a held send is answered, for a success shortly before it.
    private static readonly TimeSpan ShortlyBefore = TimeSpan.FromMilliseconds(100);

    // How long to keep collecting before asserting that nothing more was sent or logged.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(300);

    // Held until the test calls Release, then answered refusing control (301).
    private static readonly XcReply HeldThenRefused = XcReply.Held with { Livescope = "301" };

    // Every failure counts alike: the camera refuses control (301), asks for a login the camera has
    // none of or refuses the one it has (HTTP 401), answers with another HTTP error, or does not
    // answer. The camera answers probes, except where it asks for a login.
    [Theory]
    [InlineData("refused", "no camera control right: another client may hold control")]
    [InlineData("no login", "the camera requires a login; set username and password")]
    [InlineData("wrong login", "the camera refused the username and password")]
    [InlineData("HTTP 500", "HTTP 500")]
    [InlineData("unanswered", "no answer within 100 ms")]
    public async Task AStopThatFailsThroughout_IsGivenUpOnceTheBoundHasPassed_WithOneError_AndProbedAgain(string failure, string reason)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var failing = Failing(failure);
        using var fake = failing.Fake;
        using var camera = XcTestCameras.CameraFor(fake, name, failing.RequestTimeout, ProbeInterval,
            username: failing.Username, password: failing.Password, stopGiveUpAfter: Bound);
        var stoppedAt = await PanAndStopAsync(fake, camera);
        var error = await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);
        var givenUpBy = Stopwatch.GetTimestamp();
        var stopsAtError = fake.ControlRequests.Count(r => r.IsStop);
        var requests = await fake.WaitUntilAsync(r => ProbeAfterTheLast(r, x => x.IsStop) != null, "a probe after the last stop");

        var described = FakeXcCamera.Describe(requests);
        var stops = requests.Where(r => r.IsStop).ToList();
        Assert.True(stops.Count >= MinStopSends * failing.RequestsPerSend, described);
        Assert.True(stops.Count == stopsAtError, $"A stop arrived after the ERROR. {described}");
        var sinceStop = Stopwatch.GetElapsedTime(stoppedAt, givenUpBy);
        Assert.True(sinceStop >= Bound, $"The stop was given up within {sinceStop.TotalMilliseconds:0.0} ms. {described}");
        var gap = Stopwatch.GetElapsedTime(stops[^1].ArrivedAt, ProbeAfterTheLast(requests, x => x.IsStop)!.ArrivedAt);
        Assert.True(gap >= ProbeInterval, $"The probe arrived {gap.TotalMilliseconds:0.0} ms after the last stop. {described}");
        Assert.Equal(TraceEventType.Error, error.Type);
        Assert.Contains("(pan/tilt)", error.Message);
        Assert.Contains($"(last failure: {reason})", error.Message);
        var log = XcTestCameras.LogFor(trace, name);
        Assert.Single(log, l => l.Type == TraceEventType.Error);
        // The failure's WARN, a login failure's included, is logged as well as the ERROR.
        Assert.Contains(log, l => l.Type == TraceEventType.Warning && l.Message.EndsWith($"failed: {reason}"));
    }

    // Nothing listens at the camera's port, so every request fails to connect. Nothing receives the
    // requests: the log alone shows one ERROR, giving the reason its failure WARN gave.
    [Fact]
    public async Task AStopToAPortWithNothingListening_IsGivenUp_WithOneErrorGivingTheWarningsReason()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var closed = new FakeXcCamera();
        closed.Dispose();
        using var camera = XcTestCameras.CameraFor(closed, name, LongTimeout, ProbeInterval, stopGiveUpAfter: Bound);
        camera.Pan(0.5, PanDir.Left);
        camera.Pan(0.5, PanDir.Stop);

        var error = await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);
        await Task.Delay(SettleTime);

        var log = XcTestCameras.LogFor(trace, name);
        var described = string.Join(" | ", log.Select(l => $"{l.Type}: {l.Message}"));
        var warning = log.Take(log.IndexOf(error)).Last(l => l.Type == TraceEventType.Warning).Message;
        var reason = warning[(warning.IndexOf("failed: ", StringComparison.Ordinal) + "failed: ".Length)..];
        Assert.Contains($"(last failure: {reason})", error.Message);
        Assert.True(log.Count(l => l.Type == TraceEventType.Error) == 1, described);
    }

    // The camera refuses control (301) but answers status requests: once the stop is given up, the
    // probe runs again and the camera, not reachable while the stop failed, turns connected again.
    [Fact]
    public async Task ACameraThatRefusesControlButAnswersProbes_TurnsConnectedAgain_AfterTheGiveUp()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.IsControl ? XcReply.Refused : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: Bound);
        await XcTestCameras.WaitForLogAsync(trace, name, "is connected");

        await PanAndStopAsync(fake, camera);
        await XcTestCameras.WaitForLogAsync(trace, name, "succeed again; it is connected");

        Assert.True(camera.Connected);
        var log = XcTestCameras.LogFor(trace, name);
        var error = log.FindIndex(l => l.Message.Contains(GaveUp));
        var connectedAgain = log.FindIndex(l => l.Message.Contains("succeed again; it is connected"));
        Assert.True(error >= 0 && error < connectedAgain, string.Join(" | ", log.Select(l => $"{l.Type}: {l.Message}")));
    }

    // The camera refuses the stop's first two sends (301) and then stops answering: the ERROR gives the
    // last reason.
    [Fact]
    public async Task AStopWhoseFailureReasonChanges_IsGivenUpWithTheLastReason()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var stops = 0;
        using var fake = new FakeXcCamera
        {
            Responder = r => !r.IsStop ? XcReply.Ok : Interlocked.Increment(ref stops) < MinStopSends ? XcReply.Refused : XcReply.Never,
        };
        using var camera = XcTestCameras.CameraFor(fake, name, ShortTimeout, ProbeInterval, stopGiveUpAfter: Bound);
        await PanAndStopAsync(fake, camera);
        var error = await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);

        Assert.Contains("(last failure: no answer within 100 ms)", error.Message);
    }

    // The bound passes before the stop's second send: it is still sent three times, the last one given
    // up once it fails.
    [Fact]
    public async Task AStopFailingPastABoundShorterThanThreeSends_IsStillSentThreeTimes()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.IsControl ? XcReply.Refused : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: TimeSpan.FromMilliseconds(1));
        await PanAndStopAsync(fake, camera);
        await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);
        var requests = await fake.WaitUntilAsync(r => ProbeAfterTheLast(r, x => x.IsStop) != null, "a probe after the last stop");

        Assert.True(requests.Count(r => r.IsStop) == MinStopSends, FakeXcCamera.Describe(requests));
        XcTestCameras.AssertRepeatedStops(requests, r => r.IsStop);
    }

    // Scaled as the 5 s default is: the bound exceeds the probe interval plus two request timeouts plus
    // the repeat interval. The move before the stop is still in flight when the stop starts, and the
    // camera never answers the stop, the slowest way to fail: it still shows not reachable first.
    [Fact]
    public async Task WithTheBoundScaledAsTheDefault_TheNotReachableLine_ComesBeforeTheError()
    {
        var timeout = TimeSpan.FromMilliseconds(100);
        var probeInterval = TimeSpan.FromMilliseconds(300);
        var bound = TimeSpan.FromMilliseconds(700);
        Assert.True(bound > probeInterval + 2 * timeout + XcCamera.StopRepeatInterval);
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsStop ? XcReply.Never : r.IsControl ? XcReply.After(TimeSpan.FromMilliseconds(60)) : XcReply.Ok,
        };
        using var camera = XcTestCameras.CameraFor(fake, name, timeout, probeInterval, stopGiveUpAfter: bound);
        await XcTestCameras.WaitForLogAsync(trace, name, "is connected");

        await PanAndStopAsync(fake, camera);
        await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);

        var log = XcTestCameras.LogFor(trace, name);
        var unreachable = log.FindIndex(l => l.Message.Contains("is not reachable"));
        var error = log.FindIndex(l => l.Message.Contains(GaveUp));
        Assert.True(unreachable >= 0 && unreachable < error, string.Join(" | ", log.Select(l => $"{l.Type}: {l.Message}")));
    }

    // Why the default bound is 5 s: a failing stop turns the camera not reachable before it is given up.
    [Fact]
    public void TheDefaultBound_ExceedsTheProbeIntervalPlusTwoRequestTimeoutsPlusTheRepeatInterval()
    {
        Assert.True(XcCamera.DefaultStopGiveUpAfter > XcCamera.DefaultProbeInterval + 2 * XcCamera.DefaultRequestTimeout + XcCamera.StopRepeatInterval);
    }

    // Each axis group's stop is given up like pan/tilt's: a failing zoom stop with an ERROR naming zoom,
    // and a failing Stop(), whose stops start and fail together, with one ERROR naming them all.
    [Theory]
    [InlineData("zoom", "(zoom)")]
    [InlineData("Stop()", "(pan/tilt, zoom, focus)")]
    public async Task AFailingZoomStopOrStopAll_IsGivenUp_WithOneErrorNamingItsAxes(string stop, string axes)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.IsControl ? XcReply.Refused : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: Bound);
        if (stop == "zoom")
        {
            camera.Zoom(0.5, ZoomDir.Tele);
            await fake.WaitForControlAsync(1);
            camera.Zoom(0.5, ZoomDir.Stop);
        }
        else
        {
            camera.Stop();
        }

        var error = await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);
        var sentAtError = fake.ControlRequests.Count;
        var requests = await fake.WaitUntilAsync(r => ProbeAfterTheLast(r, x => x.IsControl) != null, "a probe after the last control request");

        Assert.True(requests.Count(r => r.IsControl) == sentAtError, $"A control request arrived after the ERROR. {FakeXcCamera.Describe(requests)}");
        Assert.Contains(axes, error.Message);
        Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Error);
    }

    // The camera refuses the stop's first three sends and holds the fourth, answering it shortly before
    // or after the bound: either way the stop ends as today, sent four times, with no ERROR. The third
    // refusal completes inside the bound, so the stop is not given up there; the bound is longer than
    // the class's so that this holds on a busy host.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStopRefusedAndThenAnswered_BeforeOrAfterTheBound_EndsAsToday_WithNoError(bool pastTheBound)
    {
        var bound = TimeSpan.FromSeconds(1);
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = RefusingTheStopHolding(MinStopSends + 1, XcReply.Held) };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: bound);
        var stoppedAt = await PanAndStopAsync(fake, camera, MinStopSends + 1);

        await DelayUntilAsync(stoppedAt, pastTheBound ? bound + BoundMargin : bound - ShortlyBefore);
        fake.Release();
        await XcTestCameras.WaitForLogAsync(trace, name, "succeed again");
        await Task.Delay(SettleTime);

        var requests = fake.ControlRequests;
        Assert.True(requests.Count(r => r.IsStop) == MinStopSends + 1, FakeXcCamera.Describe(requests));
        XcTestCameras.AssertRepeatedStops(requests, r => r.IsStop);
        Assert.DoesNotContain(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Error);
    }

    // After a give-up a move is sent as normal, and a stop after it is a new stop: sent at least three
    // times and given up only once a full bound has passed since it started, with its own ERROR.
    [Fact]
    public async Task AfterAGiveUp_AMoveIsSent_AndALaterStopGetsAFullBoundAndItsOwnError()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.IsStop ? XcReply.Refused : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: Bound);
        await PanAndStopAsync(fake, camera);
        await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);

        camera.Pan(0.5, PanDir.Right);
        var move = (await fake.WaitUntilAsync(r => r.Any(x => x.Pan == "right"), "the move after the give-up")).First(x => x.Pan == "right");
        var stoppedAgainAt = Stopwatch.GetTimestamp();
        camera.Pan(0.5, PanDir.Stop);
        await XcTestCameras.WaitForLogAsync(trace, name, GaveUp, count: 2);
        var givenUpBy = Stopwatch.GetTimestamp();
        var requests = await fake.WaitUntilAsync(r => ProbeAfterTheLast(r, x => x.IsStop) is { } probe && probe.ArrivedAt > move.ArrivedAt,
            "a probe after the last stop");

        var described = FakeXcCamera.Describe(requests);
        Assert.Equal("c.1.pan.speed.dir=5005&c.1.pan=right&c.1.tilt.speed.dir=10&c.1.tilt=stop", move.Query);
        var sinceStop = Stopwatch.GetElapsedTime(stoppedAgainAt, givenUpBy);
        Assert.True(sinceStop >= Bound, $"The second stop was given up within {sinceStop.TotalMilliseconds:0.0} ms. {described}");
        Assert.True(requests.Count(r => r.IsStop && r.ArrivedAt > move.ArrivedAt) >= MinStopSends, described);
        Assert.Equal(2, XcTestCameras.LogFor(trace, name).Count(l => l.Type == TraceEventType.Error));
    }

    // A give-up does not change what later requests carry: a zoom move after a pan/tilt give-up carries
    // the zoom alone, not the given-up stop.
    [Fact]
    public async Task AfterAPanTiltGiveUp_AZoomMoveCarriesTheZoomAlone()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.IsStop ? XcReply.Refused : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: Bound);
        await PanAndStopAsync(fake, camera);
        await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);

        camera.Zoom(0.5, ZoomDir.Tele);
        var zoom = (await fake.WaitUntilAsync(r => r.Any(x => x.Zoom != null), "the zoom move")).First(x => x.Zoom != null);

        Assert.Equal("c.1.zoom.speed.dir=7&c.1.zoom=tele", zoom.Query);
    }

    // A tilt move during a failing pan stop's retries neither ends the stop (only a move on both axes
    // does) nor restarts its bound: the held send that fails past the bound counted from the pan stop
    // gives it up, and only the tilt move, never sent, goes out after it, once.
    [Fact]
    public async Task ATiltMoveDuringAFailingPanStop_LeavesTheGiveUpAtTheBoundFromThePanStop()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = RefusingTheStopHolding(MinStopSends, HeldThenRefused) };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: Bound);
        var stoppedAt = await PanAndStopAsync(fake, camera, MinStopSends);

        await DelayUntilAsync(stoppedAt, Bound / 2);
        camera.Tilt(0.5, TiltDir.Up);
        await DelayUntilAsync(stoppedAt, Bound + BoundMargin);
        fake.Release();
        var error = await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);
        await Task.Delay(SettleTime);

        var requests = fake.ControlRequests;
        var described = FakeXcCamera.Describe(requests);
        Assert.Contains("(pan/tilt)", error.Message);
        Assert.True(requests.Count == 1 + MinStopSends + 1, described);
        Assert.Equal("c.1.pan.speed.dir=10&c.1.pan=stop&c.1.tilt.speed.dir=5005&c.1.tilt=up", requests[^1].Query);
    }

    // A move on both axes during a failing stop's retries ends the stop as today: the held send fails
    // past the bound with no give-up and no ERROR, and the move goes out once.
    [Fact]
    public async Task AMoveOnBothAxesDuringAFailingStop_EndsIt_WithNoError()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = RefusingTheStopHolding(MinStopSends, HeldThenRefused) };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: Bound);
        var stoppedAt = await PanAndStopAsync(fake, camera, MinStopSends);

        camera.Pan(0.5, PanDir.Right);
        camera.Tilt(0.5, TiltDir.Up);
        await DelayUntilAsync(stoppedAt, Bound + BoundMargin);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Pan == "right"), "the move");
        await Task.Delay(SettleTime);

        var requests = fake.ControlRequests;
        Assert.True(requests.Count == 1 + MinStopSends + 1, FakeXcCamera.Describe(requests));
        Assert.Equal("c.1.pan.speed.dir=5005&c.1.pan=right&c.1.tilt.speed.dir=5005&c.1.tilt=up", requests[^1].Query);
        Assert.DoesNotContain(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Error);
    }

    // The camera's port changes while the stop's third send is in flight past the bound. That send's
    // outcome is ignored, so it gives nothing up, and the change does not restart the bound: the next
    // send, to the new port, fails and gives the stop up.
    [Fact]
    public async Task AnEndpointChangeDuringAFailingStop_NeitherGivesItUpNorRestartsItsBound()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = RefusingTheStopHolding(MinStopSends, HeldThenRefused) };
        using var moved = new FakeXcCamera { Responder = r => r.IsStop ? XcReply.Refused : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: Bound);
        var stoppedAt = await PanAndStopAsync(fake, camera, MinStopSends);
        await DelayUntilAsync(stoppedAt, Bound + BoundMargin);

        camera.Port = moved.Port;
        fake.Release();
        var error = await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);
        await Task.Delay(SettleTime);

        Assert.True(fake.ControlRequests.Count(r => r.IsStop) == MinStopSends, FakeXcCamera.Describe(fake.Requests));
        Assert.True(moved.ControlRequests.Count(r => r.IsStop) == 1, FakeXcCamera.Describe(moved.Requests));
        Assert.Contains($"127.0.0.1:{moved.Port} (pan/tilt)", error.Message);
    }

    // The camera refuses the stop throughout, and a recall is called during its retries: the recall goes
    // next and succeeds, so the stop ends, with no stop and no ERROR after it. The bound is the default:
    // the recall comes long before it.
    [Fact]
    public async Task ARecallThatSucceedsDuringAFailingStopsRetries_EndsIt_WithNoStopOrErrorAfter()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.IsStop ? XcReply.Refused : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval);
        await PanAndStopAsync(fake, camera, MinStopSends + 1);

        camera.Preset(Preset.Recall, 4);
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset != null), "the recall");
        await Task.Delay(SettleTime);

        var requests = fake.ControlRequests;
        Assert.True(requests[^1].Preset == "5", FakeXcCamera.Describe(requests));
        Assert.DoesNotContain(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Error);
    }

    // The camera refuses the stop; its third send is held, and a recall is called while it is in
    // flight, before or after the bound has passed. The send fails, but the recall goes next and takes
    // the stop's place: it succeeds, so the stop ends, with nothing more sent and no ERROR.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARecallCalledWhileAFailingStopsSendIsInFlight_TakesItsPlace_WithNoErrorWhenItSucceeds(bool pastTheBound)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = RefusingTheStopHolding(MinStopSends, HeldThenRefused) };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: Bound);
        var stoppedAt = await PanAndStopAsync(fake, camera, MinStopSends);

        if (pastTheBound)
            await DelayUntilAsync(stoppedAt, Bound + BoundMargin);
        camera.Preset(Preset.Recall, 4);
        fake.Release();
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset != null), "the recall");
        await DelayUntilAsync(stoppedAt, Bound + SettleTime);

        var requests = fake.ControlRequests;
        var described = FakeXcCamera.Describe(requests);
        Assert.True(requests[^1].Preset == "5", described);
        Assert.True(requests.Count(r => r.IsStop) == MinStopSends, described);
        Assert.DoesNotContain(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Error);
    }

    // The camera refuses the stop and the recall. The stop's third send is held until the bound has
    // passed, and a recall is called while it is in flight: the send fails while the recall is wanted,
    // the recall fails too and restores the stop as a new stop, which is sent at least three more times
    // and given up only once the bound has passed since the restore.
    [Fact]
    public async Task AStopAFailedRecallRestores_IsGivenUpOnlyOnceTheBoundHasPassedSinceTheRestore()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var refusingTheStop = RefusingTheStopHolding(MinStopSends, HeldThenRefused);
        using var fake = new FakeXcCamera { Responder = r => r.Preset != null ? XcReply.Refused : refusingTheStop(r) };
        using var camera = XcTestCameras.CameraFor(fake, name, LongTimeout, ProbeInterval, stopGiveUpAfter: Bound);
        var stoppedAt = await PanAndStopAsync(fake, camera, MinStopSends);
        await DelayUntilAsync(stoppedAt, Bound + BoundMargin);

        camera.Preset(Preset.Recall, 4);
        fake.Release();
        await XcTestCameras.WaitForLogAsync(trace, name, GaveUp);
        var givenUpBy = Stopwatch.GetTimestamp();
        var stopsAtError = fake.ControlRequests.Count(r => r.IsStop);
        var requests = await fake.WaitUntilAsync(r => ProbeAfterTheLast(r, x => x.IsStop) != null, "a probe after the last stop");

        var described = FakeXcCamera.Describe(requests);
        var recall = requests.Single(r => r.Preset != null);
        var sinceRecall = Stopwatch.GetElapsedTime(recall.ArrivedAt, givenUpBy);
        Assert.True(sinceRecall >= Bound, $"Given up within {sinceRecall.TotalMilliseconds:0.0} ms of the recall. {described}");
        Assert.True(requests.Count(r => r.IsStop && r.ArrivedAt > recall.ArrivedAt) >= MinStopSends, described);
        Assert.True(requests.Count(r => r.IsStop) == stopsAtError, $"A stop arrived after the ERROR. {described}");
        Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Error);
    }

    // A fake camera that fails every control request as named and answers probes, except where it asks
    // for a login, with the login and the request timeout the camera is given, and the HTTP requests
    // each send makes: two with a wrong login, the request and its repeat with the login.
    private static (FakeXcCamera Fake, string Username, string Password, TimeSpan RequestTimeout, int RequestsPerSend) Failing(string failure) => failure switch
    {
        "refused" => (new FakeXcCamera { Responder = r => r.IsControl ? XcReply.Refused : XcReply.Ok }, "", "", LongTimeout, 1),
        "no login" => (new FakeXcCamera { Login = new XcLogin { Username = User, Password = Secret, Digest = true } }, "", "", LongTimeout, 1),
        "wrong login" => (new FakeXcCamera { Login = new XcLogin { Username = User, Password = Secret, Digest = true } }, User, "fake-xc-wrong-password", LongTimeout, 2),
        "HTTP 500" => (new FakeXcCamera { Responder = r => r.IsControl ? XcReply.With(500, "0") : XcReply.Ok }, "", "", LongTimeout, 1),
        "unanswered" => (new FakeXcCamera { Responder = r => r.IsControl ? XcReply.Never : XcReply.Ok }, "", "", ShortTimeout, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
    };

    // A responder that refuses every send of the stop (301) but the send-th, which it answers with held,
    // a reply the fake holds until the test calls Release. It answers every other request at once.
    private static Func<XcRequest, XcReply> RefusingTheStopHolding(int send, XcReply held)
    {
        var stops = 0;
        return r => !r.IsStop ? XcReply.Ok : Interlocked.Increment(ref stops) == send ? held : XcReply.Refused;
    }

    // Pans the camera left and, once the move has gone out, stops it and waits for the stop's
    // sends-th send. Returns the time just before the stop, so no later than its start.
    private static async Task<long> PanAndStopAsync(FakeXcCamera fake, XcCamera camera, int sends = 1)
    {
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);
        var stoppedAt = Stopwatch.GetTimestamp();
        camera.Pan(0.5, PanDir.Stop);
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= sends, $"{sends} send(s) of the stop");
        return stoppedAt;
    }

    // Waits until span has passed since the Stopwatch timestamp since.
    private static async Task DelayUntilAsync(long since, TimeSpan span)
    {
        var remaining = span - Stopwatch.GetElapsedTime(since);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
    }

    // The first probe that arrived after the last request that matches; null when there is none (yet).
    private static XcRequest? ProbeAfterTheLast(IReadOnlyList<XcRequest> requests, Func<XcRequest, bool> matches)
    {
        var last = requests.LastOrDefault(matches);
        return last == null ? null : requests.FirstOrDefault(r => r.IsProbe && r.ArrivedAt > last.ArrivedAt);
    }
}
