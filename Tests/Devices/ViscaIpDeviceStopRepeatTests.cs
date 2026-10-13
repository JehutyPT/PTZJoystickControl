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
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// ViscaIpDevice sends through the static UdpSocket singleton, whose listener registry is not
// thread-safe. Keep every test that opens a device socket in this class so xUnit runs them
// sequentially.
public class ViscaIpDeviceStopRepeatTests
{
    private const int MinStopSends = 3;

    // Long enough for the whole repeat window (~100 ms) plus scheduling jitter on a busy CI host.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(600);

    // Upper bound for a single axis's repeated stops to all arrive (target ~100 ms).
    private static readonly TimeSpan SingleAxisStopWindow = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task PanTiltStop_IsSentAtLeastThreeTimes()
    {
        using var camera = new LoopbackCamera();
        camera.Device.PanTilt(10, 10, PanDir.Left, TiltDir.Up);
        await camera.WaitForPacketsAsync(1);

        camera.Device.PanTilt(10, 10, PanDir.Stop, TiltDir.Stop);
        var packets = await camera.CollectAsync(SingleAxisStopWindow);

        Assert.True(packets.Count(IsPanTiltStop) >= MinStopSends, Describe(packets));
    }

    [Fact]
    public async Task ZoomStop_IsSentAtLeastThreeTimes()
    {
        using var camera = new LoopbackCamera();
        camera.Device.Zoom(5, ZoomDir.Tele);
        await camera.WaitForPacketsAsync(1);

        camera.Device.Zoom(0, ZoomDir.Stop);
        var packets = await camera.CollectAsync(SingleAxisStopWindow);

        Assert.True(packets.Count(IsZoomStop) >= MinStopSends, Describe(packets));
    }

    [Fact]
    public async Task FocusStop_IsSentAtLeastThreeTimes()
    {
        using var camera = new LoopbackCamera();
        camera.Device.Focus(5, FocusDir.Far);
        await camera.WaitForPacketsAsync(1);

        camera.Device.Focus(0, FocusDir.Stop);
        var packets = await camera.CollectAsync(SingleAxisStopWindow);

        Assert.True(packets.Count(IsFocusStop) >= MinStopSends, Describe(packets));
    }

    [Fact]
    public async Task SpeedChangeWhileMoving_IsSentOnce()
    {
        using var camera = new LoopbackCamera();
        camera.Device.PanTilt(10, 10, PanDir.Left, TiltDir.Stop);
        await camera.WaitForPacketsAsync(1);
        await camera.CollectAsync();

        camera.Device.PanTilt(12, 10, PanDir.Left, TiltDir.Stop);
        var packets = await camera.CollectAsync();

        Assert.Single(packets);
        Assert.Equal(new byte[] { 0x81, 0x01, 0x06, 0x01, 12, 10, (byte)PanDir.Left, (byte)TiltDir.Stop, 0xFF }, packets[0]);
    }

    [Fact]
    public async Task RedeflectingWithinTheRepeatWindow_EndsWithTheMotionCommand()
    {
        using var camera = new LoopbackCamera();
        camera.Device.PanTilt(10, 10, PanDir.Left, TiltDir.Stop);
        await camera.WaitForPacketsAsync(1);

        camera.Device.PanTilt(10, 10, PanDir.Stop, TiltDir.Stop);
        await Task.Delay(10);
        camera.Device.PanTilt(8, 10, PanDir.Right, TiltDir.Stop);
        var packets = await camera.CollectAsync();

        Assert.Equal(new byte[] { 0x81, 0x01, 0x06, 0x01, 8, 10, (byte)PanDir.Right, (byte)TiltDir.Stop, 0xFF }, packets.Last());
    }

    [Fact]
    public async Task Stop_SendsRepeatedStopsForEveryAxis_EvenWhenAlreadyStopped()
    {
        using var camera = new LoopbackCamera();

        camera.Device.Stop();
        var packets = await camera.CollectAsync();

        Assert.True(packets.Count(IsPanTiltStop) >= MinStopSends, Describe(packets));
        Assert.True(packets.Count(IsZoomStop) >= MinStopSends, Describe(packets));
        Assert.True(packets.Count(IsFocusStop) >= MinStopSends, Describe(packets));
    }

