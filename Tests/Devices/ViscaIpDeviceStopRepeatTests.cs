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
using PtzJoystickControl.Tests.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// ViscaIpDevice sends through the static UdpSocket singleton, whose listener registry is not
// thread-safe. Keep every test that opens a device socket in this class so xUnit runs them
// sequentially.
public class ViscaIpDeviceStopRepeatTests
{
    private const int MinStopSends = 3;

    // Long enough for the whole repeat window (~100 ms) plus scheduling jitter on a busy CI host.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(900);

    // How long to collect after a single-axis stop. Deliberately looser than the spread bound
    // below so a slow start on CI cannot drop a packet; the spread assertion carries the timing.
    private static readonly TimeSpan SingleAxisStopWindow = TimeSpan.FromMilliseconds(600);

    // Max time from an axis's first stop packet to its third. Nominally 2 x 50 ms plus timer
    // resolution (~15.6 ms per wait on Windows) and the 30 ms send throttle. Doubled for headroom:
    // the first socket test and loaded hosts (parallel builds, CI) measured up to ~250 ms.
    private static readonly TimeSpan SingleAxisStopSpread = TimeSpan.FromMilliseconds(400);

    // Stop() queues all three axes behind the 30 ms send throttle, so each axis's stops
    // interleave with the others' and can spread further.
    private static readonly TimeSpan ForceStopSpread = TimeSpan.FromMilliseconds(500);

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
    public async Task Dispose_CancelsPendingRepeatsWithoutReopeningTheSocket()
    {
        var camera = new LoopbackCamera();
        var endpoint = camera.Endpoint;
        camera.Device.Stop();
        camera.Device.Dispose();
        var packets = await camera.CollectAsync();
        camera.Dispose();

        // Only the initial, immediate stops can have gone out; repeats are cancelled.
        Assert.True(packets.Count(p => IsPanTiltStop(p.Payload)) < MinStopSends, Describe(packets));
        Assert.True(packets.Count(p => IsZoomStop(p.Payload)) < MinStopSends, Describe(packets));
        Assert.True(packets.Count(p => IsFocusStop(p.Payload)) < MinStopSends, Describe(packets));
        Assert.False(camera.Device.Connected);
        // A repeat that reopened the socket would have re-registered this endpoint's listener,
        // and registering another device on it would throw.
        using var successor = new ViscaIpDevice("Successor", endpoint);
        successor.BeginSocket();
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

    /// <summary>One VISCA command: a single 0xFF terminator, at the end, and a header that carries exactly one payload.</summary>
    private static bool IsOneCommand(Packet p) =>
        p.Payload.Length > 0 &&
        p.Payload.Count(b => b == 0xFF) == 1 &&
        p.Payload[^1] == 0xFF &&
        p.HeaderPayloadLength == p.Payload.Length;

    /// <summary>Asserts at least 3 packets match, and the first 3 arrived within <paramref name="spread"/> of each other.</summary>
    private static void AssertStopsWithin(List<Packet> packets, Func<byte[], bool> isStop, TimeSpan spread)
    {
        var stops = packets.Where(p => isStop(p.Payload)).ToList();
        Assert.True(stops.Count >= MinStopSends, Describe(packets));
        var actual = stops[MinStopSends - 1].ReceivedAt - stops[0].ReceivedAt;
        Assert.True(actual <= spread, $"Stops spread over {actual.TotalMilliseconds:F0} ms (max {spread.TotalMilliseconds:F0} ms). {Describe(packets)}");
    }

    private static bool IsPanTilt(byte[] p) =>
        p.Length == 9 && p[0] == 0x81 && p[1] == 0x01 && p[2] == 0x06 && p[3] == 0x01;

    private static bool IsPanTiltStop(byte[] p) =>
        IsPanTilt(p) && p[6] == (byte)PanDir.Stop && p[7] == (byte)TiltDir.Stop;

    private static bool IsPanStopTiltUp(byte[] p) =>
        IsPanTilt(p) && p[6] == (byte)PanDir.Stop && p[7] == (byte)TiltDir.Up;

    private static bool IsZoomStop(byte[] p) => p.SequenceEqual(new byte[] { 0x81, 0x01, 0x04, 0x07, 0x00, 0xFF });

    private static bool IsFocusStop(byte[] p) => p.SequenceEqual(new byte[] { 0x81, 0x01, 0x04, 0x08, 0x00, 0xFF });

    private static string Describe(IEnumerable<Packet> packets) =>
        "Received: " + string.Join(" | ", packets.Select(p => BitConverter.ToString(p.Payload)));

    private sealed record Packet(byte[] Payload, TimeSpan ReceivedAt, int HeaderPayloadLength);

    /// <summary>A ViscaIpDevice pointed at a loopback UDP receiver that records VISCA payloads.</summary>
    private sealed class LoopbackCamera : IDisposable
    {
        private readonly UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly List<Packet> received = new();
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly CancellationTokenSource cts = new();
        private readonly Task receiveLoop;
        private int consumed;

        public LoopbackCamera()
        {
            var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
            Endpoint = new IPEndPoint(IPAddress.Loopback, port);
            Device = new ViscaIpDevice("Test camera", new IPEndPoint(IPAddress.Loopback, port));
            // The first send after construction only opens the socket; open it up front.
            Device.BeginSocket();
            receiveLoop = Task.Run(ReceiveLoop);
        }

        public ViscaIpDevice Device { get; }

        public IPEndPoint Endpoint { get; }

        private async Task ReceiveLoop()
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var result = await receiver.ReceiveAsync(cts.Token);
                    // Strip the 8-byte VISCA-over-IP header.
                    var payload = result.Buffer.Skip(8).ToArray();
                    // Header bytes 2-3: payload length, big-endian.
                    var headerPayloadLength = result.Buffer.Length >= 4 ? (result.Buffer[2] << 8) | result.Buffer[3] : -1;
                    lock (received) received.Add(new Packet(payload, clock.Elapsed, headerPayloadLength));
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        public async Task WaitForPacketsAsync(int totalCount)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                lock (received) if (received.Count >= totalCount) return;
                await Task.Delay(5);
            }
            throw new TimeoutException($"Expected {totalCount} packets.");
        }

