using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// Focus steps on an XC camera (#62): the camera reads its focus position (info.cgi?item=c.1.focus.value)
// when the position it knows may be stale, then writes the newest wanted position
// (control.cgi?c.1.focus.value=n), clamped to its focus position range, in requests of their own.
public class XcCameraFocusStepTests
{
    // Long enough that the first probe is the only one the camera makes by itself.
    internal static readonly TimeSpan LongProbeInterval = TimeSpan.FromSeconds(30);

    // Long enough that a held request is still outstanding when the test releases it.
    internal static readonly TimeSpan LongRequestTimeout = TimeSpan.FromSeconds(30);

    // Long enough that no step in a test reads again because of the pause.
    internal static readonly TimeSpan LongPause = TimeSpan.FromSeconds(30);

    // How long to keep collecting before asserting that nothing more was sent.
    internal static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(300);

    internal const string FocusRead = "/-wvhttp-01-/info.cgi?item=c.1.focus.value";

    internal static string FocusWrite(int position) => $"/-wvhttp-01-/control.cgi?c.1.focus.value={position}";

    internal static string FocusAt(int position) => $"c.1.focus.value:={position}\r\n";

    // Answers a focus read with the position, and everything else with success and no body.
    internal static Func<XcRequest, XcReply> AnsweringFocusAt(int position) =>
        r => r.IsFocusRead ? XcReply.Ok with { Body = FocusAt(position) } : XcReply.Ok;

    // Holds the first focus read, answering it with the position once the test releases it; answers
    // every other request at once, a focus read with the position too.
    internal static Func<XcRequest, XcReply> HoldingTheFirstFocusReadAt(int position)
    {
        var held = XcTestCameras.HoldingTheFirst(r => r.IsFocusRead);
        return r => r.IsFocusRead ? held(r) with { Body = FocusAt(position) } : XcReply.Ok;
    }

    // A camera that already sent its first probe, with a long probe interval and request timeout.
    internal static async Task<XcCamera> CameraAsync(FakeXcCamera fake, string? name = null, TimeSpan? focusStepPause = null)
    {
        var camera = XcTestCameras.CameraFor(fake, name, requestTimeout: LongRequestTimeout, probeInterval: LongProbeInterval,
            focusStepPause: focusStepPause ?? LongPause);
        await fake.WaitForProbeAsync(1);
        return camera;
    }

    internal static async Task<IReadOnlyList<XcRequest>> FocusRequestsAsync(FakeXcCamera fake, int count)
    {
        await fake.WaitUntilAsync(r => r.Count(x => x.IsFocus) >= count, $"{count} focus request(s)");
        await Task.Delay(SettleTime);
        return fake.FocusRequests;
    }

    [Fact]
    public async Task TheFirstStep_ReadsTheFocusPosition_ThenWritesItOneStepOn()
    {
        using var fake = new FakeXcCamera { Responder = AnsweringFocusAt(1000) };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);

