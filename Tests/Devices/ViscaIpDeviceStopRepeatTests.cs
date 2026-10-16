using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;
using PtzJoystickControl.Core.Utils;
using PtzJoystickControl.Tests.Services;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// Stop repeats and motion commands whose timing is not asserted. Sending tests stay in this class
// or ViscaIpDeviceStopSpreadTests, so no other sends on the shared socket stretch stop timings.
public class ViscaIpDeviceStopRepeatTests : ViscaLoopbackTests
{
    [Fact]
    public async Task SpeedChangeWhileMoving_IsSentOnce()
    {
        using var camera = new LoopbackCamera();
        camera.Device.PanTilt(0.4, 0.5, PanDir.Left, TiltDir.Stop);
        await camera.WaitForPacketsAsync(1);
        await camera.CollectAsync();

        camera.Device.PanTilt(0.5, 0.5, PanDir.Left, TiltDir.Stop);
        var packets = await camera.CollectAsync();

        Assert.Single(packets);
        Assert.Equal(new byte[] { 0x81, 0x01, 0x06, 0x01, 12, 0, (byte)PanDir.Left, (byte)TiltDir.Stop, 0xFF }, packets[0].Payload);
    }

    [Fact]
    public async Task RedeflectingWithinTheRepeatWindow_EndsWithTheMotionCommand()
    {
        using var camera = new LoopbackCamera();
        camera.Device.PanTilt(0.4, 0.5, PanDir.Left, TiltDir.Stop);
        await camera.WaitForPacketsAsync(1);

        camera.Device.PanTilt(0.4, 0.5, PanDir.Stop, TiltDir.Stop);
        await Task.Delay(10);
        camera.Device.PanTilt(0.31, 0.5, PanDir.Right, TiltDir.Stop);
        var packets = await camera.CollectAsync();

        Assert.Equal(new byte[] { 0x81, 0x01, 0x06, 0x01, 8, 0, (byte)PanDir.Right, (byte)TiltDir.Stop, 0xFF }, packets.Last().Payload);
    }

    // Removal disposes the camera (docs/live-safety.md §1). With nothing pending it is released at
    // once, so a camera added again with the same address and port receives the replies.
    [Fact]
    public async Task DisposingAnIdleCamera_ReleasesItAtOnce_SoACameraAddedAgainReceivesTheReplies()
    {
        using var trace = new TraceRecorder();
        using var camera = new LoopbackCamera();
        camera.Device.Preset(Preset.Recall, 1);
        await camera.WaitForPacketsAsync(1);
        await camera.CollectAsync();

        camera.Device.Dispose();

        Assert.True(camera.Device.Released);
        Assert.False(camera.Device.Connected);
        Assert.Null(UdpSocket.ListenerFor(camera.Endpoint));
        var name = UniqueName();
        using var again = new ViscaIpDevice(name, new IPEndPoint(IPAddress.Loopback, camera.Endpoint.Port));
        again.BeginSocket();
        Assert.Same(again, UdpSocket.ListenerFor(camera.Endpoint)?.Target);
        Assert.Empty(LogFor(trace, name));
    }

    // A VISCA camera has no focus position to nudge (#62): a focus step sends nothing and logs one
    // INFO naming the camera, at most once a minute per camera. A step of share 0 or with no
    // direction does nothing at all, and no share throws.
    [Fact]
    public async Task AFocusStep_SendsNothing_AndLogsOneInfoAMinutePerCamera()
    {
        using var trace = new TraceRecorder();
        var name = UniqueName();
        var otherName = UniqueName();
        using var camera = new LoopbackCamera(name);
        using var other = new LoopbackCamera(otherName);

        camera.Device.FocusStep(0.0, FocusDir.Far);
        camera.Device.FocusStep(0.005, FocusDir.Stop);
        camera.Device.FocusStep(double.NaN, FocusDir.Near);
        Assert.Empty(LogFor(trace, name));
        camera.Device.FocusStep(0.005, FocusDir.Far);
        camera.Device.FocusStep(0.005, FocusDir.Near);
        camera.Device.FocusStep(1.5, FocusDir.Far);
        camera.Device.FocusStep(-0.5, FocusDir.Near);
        other.Device.FocusStep(0.05, FocusDir.Near);
        var packets = await camera.CollectAsync();
        var otherPackets = await other.CollectAsync(TimeSpan.Zero);

        Assert.Empty(packets);
        Assert.Empty(otherPackets);
        Assert.False(camera.Device.IsMoving);
        var line = Assert.Single(LogFor(trace, name));
        Assert.Equal(TraceEventType.Information, line.Type);
        Assert.Contains("focus step", line.Message);
        Assert.Single(LogFor(trace, otherName));
    }