    [Fact]
    public async Task Stop_StopsAMovingCamera()
    {
        using var camera = new LoopbackCamera();
        camera.Device.PanTilt(10, 10, PanDir.Left, TiltDir.Up);
        await camera.WaitForPacketsAsync(1);
        camera.Device.Zoom(5, ZoomDir.Wide);
        await camera.WaitForPacketsAsync(2);
        await camera.CollectAsync();

        camera.Device.Stop();
        var packets = await camera.CollectAsync();

        Assert.True(packets.Count(IsPanTiltStop) >= MinStopSends, Describe(packets));
        Assert.True(packets.Count(IsZoomStop) >= MinStopSends, Describe(packets));
        Assert.True(packets.Count(IsFocusStop) >= MinStopSends, Describe(packets));
        Assert.DoesNotContain(packets, p => !IsPanTiltStop(p) && !IsZoomStop(p) && !IsFocusStop(p));
    }

    [Fact]
    public async Task AxisReturningToDeadzone_SendsRepeatedPanStops()
    {
        using var camera = new LoopbackCamera();
        var gamepad = new TestGamepad { SelectedCamera = camera.Device };
        var pan = new PanCommand(gamepad, new NullCompanionService());
        var axis = new Input("axis-0", "X", InputType.Axis, new ICommand[] { pan });
        axis.SelectedCommand = pan;

        axis.InputValue = 0.8f;
        await camera.WaitForPacketsAsync(1);
        axis.InputValue = 0.01f;
        var packets = await camera.CollectAsync();

        Assert.True(packets.Count(IsPanTiltStop) >= MinStopSends, Describe(packets));
    }

    private static bool IsPanTiltStop(byte[] p) =>
        p.Length == 9 && p[0] == 0x81 && p[1] == 0x01 && p[2] == 0x06 && p[3] == 0x01
        && p[6] == (byte)PanDir.Stop && p[7] == (byte)TiltDir.Stop;

    private static bool IsZoomStop(byte[] p) => p.SequenceEqual(new byte[] { 0x81, 0x01, 0x04, 0x07, 0x00, 0xFF });

    private static bool IsFocusStop(byte[] p) => p.SequenceEqual(new byte[] { 0x81, 0x01, 0x04, 0x08, 0x00, 0xFF });

    private static string Describe(IEnumerable<byte[]> packets) =>
        "Received: " + string.Join(" | ", packets.Select(p => BitConverter.ToString(p)));

    /// <summary>A ViscaIpDevice pointed at a loopback UDP receiver that records VISCA payloads.</summary>
    private sealed class LoopbackCamera : IDisposable
    {
        private readonly UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly List<byte[]> received = new();
        private readonly CancellationTokenSource cts = new();
        private readonly Task receiveLoop;
        private int consumed;

        public LoopbackCamera()
        {
            var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
            Device = new ViscaIpDevice("Test camera", new IPEndPoint(IPAddress.Loopback, port));
            // The first send after construction only opens the socket; open it up front.
            Device.BeginSocket();
            receiveLoop = Task.Run(ReceiveLoop);
        }

        public ViscaIpDevice Device { get; }

        private async Task ReceiveLoop()
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var result = await receiver.ReceiveAsync(cts.Token);
                    // Strip the 8-byte VISCA-over-IP header.
                    var payload = result.Buffer.Skip(8).ToArray();
                    lock (received) received.Add(payload);
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
        public async Task<List<byte[]>> CollectAsync(TimeSpan? window = null)
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
            Device.EndSocket();
            cts.Cancel();
            receiver.Dispose();
            try { receiveLoop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }
    }

    private sealed class NullCompanionService : IBitfocusCompanionService
    {
        public event EventHandler<BitfocusCompanionEvent>? ReceivedBitfocusCommand { add { } remove { } }
        public void PressButton(int? pageNumber, int? buttonNumber) { }
        public void SetCustomVariable(string variableName, string value) { }
        public List<BitfocusCompanionEvent> SupportedEvents => new();
    }

    private sealed class TestGamepad : IGamepad
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public string Id => "test-gamepad";
        public string Name => "Test gamepad";
        public bool IsActivated { get; set; } = true;
        public bool IsConnected { get; set; } = true;
        public ObservableCollection<ViscaDeviceBase>? Cameras { get; set; }
        public IReadOnlyList<ICommand> Commands => Array.Empty<ICommand>();
        public bool DetectInput { get; set; }
        public IEnumerable<IInput> Inputs => Array.Empty<IInput>();
        public ViscaDeviceBase? SelectedCamera { get; set; }

        public void Acquire() { }
        public void Unacquire() { }
        public void Update() { }
    }
}