        var focus = await FocusRequestsAsync(fake, 2);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010) }, focus.Select(r => r.Target));
        Assert.Empty(fake.ControlRequests);
    }

    [Fact]
    public async Task StepsWithinThePause_WriteFromTheKnownPosition_WithNoRead()
    {
        using var fake = new FakeXcCamera { Responder = AnsweringFocusAt(1000) };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusWrite), "the first write");
        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Count(x => x.IsFocusWrite) >= 2, "the second write");
        camera.FocusStep(0.0005, FocusDir.Near);

        var focus = await FocusRequestsAsync(fake, 4);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010), FocusWrite(1020), FocusWrite(1019) }, focus.Select(r => r.Target));
    }

    // The position changed on the camera (its own web page, say) while no step came: the first step
    // after the pause reads it again and moves on from there.
    [Fact]
    public async Task AStepAfterThePause_ReadsThePositionAgain()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), XcCamera.DefaultFocusStepPause);
        var position = 1000;
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsFocusRead ? XcReply.Ok with { Body = FocusAt(Volatile.Read(ref position)) } : XcReply.Ok,
        };
        var pause = TimeSpan.FromMilliseconds(200);
        using var camera = await CameraAsync(fake, focusStepPause: pause);

        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusWrite), "the first write");
        Volatile.Write(ref position, 1500);
        await Task.Delay(pause + TimeSpan.FromMilliseconds(100));
        camera.FocusStep(0.005, FocusDir.Near);

        var focus = await FocusRequestsAsync(fake, 4);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010), FocusRead, FocusWrite(1490) }, focus.Select(r => r.Target));
    }

    // A share maps onto the range as the nearest whole number of units, never less than 1; on 0..2048,
    // 0.05 % is 1 unit, 0.5 % is 10 and 5 % is 102. A share above 1 is clamped to 1.
    [Theory]
    [InlineData(0.0005, FocusDir.Far, 1001)]
    [InlineData(0.001, FocusDir.Near, 998)]
    [InlineData(0.0025, FocusDir.Far, 1005)]
    [InlineData(0.005, FocusDir.Near, 990)]
    [InlineData(0.01, FocusDir.Far, 1020)]
    [InlineData(0.02, FocusDir.Near, 959)]
    [InlineData(0.05, FocusDir.Far, 1102)]
    [InlineData(0.00001, FocusDir.Near, 999)]
    [InlineData(1.5, FocusDir.Far, 2048)]
    [InlineData(1.5, FocusDir.Near, 0)]
    public async Task AStep_MovesByTheNearestWholeNumberOfUnits(double share, FocusDir direction, int expected)
    {
        using var fake = new FakeXcCamera { Responder = AnsweringFocusAt(1000) };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(share, direction);

        var focus = await FocusRequestsAsync(fake, 2);
        Assert.Equal(new[] { FocusRead, FocusWrite(expected) }, focus.Select(r => r.Target));
    }

    [Theory]
    [InlineData(0.0, FocusDir.Far)]
    [InlineData(0.005, FocusDir.Stop)]
    [InlineData(double.NaN, FocusDir.Near)]
    [InlineData(-0.5, FocusDir.Far)]
    [InlineData(double.NegativeInfinity, FocusDir.Near)]
    public async Task AStepOfNothing_SendsNothing(double share, FocusDir direction)
    {
        using var fake = new FakeXcCamera { Responder = AnsweringFocusAt(1000) };
        using var camera = await CameraAsync(fake);

        var thrown = Record.Exception(() => camera.FocusStep(share, direction));
        await Task.Delay(SettleTime);

        Assert.Null(thrown);
        Assert.Empty(fake.FocusRequests);
        Assert.Empty(fake.ControlRequests);
    }

    // Steps that arrive while the read is in flight add up, each clamped to the range at that step:
    // past an end the position stays at the end, and the first step back writes a position inside the
    // range. Only the newest wanted position is written.
    [Theory]
    [InlineData(2040, new[] { 1, 1, 1, -1 }, 0.005, 2038)]
    [InlineData(5, new[] { -1, -1, 1 }, 0.005, 10)]
    [InlineData(1000, new[] { 1, -1, 1, -1 }, 0.005, 1000)]
    [InlineData(1000, new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1 }, 0.05, 1946)]
    public async Task StepsWhileTheReadIsInFlight_AddUp_ClampedAtEachStep(int position, int[] steps, double share, int expected)
    {
        using var fake = new FakeXcCamera { Responder = HoldingTheFirstFocusReadAt(position) };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(share, steps[0] > 0 ? FocusDir.Far : FocusDir.Near);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusRead), "the focus read");
        foreach (var step in steps.Skip(1))
            camera.FocusStep(share, step > 0 ? FocusDir.Far : FocusDir.Near);
        fake.Release();

        var focus = await FocusRequestsAsync(fake, 2);
        Assert.Equal(new[] { FocusRead, FocusWrite(expected) }, focus.Select(r => r.Target));
    }

    // At an end, more steps that way keep the position at the end, and the first step back moves it
    // inside the range.
    [Fact]
    public async Task StepsPastAnEnd_StayAtTheEnd_AndTheFirstStepBackMovesInside()
    {
        using var fake = new FakeXcCamera { Responder = AnsweringFocusAt(2045) };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusWrite), "the first write");
        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Count(x => x.IsFocusWrite) >= 2, "the second write");
        camera.FocusStep(0.005, FocusDir.Near);

        var focus = await FocusRequestsAsync(fake, 4);
        Assert.Equal(new[] { FocusRead, FocusWrite(2048), FocusWrite(2048), FocusWrite(2038) }, focus.Select(r => r.Target));
    }

    // A focus request waits the normal gap after the previous request completed.
    [Fact]
    public async Task TheWrite_WaitsTheNormalGapAfterTheRead()
    {
        var readAnsweredAt = 0L;
        using var fake = new FakeXcCamera
        {
            Responder = r =>
            {
                if (!r.IsFocusRead)
                    return XcReply.Ok;
                Volatile.Write(ref readAnsweredAt, Stopwatch.GetTimestamp());
                return XcReply.Ok with { Body = FocusAt(1000) };
            },
        };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);
        var requests = await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusWrite), "the write");

        var write = requests.First(x => x.IsFocusWrite);
        var gap = Stopwatch.GetElapsedTime(Volatile.Read(ref readAnsweredAt), write.ArrivedAt);
        Assert.True(gap >= XcCamera.MinRequestGap, $"The write arrived {gap.TotalMilliseconds:0.0} ms after the read was answered. {FakeXcCamera.Describe(requests)}");
    }

    // A focus step is not a move: IsMoving never changes, and no stop, move or focus mode item follows.
    [Fact]
    public async Task AStep_IsNotAMove()
    {
        using var fake = new FakeXcCamera { Responder = AnsweringFocusAt(1000) };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);
        Assert.False(camera.IsMoving);
        camera.FocusStep(0.05, FocusDir.Near);
        Assert.False(camera.IsMoving);
        await FocusRequestsAsync(fake, 2);

        Assert.False(camera.IsMoving);
        Assert.Empty(fake.ControlRequests);
        Assert.All(fake.Requests, r => Assert.DoesNotContain("c.1.focus=", r.Query));
        Assert.All(fake.Requests, r => Assert.DoesNotContain("c.1.focus.action", r.Query));
        Assert.All(fake.Requests, r => Assert.DoesNotContain("c.1.focus.speed", r.Query));
    }

    // The whole info.cgi read that reads the speed ranges also reads the focus position range, and the
    // steps move within it: 0.5 % of 0..4096 is round(20.48) = 20 units, 0.5 % of 100..1100 is 5 and
    // 5 % is 50.
    [Theory]
    [InlineData("c.1.focus.value.min:=0\r\nc.1.focus.value.max:=4096\r\n", "focus positions 0..4096 (from the camera)", 1000, 0.005, FocusDir.Far, 1020)]
    [InlineData("c.1.focus.value.min:=100\r\nc.1.focus.value.max:=1100\r\n", "focus positions 100..1100 (from the camera)", 1000, 0.005, FocusDir.Near, 995)]
    [InlineData("c.1.focus.value.min:=100\r\nc.1.focus.value.max:=1100\r\n", "focus positions 100..1100 (from the camera)", 1098, 0.005, FocusDir.Far, 1100)]
    [InlineData("c.1.focus.value.min:=100\r\nc.1.focus.value.max:=1100\r\n", "focus positions 100..1100 (from the camera)", 120, 0.05, FocusDir.Near, 100)]
    public async Task TheFocusPositionRange_ComesFromTheWholeInfoRead(string info, string logged, int position, double share, FocusDir direction, int expected)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsFullInfo ? XcReply.Ok with { Body = info } : AnsweringFocusAt(position)(r),
        };
        using var camera = await CameraAsync(fake, name);
        var line = await XcTestCameras.WaitForLogAsync(trace, name, "focus positions");

        camera.FocusStep(share, direction);

        var focus = await FocusRequestsAsync(fake, 2);
        Assert.Equal(TraceEventType.Information, line.Type);
        Assert.Contains(logged, line.Message);
        Assert.Equal(new[] { FocusRead, FocusWrite(expected) }, focus.Select(r => r.Target));
    }

    // Missing, unparseable or empty focus position ranges fall back to Canon's 0..2048, logged as the
    // spec's.
    [Theory]
    [InlineData("")]
    [InlineData("c.1.focus.value.min:=0\r\n")]
    [InlineData("c.1.focus.value.min:=0\r\nc.1.focus.value.max:=far\r\n")]
    [InlineData("c.1.focus.value.min:=2048\r\nc.1.focus.value.max:=2048\r\n")]
    [InlineData("c.1.focus.value:=1000\r\nc.1.focus.speed.min:=0\r\nc.1.focus.speed.max:=63\r\n")]
    public async Task UnusableFocusPositionRanges_FallBackTo0To2048(string info)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsFullInfo ? XcReply.Ok with { Body = info } : AnsweringFocusAt(1000)(r),
        };
        using var camera = await CameraAsync(fake, name);
        var line = await XcTestCameras.WaitForLogAsync(trace, name, "focus positions");

        camera.FocusStep(0.005, FocusDir.Far);

        var focus = await FocusRequestsAsync(fake, 2);
        Assert.Contains("focus positions 0..2048 (Canon's spec", line.Message);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010) }, focus.Select(r => r.Target));
    }

    // An address or port change goes back to 0..2048 until the next whole info.cgi read takes the
    // camera's range. Here that read fails, so the step still moves within 0..2048.
    [Fact]
    public async Task AnAddressChange_GoesBackTo0To2048_UntilTheNextRead()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var first = new FakeXcCamera
        {
            Responder = r => r.IsFullInfo ? XcReply.Ok with { Body = "c.1.focus.value.min:=0\r\nc.1.focus.value.max:=4096\r\n" } : XcReply.Ok,
        };
        using var second = new FakeXcCamera
        {
            Responder = r => r.IsFullInfo ? XcReply.With(500, "0") : AnsweringFocusAt(1000)(r),
        };
        using var camera = await CameraAsync(first, name);
        await XcTestCameras.WaitForLogAsync(trace, name, "focus positions 0..4096");

        camera.Port = second.Port;
        await XcTestCameras.WaitForLogAsync(trace, name, "failed: HTTP 500");
        camera.FocusStep(0.005, FocusDir.Far);

        var focus = await FocusRequestsAsync(second, 2);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010) }, focus.Select(r => r.Target));
    }

    // c.1.focus.value:=n or c.1.focus.value==n in a read's answer gives the position, among other lines
    // too.
    [Theory]
    [InlineData("c.1.focus.value:=1000\r\n")]
    [InlineData("c.1.focus.value==1000\r\n")]
    [InlineData("c.1.focus.value.min:=0\r\nc.1.focus.value:=1000\r\nc.1.focus.value.max:=2048\r\n")]
    [InlineData("c.1.focus.value:= 1000 \n")]
    public async Task AReadsAnswer_GivesThePosition(string body)
    {
        using var fake = new FakeXcCamera { Responder = r => r.IsFocusRead ? XcReply.Ok with { Body = body } : XcReply.Ok };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);

        var focus = await FocusRequestsAsync(fake, 2);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010) }, focus.Select(r => r.Target));
    }

    // A focus read's or write's answer is read as UTF-8, or as its byte order mark says, whatever
    // charset its Content-Type names: one the client does not know stops nothing. Each write's answer
    // reports the position written.
    [Theory]
    [InlineData("text/plain; charset=not-a-charset", "c.1.focus.value:=1000\r\n")]
    [InlineData("text/plain; charset=not-a-charset", "\uFEFFc.1.focus.value:=1000\r\n")]
    [InlineData("text/plain", "\uFEFFc.1.focus.value:=1000\r\n")]
    public async Task AFocusAnswer_IsReadWhateverCharsetItNames(string contentType, string readAnswer)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsFocusRead ? XcReply.Ok with { ContentType = contentType, Body = readAnswer }
                : r.IsFocusWrite ? XcReply.Ok with { ContentType = contentType, Body = $"c.1.focus.value:={r.FocusValue}\r\n" }
                : XcReply.Ok,
        };
        using var camera = await CameraAsync(fake, name);

        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusWrite), "the first write");
        camera.FocusStep(0.005, FocusDir.Far);

        var focus = await FocusRequestsAsync(fake, 3);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010), FocusWrite(1020) }, focus.Select(r => r.Target));
        Assert.DoesNotContain(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Error);
    }

    // A write's answer that reports the position sets it, and the next step moves on from there; a
    // write's answer with no position line counts the position sent as set (the cases above).
    [Fact]
    public async Task AWriteAnsweredWithAPosition_TheNextStepMovesOnFromIt()
    {
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsFocusRead ? XcReply.Ok with { Body = FocusAt(1000) }
                : r.IsFocusWrite ? XcReply.Ok with { Body = "c.1.focus.value:=1234\r\n" }
                : XcReply.Ok,
        };
        using var camera = await CameraAsync(fake);

        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusWrite), "the first write");
        camera.FocusStep(0.005, FocusDir.Far);

        var focus = await FocusRequestsAsync(fake, 3);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010), FocusWrite(1244) }, focus.Select(r => r.Target));
    }

    // c.1.focus.value==? (no focus position in the camera's current mode, such as auto focus), whatever
    // the livescope-status: the steps not yet sent are dropped, with one INFO a minute, and it is not a
    // failed request. The next step reads again.
    [Theory]
    [InlineData("0", "(livescope-status 0)")]
    [InlineData("301", "(livescope-status 301)")]
    [InlineData(null, "(no livescope-status header)")]
    public async Task AReadAnsweredNotAvailable_DropsTheSteps_WithOneInfo_AndIsNotAFailure(string? livescope, string logged)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var held = XcTestCameras.HoldingTheFirst(r => r.IsFocusRead);
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsFocusRead ? held(r) with { Livescope = livescope, Body = "c.1.focus.value==?\r\n" } : XcReply.Ok,
        };
        using var camera = await CameraAsync(fake, name);
        await XcTestCameras.WaitForLogAsync(trace, name, "is connected");

        camera.FocusStep(0.005, FocusDir.Far);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsFocusRead), "the focus read");
        camera.FocusStep(0.005, FocusDir.Far);
        camera.FocusStep(0.005, FocusDir.Far);
        fake.Release();
        var line = await XcTestCameras.WaitForLogAsync(trace, name, "not available");
        camera.FocusStep(0.005, FocusDir.Near);

        var focus = await FocusRequestsAsync(fake, 2);
        Assert.Equal(new[] { FocusRead, FocusRead }, focus.Select(r => r.Target));
        Assert.Equal(TraceEventType.Information, line.Type);
        Assert.Contains("focus position", line.Message);
        Assert.Contains("not available in its current mode", line.Message);
        Assert.EndsWith(logged, line.Message);
        var log = XcTestCameras.LogFor(trace, name);
        Assert.Single(log, l => l.Message.Contains("not available"));
        Assert.DoesNotContain(log, l => l.Type == TraceEventType.Warning);
        Assert.True(camera.Connected);
        Assert.Null(camera.FailureReason);
    }

    // A successful read with no whole-number position is not available either.
    [Theory]
    [InlineData("")]
    [InlineData("c.1.focus.value:=near\r\n")]
    [InlineData("c.1.focus.value.min:=0\r\nc.1.focus.value.max:=2048\r\n")]
    public async Task ASuccessfulReadWithNoPosition_IsNotAvailable(string body)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.IsFocusRead ? XcReply.Ok with { Body = body } : XcReply.Ok };
        using var camera = await CameraAsync(fake, name);
        await XcTestCameras.WaitForLogAsync(trace, name, "is connected");

        camera.FocusStep(0.005, FocusDir.Far);
        var line = await XcTestCameras.WaitForLogAsync(trace, name, "not available");

        var focus = await FocusRequestsAsync(fake, 1);
        Assert.Equal(new[] { FocusRead }, focus.Select(r => r.Target));
        Assert.EndsWith("(livescope-status 0)", line.Message);
        Assert.DoesNotContain(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
        Assert.True(camera.Connected);
    }

    // A write answered c.1.focus.value==? is not available too: the next step reads again.
    [Fact]
    public async Task AWriteAnsweredNotAvailable_TheNextStepReadsAgain()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsFocusRead ? XcReply.Ok with { Body = FocusAt(1000) }
                : r.IsFocusWrite ? XcReply.Ok with { Body = "c.1.focus.value==?\r\n" }
                : XcReply.Ok,
        };
        using var camera = await CameraAsync(fake, name);

        camera.FocusStep(0.005, FocusDir.Far);
        await XcTestCameras.WaitForLogAsync(trace, name, "not available");
        camera.FocusStep(0.005, FocusDir.Far);

        var focus = await FocusRequestsAsync(fake, 4);
        Assert.Equal(new[] { FocusRead, FocusWrite(1010), FocusRead, FocusWrite(1010) }, focus.Select(r => r.Target));
        Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Message.Contains("not available"));
        Assert.DoesNotContain(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
    }

    // Any other failed focus read is a failed request, with its WARN: an HTTP error, even with
    // c.1.focus.value==? in its body, a livescope-status error, or no livescope-status. Its steps are
    // dropped, never retried, and the next step reads again.
    [Theory]
    [InlineData(500, "0", "c.1.focus.value==?\r\n", "HTTP 500")]
    [InlineData(200, "301", "c.1.focus.value:=1000\r\n", "no camera control right: another client may hold control")]
    [InlineData(200, null, "c.1.focus.value:=1000\r\n", "no livescope-status header")]
    public async Task AFailedRead_IsAFailedRequest_AndDropsTheSteps(int status, string? livescope, string body, string failure)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var failed = 0;
        using var fake = new FakeXcCamera
        {
            Responder = r => !r.IsFocusRead ? XcReply.Ok
                : Interlocked.Exchange(ref failed, 1) == 0 ? XcReply.With(status, livescope) with { Body = body }
                : XcReply.Ok with { Body = FocusAt(1000) },
        };
        using var camera = await CameraAsync(fake, name);

        camera.FocusStep(0.005, FocusDir.Far);
        var warning = await XcTestCameras.WaitForLogAsync(trace, name, "failed:");
        camera.FocusStep(0.005, FocusDir.Near);

        var focus = await FocusRequestsAsync(fake, 3);
        Assert.Equal(new[] { FocusRead, FocusRead, FocusWrite(990) }, focus.Select(r => r.Target));
        Assert.Equal(TraceEventType.Warning, warning.Type);
        Assert.EndsWith($"failed: {failure}", warning.Message);
        Assert.DoesNotContain(XcTestCameras.LogFor(trace, name), l => l.Message.Contains("not available"));
    }

    // A focus read the camera does not answer times out like any request, with its WARN, and drops the
    // steps.
    [Fact]
    public async Task AnUnansweredRead_IsAFailedRequest_AndDropsTheSteps()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var failed = 0;
        using var fake = new FakeXcCamera
        {
            Responder = r => !r.IsFocusRead ? XcReply.Ok
                : Interlocked.Exchange(ref failed, 1) == 0 ? XcReply.Never
                : XcReply.Ok with { Body = FocusAt(1000) },
        };
        using var camera = XcTestCameras.CameraFor(fake, name, requestTimeout: TimeSpan.FromMilliseconds(200), probeInterval: LongProbeInterval, focusStepPause: LongPause);
        await fake.WaitForProbeAsync(1);

        camera.FocusStep(0.005, FocusDir.Far);
        var warning = await XcTestCameras.WaitForLogAsync(trace, name, "failed:");
        camera.FocusStep(0.005, FocusDir.Near);

        var focus = await FocusRequestsAsync(fake, 3);
        Assert.Equal(new[] { FocusRead, FocusRead, FocusWrite(990) }, focus.Select(r => r.Target));
        Assert.EndsWith("failed: no answer within 200 ms", warning.Message);
    }

    // A successful focus request counts for reachability like any other request.
    [Fact]
    public async Task ASuccessfulFocusRequest_CountsForReachability()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsProbe ? XcReply.With(500, "0") : AnsweringFocusAt(1000)(r),
        };
        using var camera = await CameraAsync(fake, name);
        await XcTestCameras.WaitForLogAsync(trace, name, "failed: HTTP 500");
        Assert.False(camera.Connected);

        camera.FocusStep(0.005, FocusDir.Far);
        var line = await XcTestCameras.WaitForLogAsync(trace, name, "is connected");

        Assert.Contains("succeed again", line.Message);
        Assert.True(camera.Connected);
        Assert.Null(camera.FailureReason);
    }
}

