using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Utils;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// Hammers the static UdpSocket listener registry from many threads. Run on its own, so it cannot
// slow the timing-bound tests (stop repeats, the panel link) running in parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UdpListenerRegistryCollection
{
    public const string Name = "UDP listener registry";
}

// Each VISCA-over-UDP camera registers for the replies from its IP:port on the one shared UDP
// socket. Cameras.json sets the address, then the port, on a camera that is already listening, so
// every load moves a camera's listener at least once. Addresses are from the documentation ranges
// (192.0.2.0/24, 198.51.100.0/24): opening a UDP listener sends nothing.
[Collection(UdpListenerRegistryCollection.Name)]
public sealed class UdpSocketListenerTests : IDisposable
{
    private readonly TraceRecorder _trace = new();
    private readonly List<ViscaIpDevice> _devices = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PtzUdpSocketListenerTests", Guid.NewGuid().ToString("N"));

    public UdpSocketListenerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var device in _devices)
            device.Dispose();
        _trace.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ViscaIpDevice Device(string name, IPEndPoint? endpoint = null)
    {
        var device = new ViscaIpDevice(name, endpoint);
        lock (_devices) _devices.Add(device);
        return device;
    }

    private static IEnumerable<EndPoint> ListenersOn(string address) =>
        UdpSocket.ListenerEndpoints().Where(e => e is IPEndPoint ip && ip.Address.ToString() == address);

    private static object? ListenerOwner(string address, int port) =>
        UdpSocket.ListenerFor(new IPEndPoint(IPAddress.Parse(address), port))?.Target;

    [Fact]
    public void ACameraMovedToANewAddressAndPort_ListensOnlyOnTheNewOne()
    {
        var camera = Device("Moved");

        camera.IPAddress = "192.0.2.10";
        camera.Port = 52381;
        camera.Port = 52382;

        Assert.Same(camera, ListenerOwner("192.0.2.10", 52382));
        Assert.Equal(new EndPoint[] { new IPEndPoint(IPAddress.Parse("192.0.2.10"), 52382) }, ListenersOn("192.0.2.10"));
    }

    [Fact]
    public void ClosingACameraThatWasMoved_LeavesNoListenerBehind()
    {
        var camera = Device("Moved");
        camera.IPAddress = "192.0.2.11";
        camera.Port = 52381;
        camera.IPAddress = "192.0.2.12";

        camera.Dispose();

        Assert.Empty(ListenersOn("192.0.2.11"));
        Assert.Empty(ListenersOn("192.0.2.12"));
    }

    // Two cameras configured on one IP:port cannot tell the replies apart. The first keeps them; the
    // second still sends its commands, and opening it neither throws nor takes the first's replies.
    [Fact]
    public void ASecondCameraOnTheSameEndpoint_DoesNotThrow_TheFirstKeepsItsReplies_AndAWarningNamesBoth()
    {
        var first = Device("First");
        first.IPAddress = "192.0.2.20";
        first.Port = 52381;
        var second = Device("Second", new IPEndPoint(IPAddress.Parse("192.0.2.20"), 52381));

        second.BeginSocket();

        Assert.Same(first, ListenerOwner("192.0.2.20", 52381));
        Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Warning
            && e.Message.Contains("Second") && e.Message.Contains("192.0.2.20:52381"));
    }

    [Fact]
    public void ClosingTheSecondCameraOnAnEndpoint_LeavesTheFirstListening()
    {
        var first = Device("First");
        first.IPAddress = "192.0.2.21";
        first.Port = 52381;
        var second = Device("Second");
        second.IPAddress = "192.0.2.21";
        second.Port = 52381;

        second.Dispose();

        Assert.Same(first, ListenerOwner("192.0.2.21", 52381));
    }

    [Fact]
    public void TwoCamerasOnOneEndpointInCamerasJson_BothLoad_AndTheFirstKeepsTheReplies()
    {
        var path = WriteCamerasJson(("Cam A", "192.0.2.30"), ("Cam B", "192.0.2.30"));

        var cameras = Load(path);

        Assert.Equal(new[] { "Cam A", "Cam B" }, cameras.Select(c => c.Name));
        Assert.Same(cameras[0], ListenerOwner("192.0.2.30", 52381));
    }

    // The failure seen in the test suite: loads on several threads at once (xUnit runs test classes in
    // parallel; on the show PC, a reload while a camera reconnects) must each load every camera.
    [Fact]
    public void ConcurrentLoads_EachLoadEveryCamera_AndLeaveNoListenerBehindOnceClosed()
    {
        var path = WriteCamerasJson(("Cam A", "198.51.100.1"), ("Cam B", "198.51.100.2"));
        var failures = new ConcurrentQueue<string>();

        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            var cameras = Load(path);
            if (cameras.Count != 2)
                failures.Enqueue($"loaded {cameras.Count} cameras");
            foreach (var camera in cameras)
                camera.Dispose();
        });

        Assert.True(failures.IsEmpty, string.Join("; ", failures.Distinct())
            + " | " + string.Join(" | ", _trace.Messages.Where(m => m.Contains(path)).Distinct().Take(3)));
        Assert.Empty(ListenersOn("198.51.100.1"));
        Assert.Empty(ListenersOn("198.51.100.2"));
    }

    [Fact]
    public void CamerasMovedOnManyThreadsAtOnce_NeverThrow_AndLeaveNoListenerBehindOnceClosed()
    {
        var failures = new ConcurrentQueue<Exception>();

        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            try
            {
                using var camera = new ViscaIpDevice($"Cam {i}");
                camera.IPAddress = $"198.51.100.{10 + i % 20}";
                camera.Port = 52381;
                camera.Port = 52382;
            }
            catch (Exception e)
            {
                failures.Enqueue(e);
            }
        });

        Assert.True(failures.IsEmpty, string.Join(" | ", failures.Select(e => $"{e.GetType().Name}: {e.Message}").Distinct()));
        for (var i = 10; i < 30; i++)
            Assert.Empty(ListenersOn($"198.51.100.{i}"));
    }

    private string WriteCamerasJson(params (string Name, string Ip)[] cameras)
    {
        var type = typeof(ViscaIpDevice).AssemblyQualifiedName;
        var path = Path.Combine(_dir, "Cameras.json");
        File.WriteAllText(path, "[" + string.Join(",", cameras.Select(c =>
            $"{{\"IViscaDeveiceTypeAssemblyQualifiedName\":\"{type}\",\"IpAddress\":\"{c.Ip}\",\"Name\":\"{c.Name}\",\"Port\":52381,\"Protocol\":1,\"SendWaitTime\":45,\"UseHeader\":false}}")) + "]");
        return path;
    }

    private List<ViscaIpDevice> Load(string path)
    {
        var cameras = new CameraSettingsStore(path).GetAllCameras().Cast<ViscaIpDevice>().ToList();
        lock (_devices) _devices.AddRange(cameras);
        return cameras;
    }
}
