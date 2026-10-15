using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// Zoom and focus speeds mapped onto the ranges the camera reports in its info.cgi (#60), with Canon's
// spec values (zoom 0..15, focus 0..2) when it reports none usable.
public class XcCameraSpeedRangeTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // What a CR-N500 on firmware 1.5.2 reports, among its other items.
    private const string Firmware152Info =
        "s.firmware:=Ver.1.5.2\r\nc.1.pan.speed.min:=10\r\nc.1.pan.speed.max:=10000\r\n"
        + "c.1.zoom.speed.min:=0\r\nc.1.zoom.speed.max:=127\r\nc.1.focus.speed.min:=0\r\nc.1.focus.speed.max:=63\r\n";

    private static Func<XcRequest, XcReply> AnsweringInfoWith(string info) =>
        r => r.IsFullInfo ? XcReply.Ok with { Body = info } : XcReply.Ok;

    [Fact]
    public async Task TheFirstProbeReadsTheWholeInfo_LaterProbesOnlyTheStatus()
    {
        using var fake = new FakeXcCamera { Responder = AnsweringInfoWith(Firmware152Info) };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: ShortTimeout, probeInterval: Interval);

        var probes = (await fake.WaitUntilAsync(r => r.Count(x => x.IsProbe) >= 3, "three probes")).Where(r => r.IsProbe).ToList();

        Assert.Equal("", probes[0].Query);
        Assert.Equal("item=s", probes[1].Query);
        Assert.Equal("item=s", probes[2].Query);
    }

    [Fact]
    public async Task ACameraReportingWiderRanges_ZoomsAndFocusesAtTheTopOfThem()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = AnsweringInfoWith(Firmware152Info) };
        using var camera = XcTestCameras.CameraFor(fake, name);
        var line = await XcTestCameras.WaitForLogAsync(trace, name, "zoom speeds");

        camera.Zoom(1.0, ZoomDir.Tele);
        var zoom = await fake.WaitForControlAsync(1);
        camera.Focus(1.0, FocusDir.Far);
        var focus = await fake.WaitForControlAsync(2);
        camera.Zoom(0.5, ZoomDir.Wide);
        var halfZoom = await fake.WaitForControlAsync(3);

        Assert.Equal(TraceEventType.Information, line.Type);
        Assert.Contains("zoom speeds 0..127 (from the camera)", line.Message);
        Assert.Contains("focus speeds 0..63 (from the camera)", line.Message);
        Assert.Equal("c.1.zoom.speed.dir=127&c.1.zoom=tele", zoom.Query);
        Assert.Equal("c.1.focus.speed=63&c.1.focus.action=far", focus.Query);
        // 0 + floor(0.5 · 127 + 1e-6) = 63.
        Assert.Equal("c.1.zoom.speed.dir=63&c.1.zoom=wide", halfZoom.Query);
    }

    [Fact]
    public async Task AStoppedZoom_SendsTheBottomOfTheReportedRange()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = AnsweringInfoWith("c.1.zoom.speed.min:=1\r\nc.1.zoom.speed.max:=127\r\n") };
        using var camera = XcTestCameras.CameraFor(fake, name);
        await XcTestCameras.WaitForLogAsync(trace, name, "zoom speeds");

        camera.Zoom(0.0, ZoomDir.Tele);
        var move = await fake.WaitForControlAsync(1);
        camera.Zoom(0.0, ZoomDir.Stop);
        var stop = await fake.WaitForControlAsync(2);

        Assert.Equal("c.1.zoom.speed.dir=1&c.1.zoom=tele", move.Query);
        Assert.Equal("c.1.zoom.speed.dir=1&c.1.zoom=stop", stop.Query);
    }

    // Missing, unparseable or empty ranges fall back to the spec's, each axis on its own.
    [Theory]
    [InlineData("", 15, 2)]
    [InlineData("c.1.zoom.speed.min:=0\r\n", 15, 2)]
    [InlineData("c.1.zoom.speed.min:=0\r\nc.1.zoom.speed.max:=fast\r\nc.1.focus.speed.min:=0\r\nc.1.focus.speed.max:=\r\n", 15, 2)]
    [InlineData("c.1.zoom.speed.min:=127\r\nc.1.zoom.speed.max:=127\r\nc.1.focus.speed.min:=63\r\nc.1.focus.speed.max:=0\r\n", 15, 2)]
    [InlineData("c.1.zoom.speed.min:=0\nc.1.zoom.speed.max:=127\n", 127, 2)]
    [InlineData("c.1.focus.speed.min:=0\r\nc.1.focus.speed.max:=63\r\n", 15, 63)]
    public async Task UnusableRanges_FallBackToTheSpecs(string info, int topZoom, int topFocus)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = AnsweringInfoWith(info) };
        using var camera = XcTestCameras.CameraFor(fake, name);
        await XcTestCameras.WaitForLogAsync(trace, name, "zoom speeds");

        camera.Zoom(1.0, ZoomDir.Tele);
        var zoom = await fake.WaitForControlAsync(1);
        camera.Focus(1.0, FocusDir.Near);
        var focus = await fake.WaitForControlAsync(2);

        Assert.Equal($"c.1.zoom.speed.dir={topZoom}&c.1.zoom=tele", zoom.Query);
        Assert.Equal($"c.1.focus.speed={topFocus}&c.1.focus.action=near", focus.Query);
    }

    [Fact]
    public async Task UnreportedRanges_AreLoggedAsTheSpecs()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake, name);

        var line = await XcTestCameras.WaitForLogAsync(trace, name, "zoom speeds");

        Assert.Contains("zoom speeds 0..15 (Canon's spec", line.Message);
        Assert.Contains("focus speeds 0..2 (Canon's spec", line.Message);
    }

    [Fact]
    public async Task AReconnectingCamera_ReadsTheRangesAgain_LoggingThemOnlyWhenTheyChange()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var answering = 1;
        var info = Firmware152Info;
        using var fake = new FakeXcCamera
        {
            Responder = r => Volatile.Read(ref answering) == 0 ? XcReply.Never
                : r.IsFullInfo ? XcReply.Ok with { Body = Volatile.Read(ref info) } : XcReply.Ok,
        };
        using var camera = new XcCamera(name, ShortTimeout, Interval);
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        await XcTestCameras.WaitForLogAsync(trace, name, "zoom speeds");

        // Unreachable, then back with the same ranges: read again, not logged again.
        await Reconnect(camera, fake, () => Volatile.Write(ref answering, 0), () => Volatile.Write(ref answering, 1));
        Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Message.Contains("zoom speeds"));

        // Unreachable, then back with other ranges: logged.
        await Reconnect(camera, fake, () => Volatile.Write(ref answering, 0), () =>
        {
            Volatile.Write(ref info, "c.1.zoom.speed.min:=0\r\nc.1.zoom.speed.max:=31\r\n");
            Volatile.Write(ref answering, 1);
        });
        var line = await XcTestCameras.WaitForLogAsync(trace, name, "zoom speeds", count: 2);
        Assert.Contains("zoom speeds 0..31 (from the camera)", line.Message);
        camera.Zoom(1.0, ZoomDir.Tele);
        var zoom = await fake.WaitForControlAsync(1);
        Assert.Equal("c.1.zoom.speed.dir=31&c.1.zoom=tele", zoom.Query);
    }

    // Silences the camera until it turns unreachable, answers again, and waits for a status probe
    // after the next whole-info read, so that read has completed.
    private static async Task Reconnect(XcCamera camera, FakeXcCamera fake, Action silence, Action answer)
    {
        var unreachable = ConnectedBecomes(camera, false);
        silence();
        await unreachable.WaitAsync(Wait);
        var fullReads = fake.Requests.Count(r => r.IsFullInfo);
        answer();
        await fake.WaitUntilAsync(r =>
        {
            var reads = r.Select((x, i) => (x, i)).Where(p => p.x.IsFullInfo).Select(p => p.i).ToList();
            return reads.Count > fullReads && r.Skip(reads[^1] + 1).Any(x => x.IsProbe && !x.IsFullInfo);
        }, "a whole-info read after the reconnect, then a status probe");
    }

    private static Task ConnectedBecomes(XcCamera camera, bool value)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(XcCamera.Connected) && camera.Connected == value)
                changed.TrySetResult();
        };
        return changed.Task;
    }
}
