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
    // Loaded cameras listen for UDP replies on the shared socket until closed. Each test loads Cam 1
    // on the same address, and a camera still listening there would make the next one warn.
    private readonly List<ICamera> _loaded = new();

    public CameraSettingsStoreRetiredKeysTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "Cameras.json");
        Trace.Listeners.Add(_trace);
    }

    public void Dispose()
    {
        foreach (var camera in _loaded.OfType<IDisposable>())
            camera.Dispose();
        Trace.Listeners.Remove(_trace);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private List<ICamera> Load(CameraSettingsStore store)
    {
        var cameras = store.GetAllCameras().ToList();
        _loaded.AddRange(cameras);
        return cameras;
    }

    // Other test classes log through the same global Trace in parallel, and many name a camera "Cam 1",
    // so a message is this test's when it names this file or a camera address only this class uses.
    private bool IsAbout(string message, string ipAddress) => message.Contains(_path) || message.Contains(ipAddress);

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

        var cameras = Load(new CameraSettingsStore(_path)).Cast<ViscaIPDeviceBase>().ToList();

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
        Assert.DoesNotContain(_trace.Messages, m => IsAbout(m, "192.168.1.11") || IsAbout(m, "192.168.1.12"));
    }

    [Fact]
    public void LoadingCamerasJsonWithBitfocusPageAndButton_DoesNotRewriteTheFile()
    {
        var contents = "[" + Camera("Cam 1", "192.168.1.11", 1, "\"OnSelectedBitfocusButtonNumber\":5,\"OnSelectedBitfocusPageNumber\":2,") + "]";
        File.WriteAllText(_path, contents);

        _ = Load(new CameraSettingsStore(_path));

        Assert.Equal(contents, File.ReadAllText(_path));
    }

    // Files saved before #50 also hold SingleCommand (an unused option to pack several VISCA commands per packet).
    [Fact]
    public void CamerasJsonWithSingleCommand_LoadsEveryOtherSetting_AndDropsTheKeyOnSave()
    {
        var contents = "[" + Camera("Cam 1", "192.168.1.11", 1, "") + "]";
        Assert.Contains("\"SingleCommand\"", contents);
        File.WriteAllText(_path, contents);
        var store = new CameraSettingsStore(_path);

        var camera = Assert.IsAssignableFrom<ViscaIPDeviceBase>(Assert.Single(Load(store)));

        Assert.Equal("Cam 1", camera.Name);
        Assert.Equal("192.168.1.11", camera.IPAddress);
        Assert.Equal(1, camera.CompanionNumber);
        Assert.Equal(52381, camera.Port);
        Assert.Equal(Protocol.Udp, camera.Protocol);
        Assert.Equal(45, camera.SendWaitTime);
        Assert.False(camera.UseHeader);
        Assert.DoesNotContain(_trace.Messages, m => IsAbout(m, "192.168.1.11"));

        Assert.True(store.SaveCameras(new ViscaDeviceBase[] { camera }));
        Assert.DoesNotContain("SingleCommand", File.ReadAllText(_path));
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public override void Write(string? message) => _messages.Enqueue(message ?? string.Empty);

        public override void WriteLine(string? message) => _messages.Enqueue(message ?? string.Empty);
    }
}
