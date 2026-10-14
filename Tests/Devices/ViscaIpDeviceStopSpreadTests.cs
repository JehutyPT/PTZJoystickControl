using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;
using PtzJoystickControl.Core.Utils;
using PtzJoystickControl.Tests.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// The stop repeat tests that assert how far apart the first three stops arrive. They run on their
// own: a parallel run starves the repeat timers and pushed the single-axis spreads past 400 ms.
[Collection(TimingBoundCollection.Name)]
public class ViscaIpDeviceStopSpreadTests : ViscaLoopbackTests
{
    [Fact]
    public async Task PanTiltStop_IsSentAtLeastThreeTimes()
    {
        using var camera = new LoopbackCamera();
        camera.Device.PanTilt(0.4, 0.5, PanDir.Left, TiltDir.Up);
        await camera.WaitForPacketsAsync(1);

        camera.Device.PanTilt(0.4, 0.5, PanDir.Stop, TiltDir.Stop);
        var packets = await camera.CollectAsync(SingleAxisStopWindow);

        AssertStopsWithin(packets, IsPanTiltStop, SingleAxisStopSpread);
    }

    [Fact]
    public async Task ZoomStop_IsSentAtLeastThreeTimes()
    {
        using var camera = new LoopbackCamera();
        camera.Device.Zoom(0.75, ZoomDir.Tele);
        await camera.WaitForPacketsAsync(1);

        camera.Device.Zoom(0, ZoomDir.Stop);
        var packets = await camera.CollectAsync(SingleAxisStopWindow);

        AssertStopsWithin(packets, IsZoomStop, SingleAxisStopSpread);
    }

    [Fact]
    public async Task FocusStop_IsSentAtLeastThreeTimes()
    {
        using var camera = new LoopbackCamera();
        camera.Device.Focus(0.75, FocusDir.Far);
        await camera.WaitForPacketsAsync(1);

        camera.Device.Focus(0, FocusDir.Stop);
        var packets = await camera.CollectAsync(SingleAxisStopWindow);

        AssertStopsWithin(packets, IsFocusStop, SingleAxisStopSpread);
    }

    [Fact]
    public async Task Stop_SendsRepeatedStopsForEveryAxis_EvenWhenAlreadyStopped()
    {
        using var camera = new LoopbackCamera();

        camera.Device.Stop();
        var packets = await camera.CollectAsync();

        AssertStopsWithin(packets, IsPanTiltStop, ForceStopSpread);
        AssertStopsWithin(packets, IsZoomStop, ForceStopSpread);
        AssertStopsWithin(packets, IsFocusStop, ForceStopSpread);
    }

    [Fact]
    public async Task PanStoppingWhileTiltMoves_RepeatsTheStopAndKeepsTilting()
    {
        using var camera = new LoopbackCamera();
        camera.Device.PanTilt(0.4, 0.5, PanDir.Left, TiltDir.Up);
        await camera.WaitForPacketsAsync(1);
        await camera.CollectAsync();

        camera.Device.PanTilt(0.4, 0.5, PanDir.Stop, TiltDir.Up);
        var packets = await camera.CollectAsync(SingleAxisStopWindow);

        AssertStopsWithin(packets, IsPanStopTiltUp, SingleAxisStopSpread);
        Assert.DoesNotContain(packets, p => IsPanTilt(p.Payload) && p.Payload[7] == (byte)TiltDir.Stop);
        Assert.True(IsPanStopTiltUp(packets.Last().Payload), Describe(packets));
    }

    [Fact]
    public async Task Stop_StopsAMovingCamera()
    {
        using var camera = new LoopbackCamera();
        camera.Device.PanTilt(0.4, 0.5, PanDir.Left, TiltDir.Up);
        await camera.WaitForPacketsAsync(1);
        camera.Device.Zoom(0.75, ZoomDir.Wide);
        await camera.WaitForPacketsAsync(2);
        await camera.CollectAsync();

        camera.Device.Stop();
        var packets = await camera.CollectAsync();

        AssertStopsWithin(packets, IsPanTiltStop, ForceStopSpread);
        AssertStopsWithin(packets, IsZoomStop, ForceStopSpread);
        AssertStopsWithin(packets, IsFocusStop, ForceStopSpread);
        Assert.DoesNotContain(packets, p => !IsPanTiltStop(p.Payload) && !IsZoomStop(p.Payload) && !IsFocusStop(p.Payload));
    }

    [Fact]
    public async Task ControllerGoingAway_SendsRepeatedStopsToTheCameraItWasDriving()
    {
        using var camera = new LoopbackCamera();
        var gamepad = new TestGamepad { SelectedCamera = camera.Device };
        var driven = new DrivenCameras(gamepad);
        var pan = new PanCommand(gamepad, new NullCompanionService(), TestSpeedProfiles.Full());
        var axis = new Input("axis-0", "X", InputType.Axis, new ICommand[] { pan });
        axis.SelectedCommand = pan;
        axis.InputValue = 0.8f;
        await camera.WaitForPacketsAsync(1);
        await camera.CollectAsync();

        // The stick is still deflected: only the disconnect can stop the camera.
        driven.StopAll();
        var packets = await camera.CollectAsync();

        AssertStopsWithin(packets, IsPanTiltStop, ForceStopSpread);
        AssertStopsWithin(packets, IsZoomStop, ForceStopSpread);
        AssertStopsWithin(packets, IsFocusStop, ForceStopSpread);
    }

    [Fact]
    public async Task SwitchingAwayFromAPanningCamera_SendsItRepeatedStops()
    {
        using var camera1 = new LoopbackCamera();
        using var camera2 = new LoopbackCamera();
        var gamepad = new TestGamepad { SelectedCamera = camera1.Device };
        _ = new DrivenCameras(gamepad);
        var pan = new PanCommand(gamepad, new NullCompanionService(), TestSpeedProfiles.Full());
        var axis = new Input("axis-0", "X", InputType.Axis, new ICommand[] { pan });
        axis.SelectedCommand = pan;
        axis.InputValue = 0.8f;
        await camera1.WaitForPacketsAsync(1);
        await camera1.CollectAsync();

        // The stick is still deflected: only the switch can stop camera 1.
        gamepad.SelectedCamera = camera2.Device;
        var packets = await camera1.CollectAsync();

        AssertStopsWithin(packets, IsPanTiltStop, ForceStopSpread);
    }

    [Fact]
    public async Task AStop_SendsSpeedZeroForTheStoppedAxis_WhateverTheFraction()
    {
        using var camera = new LoopbackCamera();
        camera.Device.PanTilt(0.5, 0.5, PanDir.Left, TiltDir.Up);
        await camera.WaitForPacketsAsync(1);
        await camera.CollectAsync();

        camera.Device.Pan(0.7, PanDir.Stop);
        var packets = await camera.CollectAsync(SingleAxisStopWindow);

        AssertStopsWithin(packets, IsPanStopTiltUp, SingleAxisStopSpread);
        Assert.All(packets.Where(p => IsPanStopTiltUp(p.Payload)),
            p => Assert.Equal(new byte[] { 0x81, 0x01, 0x06, 0x01, 0, 10, (byte)PanDir.Stop, (byte)TiltDir.Up, 0xFF }, p.Payload));
    }
}