// Focus step cases that assert wall-clock timing (see TimingBoundCollection).
[Collection(TimingBoundCollection.Name)]
public class XcCameraFocusStepTimingTests
{
    // 20 steps 17 ms apart, against a camera that holds each answer for 100 ms: one read, and fewer
    // than 10 writes, the last at the position read plus 20 steps.
    [Fact]
    public async Task TwentyQuickSteps_AgainstSlowAnswers_GiveOneRead_AndFewWrites_EndingTwentyStepsOn()
    {
        var hold = TimeSpan.FromMilliseconds(100);
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsFocusRead ? XcReply.After(hold) with { Body = XcCameraFocusStepTests.FocusAt(1000) }
                : r.IsFocusWrite ? XcReply.After(hold)
                : XcReply.Ok,
        };
        using var camera = XcTestCameras.CameraFor(fake, probeInterval: XcCameraFocusStepTests.LongProbeInterval, focusStepPause: XcCameraFocusStepTests.LongPause);
        await fake.WaitForProbeAsync(1);

        for (var i = 0; i < 20; i++)
        {
            if (i > 0)
                await Task.Delay(17);
            camera.FocusStep(0.005, FocusDir.Far);
        }
        await fake.WaitUntilAsync(r => r.Any(x => x.FocusValue == "1200"), "the write at 1200");
        await Task.Delay(XcCameraFocusStepTests.SettleTime);

        var focus = fake.FocusRequests;
        var described = FakeXcCamera.Describe(focus);
        Assert.True(focus.Count(r => r.IsFocusRead) == 1, described);
        var writes = focus.Where(r => r.IsFocusWrite).ToList();
        Assert.True(writes.Count < 10, described);
        Assert.Equal("1200", writes[^1].FocusValue);
        Assert.True(focus[0].IsFocusRead, described);
    }
}
