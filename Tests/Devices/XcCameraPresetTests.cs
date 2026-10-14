using System.Diagnostics;
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

    [Fact]
    public async Task ARecallMadeWhileAStopIsRepeated_GoesOutAfterTheStopsLastSend()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);

        camera.Pan(0.5, PanDir.Stop);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsStop), "the first stop");
        camera.Preset(Preset.Recall, 0);
        await fake.WaitUntilAsync(r => r.Any(x => x.Preset != null), "the recall");
        await Task.Delay(SettleTime);

        var requests = fake.ControlRequests.ToList();
        var recall = requests.FindIndex(r => r.Preset != null);
        Assert.True(requests.Take(recall).Count(r => r.IsStop) >= MinStopSends, FakeXcCamera.Describe(requests));
        Assert.Equal("p=1", requests[recall].Query);
        Assert.Equal(recall, requests.Count - 1);
    }

    [Fact]
    public async Task AStopEnteredAfterARecallBeforeEitherWasSent_GoesOutFirst_ThenTheRecall()
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
        Assert.True(afterHeld.Count >= MinStopSends + 1, FakeXcCamera.Describe(afterHeld));
        Assert.All(afterHeld.SkipLast(1), r => Assert.Equal("c.1.zoom.speed.dir=0&c.1.zoom=stop", r.Query));
        Assert.Equal("p=5", afterHeld.Last().Query);
    }

    [Fact]
    public async Task Stop_DropsARecallNotYetSent()
    {
        using var fake = new FakeXcCamera { Responder = XcTestCameras.HoldingTheFirstControlRequest() };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongRequestTimeout);
        camera.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);

        // While the pan request is held, a preset is recalled and then everything is stopped.
        camera.Preset(Preset.Recall, 4);
        camera.Stop();
        fake.Release();
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= MinStopSends, "three stops");
        await Task.Delay(SettleTime);

        Assert.DoesNotContain(fake.ControlRequests, r => r.Preset != null);
        Assert.True(fake.ControlRequests.Last().IsStop, FakeXcCamera.Describe(fake.Requests));
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
}