    [Fact]
    public async Task CommandsAfterDispose_AreIgnored_AndThePendingStopsStillCarryStop()
    {
        using var trace = new TraceRecorder();
        var name = UniqueName();
        using var camera = new LoopbackCamera(name);
        camera.Device.Stop();
        camera.Device.Dispose();

        camera.Device.Pan(0.5, PanDir.Right);
        camera.Device.Tilt(0.5, TiltDir.Up);
        camera.Device.Zoom(0.5, ZoomDir.Tele);
        camera.Device.Focus(0.5, FocusDir.Far);
        camera.Device.FocusStep(0.5, FocusDir.Far);
        camera.Device.FocusMode(FocusMode.Auto);
        camera.Device.FocusLock(FocusLock.Lock);
        camera.Device.Power(Power.On);
        camera.Device.PresetRecallSpeed(12);
        camera.Device.Preset(Preset.Recall, 4);
        camera.Device.Stop();
        await camera.WaitForReleaseAsync();
        var packets = await camera.CollectAsync();

        Assert.False(camera.Device.IsMoving);
        Assert.All(packets, p => Assert.True(IsStop(p), Describe(packets)));
        // Exactly 3 of each: the first Stop() and its 2 repeats. The second Stop() sends nothing.
        Assert.True(packets.Count(p => IsPanTiltStop(p.Payload)) == 3, Describe(packets));
        Assert.True(packets.Count(p => IsZoomStop(p.Payload)) == 3, Describe(packets));
        Assert.True(packets.Count(p => IsFocusStop(p.Payload)) == 3, Describe(packets));
        Assert.Empty(LogFor(trace, name));
    }

    // Setting a disposed camera's address or port still records it, and closes its socket while its
    // stops are going out. It never opens another: those stops are dropped, and it logs nothing, not
    // even that it is connecting.
    [Fact]
    public async Task ADisposedCamera_NeverReopensItsSocket_AndLogsNothing()
    {
        using var trace = new TraceRecorder();
        var name = UniqueName();
        using var camera = new LoopbackCamera(name);
        var newPort = camera.Endpoint.Port == IPEndPoint.MaxPort ? camera.Endpoint.Port - 1 : camera.Endpoint.Port + 1;
        camera.Device.Stop();
        camera.Device.Dispose();

        camera.Device.Port = newPort;

        await camera.WaitForReleaseAsync();
        await camera.CollectAsync();
        Assert.Equal(newPort, camera.Device.Port);
        Assert.False(camera.Device.Connected);
        Assert.Null(UdpSocket.ListenerFor(camera.Endpoint));
        Assert.Null(UdpSocket.ListenerFor(new IPEndPoint(IPAddress.Loopback, newPort)));
        Assert.Empty(LogFor(trace, name));
    }

