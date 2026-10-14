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

/// <summary>
/// A ViscaIpDevice pointed at a loopback UDP receiver, and the packet checks the VISCA device tests
/// share. ViscaIpDevice sends through the static UdpSocket singleton, so the tests that send
/// through a device socket live in the two subclasses only: xUnit runs each one test at a time,
/// and the timing-bound one on its own after the parallel run, so the two never overlap either.
/// </summary>
public abstract class ViscaLoopbackTests
{
    protected const int MinStopSends = 3;

    // Long enough for the whole repeat window (~100 ms) plus scheduling jitter on a busy CI host.
    protected static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(900);

    // How long to collect after a single-axis stop. Deliberately looser than the spread bound
    // below so a slow start on CI cannot drop a packet; the spread assertion carries the timing.
    protected static readonly TimeSpan SingleAxisStopWindow = TimeSpan.FromMilliseconds(600);

    // Max time from an axis's first stop packet to its third. Nominally 2 x 50 ms plus timer
    // resolution (~15.6 ms per wait on Windows) and the 30 ms send throttle. Doubled for headroom:
    // the first socket test and loaded hosts (parallel builds, CI) measured up to ~250 ms.
    protected static readonly TimeSpan SingleAxisStopSpread = TimeSpan.FromMilliseconds(400);

    // Stop() queues all three axes behind the 30 ms send throttle, so each axis's stops
    // interleave with the others' and can spread further.
    protected static readonly TimeSpan ForceStopSpread = TimeSpan.FromMilliseconds(500);

    /// <summary>One VISCA command: a single 0xFF terminator, at the end, and a header that carries exactly one payload.</summary>
    protected static bool IsOneCommand(Packet p) =>
        p.Payload.Length > 0 &&
        p.Payload.Count(b => b == 0xFF) == 1 &&
        p.Payload[^1] == 0xFF &&
        p.HeaderPayloadLength == p.Payload.Length;

    /// <summary>Asserts at least 3 packets match, and the first 3 arrived within <paramref name="spread"/> of each other.</summary>
    protected static void AssertStopsWithin(List<Packet> packets, Func<byte[], bool> isStop, TimeSpan spread)
    {
        var stops = packets.Where(p => isStop(p.Payload)).ToList();
        Assert.True(stops.Count >= MinStopSends, Describe(packets));
        var actual = stops[MinStopSends - 1].ReceivedAt - stops[0].ReceivedAt;
        Assert.True(actual <= spread, $"Stops spread over {actual.TotalMilliseconds:F0} ms (max {spread.TotalMilliseconds:F0} ms). {Describe(packets)}");
    }

    protected static bool IsPanTilt(byte[] p) =>
        p.Length == 9 && p[0] == 0x81 && p[1] == 0x01 && p[2] == 0x06 && p[3] == 0x01;

    protected static bool IsPanTiltStop(byte[] p) =>
        IsPanTilt(p) && p[6] == (byte)PanDir.Stop && p[7] == (byte)TiltDir.Stop;

    protected static bool IsPanStopTiltUp(byte[] p) =>
        IsPanTilt(p) && p[6] == (byte)PanDir.Stop && p[7] == (byte)TiltDir.Up;

    protected static bool IsZoomStop(byte[] p) => p.SequenceEqual(new byte[] { 0x81, 0x01, 0x04, 0x07, 0x00, 0xFF });

    protected static bool IsFocusStop(byte[] p) => p.SequenceEqual(new byte[] { 0x81, 0x01, 0x04, 0x08, 0x00, 0xFF });

    protected static string Describe(IEnumerable<Packet> packets) =>
        "Received: " + string.Join(" | ", packets.Select(p => BitConverter.ToString(p.Payload)));

    protected sealed record Packet(byte[] Payload, TimeSpan ReceivedAt, int HeaderPayloadLength);

    /// <summary>A ViscaIpDevice pointed at a loopback UDP receiver that records VISCA payloads.</summary>
    protected sealed class LoopbackCamera : IDisposable
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

    protected sealed class NullCompanionService : IBitfocusCompanionService
    {
        public event EventHandler<BitfocusCompanionEvent>? ReceivedBitfocusCommand { add { } remove { } }
        public string? InboundListenerError => null;
        public event EventHandler? InboundListenerErrorChanged { add { } remove { } }
    }

    protected sealed class TestGamepad : IGamepad
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
