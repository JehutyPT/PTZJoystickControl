using System.Collections.Concurrent;
using System.Diagnostics;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Db;

// Cameras.json on the show PC was saved when each camera had a Companion page/button to press on
// selection (removed with the outbound Companion calls, #15). Those files must keep loading.
public sealed class CameraSettingsStoreRetiredKeysTests : IDisposable
{
    private readonly RecordingTraceListener _trace = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PtzCameraSettingsStoreRetiredKeysTests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public CameraSettingsStoreRetiredKeysTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "Cameras.json");
        Trace.Listeners.Add(_trace);
    }

    public void Dispose()
    {
        Trace.Listeners.Remove(_trace);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Camera(string name, string ip, int companionNumber, string bitfocusKeys)
    {
        var type = typeof(ViscaIpDevice).AssemblyQualifiedName;
        return $"{{\"IViscaDeveiceTypeAssemblyQualifiedName\":\"{type}\",\"IpAddress\":\"{ip}\",\"Name\":\"{name}\",\"Port\":52381,\"Protocol\":1,\"SendWaitTime\":45,\"UseHeader\":false,\"SingleCommand\":false,{bitfocusKeys}\"CompanionNumber\":{companionNumber}}}";
    }

    [Fact]
    public void CamerasJsonWithBitfocusPageAndButton_LoadsEveryOtherSetting()
    {
        var contents = "[" +
            Camera("Cam 1", "192.168.1.11", 1, "\"OnSelectedBitfocusButtonNumber\":5,\"OnSelectedBitfocusPageNumber\":2,") + "," +
            Camera("Cam 2", "192.168.1.12", 2, "\"OnSelectedBitfocusButtonNumber\":null,\"OnSelectedBitfocusPageNumber\":null,") +
            "]";
        File.WriteAllText(_path, contents);

        var cameras = new CameraSettingsStore(_path).GetAllCameras().Cast<ViscaIPDeviceBase>().ToList();

        Assert.Equal(new[] { "Cam 1", "Cam 2" }, cameras.Select(c => c.Name));
        Assert.Equal(new[] { "192.168.1.11", "192.168.1.12" }, cameras.Select(c => c.IPAddress));
        Assert.Equal(new int?[] { 1, 2 }, cameras.Select(c => c.CompanionNumber));
        Assert.All(cameras, c =>
        {
            Assert.Equal(52381, c.Port);
            Assert.Equal(Protocol.Udp, c.Protocol);
            Assert.Equal(45, c.SendWaitTime);
            Assert.False(c.UseHeader);
        });
        // Filtered to this file and these cameras: other test classes log through the same global Trace in parallel.
        Assert.DoesNotContain(_trace.Messages, m => m.Contains(_path) || m.Contains("Cam 1") || m.Contains("Cam 2"));
    }

    [Fact]
    public void LoadingCamerasJsonWithBitfocusPageAndButton_DoesNotRewriteTheFile()
    {
        var contents = "[" + Camera("Cam 1", "192.168.1.11", 1, "\"OnSelectedBitfocusButtonNumber\":5,\"OnSelectedBitfocusPageNumber\":2,") + "]";
        File.WriteAllText(_path, contents);

        _ = new CameraSettingsStore(_path).GetAllCameras().ToList();

        Assert.Equal(contents, File.ReadAllText(_path));
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public override void Write(string? message) => _messages.Enqueue(message ?? string.Empty);

        public override void WriteLine(string? message) => _messages.Enqueue(message ?? string.Empty);
    }
}