    // Removal stops the camera a controller is driving, through the selection, before it disposes it.
    [Fact]
    public async Task RemovingTheDrivenCamera_SendsItsStops_ThenNothingMore()
    {
        using var trace = new TraceRecorder();
        var name = UniqueName();
        using var camera = new LoopbackCamera(name);
        var service = new CamerasService(new MemoryCameraStore(camera.Device));
        SelectingGamepad.Driving(service, camera.Device);
        camera.Device.Pan(0.5, PanDir.Left);
        await camera.WaitForPacketsAsync(1);
        await camera.CollectAsync(TimeSpan.Zero);

        service.RemoveCamera(camera.Device);

        await camera.WaitForReleaseAsync();
        var packets = await camera.CollectAsync();
        // Exactly 3: the stop the removal sends and its 2 repeats.
        Assert.True(packets.Count(p => IsPanTiltStop(p.Payload)) == 3, Describe(packets));
        Assert.All(packets, p => Assert.True(IsStop(p), Describe(packets)));
        Assert.False(camera.Device.Connected);
        Assert.Null(UdpSocket.ListenerFor(camera.Endpoint));
        Assert.Empty(await camera.CollectAsync());
        Assert.Empty(LogFor(trace, name));
    }

    [Fact]
    public async Task SwitchingAwayDuringAPresetRecall_SendsNothing()
    {
        using var camera1 = new LoopbackCamera();
        using var camera2 = new LoopbackCamera();
        var gamepad = new TestGamepad { SelectedCamera = camera1.Device };
        _ = new DrivenCameras(gamepad);
        camera1.Device.Preset(Preset.Recall, 3);
        await camera1.WaitForPacketsAsync(1);
        await camera1.CollectAsync();

        gamepad.SelectedCamera = camera2.Device;
        var packets = await camera1.CollectAsync();

        Assert.Empty(packets);
    }

    [Fact]
    public async Task IsMoving_FollowsTheLastSentPanTiltZoomAndFocus()
    {
        using var camera = new LoopbackCamera();
        var device = camera.Device;
        Assert.False(device.IsMoving);

        device.PanTilt(0.4, 0.5, PanDir.Left, TiltDir.Stop);
        Assert.True(device.IsMoving);
        device.PanTilt(0.4, 0.5, PanDir.Stop, TiltDir.Stop);
        Assert.False(device.IsMoving);

        device.Tilt(0.5, TiltDir.Down);
        Assert.True(device.IsMoving);
        device.Tilt(0, TiltDir.Stop);
        Assert.False(device.IsMoving);

        device.Zoom(0.75, ZoomDir.Wide);
        Assert.True(device.IsMoving);
        device.Zoom(0, ZoomDir.Stop);
        Assert.False(device.IsMoving);

        device.Focus(0.75, FocusDir.Far);
        Assert.True(device.IsMoving);
        device.Stop();
        Assert.False(device.IsMoving);

        device.Preset(Preset.Recall, 1);
        Assert.False(device.IsMoving);
        await camera.CollectAsync();
    }

    [Fact]
    public async Task AxisReturningToDeadzone_SendsRepeatedPanStops()
    {
        using var camera = new LoopbackCamera();
        var gamepad = new TestGamepad { SelectedCamera = camera.Device };
        var pan = new PanCommand(gamepad, new NullCompanionService(), TestSpeedProfiles.Full());
        var axis = new Input("axis-0", "X", InputType.Axis, new ICommand[] { pan });
        axis.SelectedCommand = pan;

        axis.InputValue = 0.8f;
        await camera.WaitForPacketsAsync(1);
        axis.InputValue = 0.01f;
        var packets = await camera.CollectAsync();

        Assert.True(packets.Count(p => IsPanTiltStop(p.Payload)) >= MinStopSends, Describe(packets));
    }

    [Fact]
    public async Task SeveralPendingCommands_AreSentOneCommandPerPacket()
    {
        using var camera = new LoopbackCamera();

        // Queued faster than the send throttle, so all three are pending at once.
        camera.Device.PanTilt(0.4, 0.5, PanDir.Left, TiltDir.Up);
        camera.Device.Zoom(0.75, ZoomDir.Tele);
        camera.Device.Focus(0.75, FocusDir.Far);
        await camera.WaitForPacketsAsync(3);
        var packets = await camera.CollectAsync();

        Assert.All(packets, p => Assert.True(IsOneCommand(p), Describe(packets)));
        Assert.Contains(packets, p => IsPanTilt(p.Payload));
        Assert.Contains(packets, p => p.Payload.Take(4).SequenceEqual(new byte[] { 0x81, 0x01, 0x04, 0x07 }));
        Assert.Contains(packets, p => p.Payload.Take(4).SequenceEqual(new byte[] { 0x81, 0x01, 0x04, 0x08 }));
    }