        /// <summary>Waits for the window (default: the full repeat window) and returns packets received since the last call.</summary>
        public async Task<List<Packet>> CollectAsync(TimeSpan? window = null)
        {
            await Task.Delay(window ?? SettleTime);
            lock (received)
            {
                var result = received.Skip(consumed).ToList();
                consumed = received.Count;
                return result;
            }
        }

        public void Dispose()
        {
            // Cancels the device's pending stop repeats, so none outlive this test and touch the
            // static UdpSocket listener registry while the next test runs.
            Device.Dispose();
            cts.Cancel();
            receiver.Dispose();
            try { receiveLoop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }
    }

    private sealed class NullCompanionService : IBitfocusCompanionService
    {
        public event EventHandler<BitfocusCompanionEvent>? ReceivedBitfocusCommand { add { } remove { } }
        public string? InboundListenerError => null;
        public event EventHandler? InboundListenerErrorChanged { add { } remove { } }
    }

    private sealed class TestGamepad : IGamepad
    {
        private ICamera? selectedCamera;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public string Id => "test-gamepad";
        public string Name => "Test gamepad";
        public bool IsActivated { get; set; } = true;
        public bool IsConnected { get; set; } = true;
        public ObservableCollection<ICamera>? Cameras { get; set; }
        public IReadOnlyList<ICommand> Commands => Array.Empty<ICommand>();
        public bool DetectInput { get; set; }
        public IEnumerable<IInput> Inputs => Array.Empty<IInput>();
        public ICamera? SelectedCamera
        {
            get => selectedCamera;
            set
            {
                selectedCamera = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCamera)));
            }
        }

        public void Acquire() { }
        public void Unacquire() { }
        public void Update() { }
    }
}