    // A speed fraction becomes Min + floor(f · (Max − Min) + 1e-6): pan 1..24, tilt 1..20, zoom and
    // focus 0..7. Fraction 0 still moves at Min; out-of-range and NaN fractions are clamped into range.
    // The 1e-6 keeps a fraction a hair below a step on that step: 0.6521738767623901 (a USB stick
    // position with expo 0.5) is 14.99999917 of pan's 23 steps and sends 16, as before fractions.
    [Theory]
    [InlineData("Pan", 0.0, 1)]
    [InlineData("Pan", 1e-9, 1)]
    [InlineData("Pan", 0.5, 12)]
    [InlineData("Pan", 0.6521738767623901, 16)]
    [InlineData("Pan", 1.0, 24)]
    [InlineData("Pan", 1.5, 24)]
    [InlineData("Pan", -0.5, 1)]
    [InlineData("Pan", double.NaN, 1)]
    [InlineData("Tilt", 0.0, 1)]
    [InlineData("Tilt", 1e-9, 1)]
    [InlineData("Tilt", 0.5, 10)]
    [InlineData("Tilt", 1.0, 20)]
    [InlineData("Zoom", 0.0, 0)]
    [InlineData("Zoom", 1e-9, 0)]
    [InlineData("Zoom", 0.5, 3)]
    [InlineData("Zoom", 1.0, 7)]
    [InlineData("Zoom", 2.0, 7)]
    [InlineData("Focus", 0.0, 0)]
    [InlineData("Focus", 1e-9, 0)]
    [InlineData("Focus", 0.5, 3)]
    [InlineData("Focus", 1.0, 7)]
    public async Task AMoveAtASpeedFraction_SendsTheMatchingViscaSpeed(string axis, double fraction, int expectedSpeed)
    {
        using var camera = new LoopbackCamera();

        switch (axis)
        {
            case "Pan": camera.Device.Pan(fraction, PanDir.Right); break;
            case "Tilt": camera.Device.Tilt(fraction, TiltDir.Up); break;
            case "Zoom": camera.Device.Zoom(fraction, ZoomDir.Tele); break;
            case "Focus": camera.Device.Focus(fraction, FocusDir.Far); break;
        }
        await camera.WaitForPacketsAsync(1);
        var payload = (await camera.CollectAsync(TimeSpan.Zero))[0].Payload;

        byte[] expected = axis switch
        {
            // The idle axis keeps the device's initial speed (4), as before.
            "Pan" => new byte[] { 0x81, 0x01, 0x06, 0x01, (byte)expectedSpeed, 0x04, (byte)PanDir.Right, (byte)TiltDir.Stop, 0xFF },
            "Tilt" => new byte[] { 0x81, 0x01, 0x06, 0x01, 0x04, (byte)expectedSpeed, (byte)PanDir.Stop, (byte)TiltDir.Up, 0xFF },
            "Zoom" => new byte[] { 0x81, 0x01, 0x04, 0x07, (byte)((byte)ZoomDir.Tele | expectedSpeed), 0xFF },
            _ => new byte[] { 0x81, 0x01, 0x04, 0x08, (byte)((byte)FocusDir.Far | expectedSpeed), 0xFF },
        };
        Assert.Equal(expected, payload);
    }

    [Fact]
    public async Task ASpeedChangeThatKeepsTheViscaSpeed_SendsNothing()
    {
        using var camera = new LoopbackCamera();
        camera.Device.Pan(0.5, PanDir.Left);
        await camera.WaitForPacketsAsync(1);
        await camera.CollectAsync();

        // 0.5 and 0.52 both quantize to pan speed 12.
        camera.Device.Pan(0.52, PanDir.Left);
        var packets = await camera.CollectAsync();

        Assert.Empty(packets);
    }
}
