using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Db;

// Cameras.json on the show PC was saved before the camera abstraction (#44). It must load into the
// same cameras and save back the same keys and values, with the VISCA camera's type name unchanged.
public sealed class CameraSettingsStoreFormatTests : IDisposable
{
    private const string TypeNameKey = "IViscaDeveiceTypeAssemblyQualifiedName";

    // Full name and assembly of the VISCA camera's saved type; the assembly version is whatever the build has.
    private const string ViscaCameraTypeName = "PtzJoystickControl.Application.Devices.ViscaIpDevice, Application,";

    // Written by the app at 208d83f, the commit before the camera abstraction.
    private const string CamerasJsonBeforeTheAbstraction =
        "[{\"IViscaDeveiceTypeAssemblyQualifiedName\":\"PtzJoystickControl.Application.Devices.ViscaIpDevice, Application, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\"," +
        "\"IpAddress\":\"192.168.1.21\",\"Name\":\"Cam 1\",\"Port\":52381,\"Protocol\":1,\"SendWaitTime\":45,\"UseHeader\":false,\"CompanionNumber\":1}," +
        "{\"IViscaDeveiceTypeAssemblyQualifiedName\":\"PtzJoystickControl.Application.Devices.ViscaIpDevice, Application, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\"," +
        "\"IpAddress\":\"127.0.0.1\",\"Name\":\"Cam 2\",\"Port\":52382,\"Protocol\":0,\"SendWaitTime\":30,\"UseHeader\":true,\"CompanionNumber\":3}]";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PtzCameraSettingsStoreFormatTests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public CameraSettingsStoreFormatTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "Cameras.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void CamerasJsonSavedBeforeTheCameraAbstraction_LoadsTheSameCameras_AndSavesTheSameKeysAndValues()
    {
        File.WriteAllText(_path, CamerasJsonBeforeTheAbstraction);
        var store = new CameraSettingsStore(_path);

        var cameras = store.GetAllCameras().ToList();

        Assert.Collection(cameras,
            camera =>
            {
                var visca = Assert.IsType<ViscaIpDevice>(camera);
                Assert.Equal("Cam 1", visca.Name);
                Assert.Equal("192.168.1.21", visca.IPAddress);
                Assert.Equal(52381, visca.Port);
                Assert.Equal(Protocol.Udp, visca.Protocol);
                Assert.Equal(45, visca.SendWaitTime);
                Assert.False(visca.UseHeader);
                Assert.Equal(1, visca.CompanionNumber);
            },
            camera =>
            {
                var visca = Assert.IsType<ViscaIpDevice>(camera);
                Assert.Equal("Cam 2", visca.Name);
                Assert.Equal("127.0.0.1", visca.IPAddress);
                Assert.Equal(52382, visca.Port);
                Assert.Equal(Protocol.Tcp, visca.Protocol);
                Assert.Equal(30, visca.SendWaitTime);
                Assert.True(visca.UseHeader);
                Assert.Equal(3, visca.CompanionNumber);
            });

        Assert.True(store.SaveCameras(cameras));
        AssertSameKeysAndValues(CamerasJsonBeforeTheAbstraction, File.ReadAllText(_path));
    }

    // Cameras are created from the saved type name with a (string name) constructor, so a camera
    // type other than VISCA (#47) loads the same way.
    [Fact]
    public void CameraOfAnotherCameraType_IsCreatedFromItsSavedTypeName()
    {
        File.WriteAllText(_path,
            $"[{{\"{TypeNameKey}\":\"{typeof(NonViscaCamera).AssemblyQualifiedName}\",\"Name\":\"Other\",\"CompanionNumber\":4}}]");

        var camera = Assert.Single(new CameraSettingsStore(_path).GetAllCameras());

        var other = Assert.IsType<NonViscaCamera>(camera);
        Assert.Equal("Other", other.Name);
        Assert.Equal(4, other.CompanionNumber);
    }

    // An XC camera (#47) is saved with the same keys as every other entry, its address and port under
    // the VISCA connection keys, and loads back as an XC camera.
    [Fact]
    public void XcAndViscaCameras_SaveAndLoadBack_WithTheViscaEntriesUnchanged()
    {
        // The XC camera's requests go to this fake, not out to the network.
        using var fake = new FakeXcCamera();
        File.WriteAllText(_path, CamerasJsonBeforeTheAbstraction);
        var store = new CameraSettingsStore(_path);
        var visca = store.GetAllCameras().ToList();
        using var addressed = new XcCamera("XC 1") { CompanionNumber = 2, Port = fake.Port, Address = "127.0.0.1" };
        using var unaddressed = new XcCamera("XC 2") { CompanionNumber = 4 };

        Assert.True(store.SaveCameras(visca.Concat(new ICamera[] { addressed, unaddressed })));
        var saved = Entries(File.ReadAllText(_path));
        var loaded = new CameraSettingsStore(_path).GetAllCameras().ToList();
        try
        {
            AssertSameKeysAndValues(Entries(CamerasJsonBeforeTheAbstraction), saved.Take(2).ToList());
            Assert.All(saved.Skip(2), entry =>
            {
                Assert.Equal(saved[0].Keys.Order(), entry.Keys.Order());
                Assert.Equal(typeof(XcCamera).AssemblyQualifiedName, entry[TypeNameKey].GetString());
            });

            Assert.Equal(4, loaded.Count);
            Assert.Equal(new[] { "Cam 1", "Cam 2" }, loaded.Take(2).Select(c => Assert.IsType<ViscaIpDevice>(c).Name));
            var first = Assert.IsType<XcCamera>(loaded[2]);
            Assert.Equal(("XC 1", (int?)2, "127.0.0.1", fake.Port), (first.Name, first.CompanionNumber, first.Address, first.Port));
            var second = Assert.IsType<XcCamera>(loaded[3]);
            Assert.Equal(("XC 2", (int?)4, "", XcCameraBase.DefaultPort), (second.Name, second.CompanionNumber, second.Address, second.Port));
        }
        finally
        {
            foreach (var camera in loaded.OfType<IDisposable>())
                camera.Dispose();
        }
    }

    // A camera type from a newer build, an empty entry or a value the camera rejects must not lose the
    // other cameras: the app would otherwise start with none.
    [Fact]
    public void AnEntryThatCannotBeLoaded_IsSkippedWithAWarningNamingIt_AndTheOthersLoad()
    {
        using var trace = new TraceRecorder();
        var visca = JsonDocument.Parse(CamerasJsonBeforeTheAbstraction).RootElement.EnumerateArray().Select(e => e.GetRawText()).ToList();
        var unknownType = $"{{\"{TypeNameKey}\":\"PtzJoystickControl.Application.Devices.FutureCamera, Application\",\"Name\":\"Future cam\",\"CompanionNumber\":5}}";
        var rejectedAddress = $"{{\"{TypeNameKey}\":\"{typeof(XcCamera).AssemblyQualifiedName}\",\"IpAddress\":\"not an address\",\"Name\":\"Bad XC\",\"Port\":80,\"CompanionNumber\":6}}";
        File.WriteAllText(_path, $"[{visca[0]},{unknownType},null,{rejectedAddress},{visca[1]}]");

        var loaded = new CameraSettingsStore(_path).GetAllCameras().ToList();

        Assert.Equal(new[] { "Cam 1", "Cam 2" }, loaded.Select(c => c.Name));
        // Filtered to this file: other test classes log through the same global Trace in parallel.
        var warnings = trace.Events.Where(e => e.Type == TraceEventType.Warning && e.Message.Contains(_path)).Select(e => e.Message).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, m => m.Contains("Future cam") && m.Contains("FutureCamera"));
        Assert.Contains(warnings, m => m.Contains("Bad XC"));
        Assert.DoesNotContain(trace.Events, e => e.Type == TraceEventType.Error && e.Message.Contains(_path));
    }

    // Valid JSON whose entry does not fit the format: a value of the wrong type, or no object at all.
    [Fact]
    public void AnEntryThatDoesNotFitTheFormat_IsSkippedWithAWarningNamingIt_AndTheOthersLoad()
    {
        using var trace = new TraceRecorder();
        var visca = JsonDocument.Parse(CamerasJsonBeforeTheAbstraction).RootElement.EnumerateArray().Select(e => e.GetRawText()).ToList();
        var textPort = $"{{\"{TypeNameKey}\":\"{typeof(XcCamera).AssemblyQualifiedName}\",\"IpAddress\":\"\",\"Name\":\"Text port\",\"Port\":\"80\",\"CompanionNumber\":6}}";
        File.WriteAllText(_path, $"[{visca[0]},{textPort},5,{visca[1]}]");

        var loaded = new CameraSettingsStore(_path).GetAllCameras().ToList();

        Assert.Equal(new[] { "Cam 1", "Cam 2" }, loaded.Select(c => c.Name));
        var warnings = trace.Events.Where(e => e.Type == TraceEventType.Warning && e.Message.Contains(_path)).Select(e => e.Message).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, m => m.Contains("Text port") && m.Contains("entry 2"));
        Assert.Contains(warnings, m => m.Contains("entry 3"));
        Assert.DoesNotContain(trace.Events, e => e.Type == TraceEventType.Error && e.Message.Contains(_path));
    }

    private static void AssertSameKeysAndValues(string expectedJson, string actualJson) =>
        AssertSameKeysAndValues(Entries(expectedJson), Entries(actualJson));

    private static void AssertSameKeysAndValues(List<Dictionary<string, JsonElement>> expected, List<Dictionary<string, JsonElement>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Keys.Order(), actual[i].Keys.Order());
            Assert.StartsWith(ViscaCameraTypeName, actual[i][TypeNameKey].GetString());
            foreach (var key in expected[i].Keys.Where(k => k != TypeNameKey))
                Assert.Equal(expected[i][key].GetRawText(), actual[i][key].GetRawText());
        }
    }

    private static List<Dictionary<string, JsonElement>> Entries(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray()
            .Select(entry => entry.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()))
            .ToList();

    // A camera of another camera type: it implements only the camera abstraction, not the VISCA base type.
    private sealed class NonViscaCamera : ICamera
    {
        public NonViscaCamera(string name) => Name = name;

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public string Name { get; }
        public int? CompanionNumber { get; set; }
        public bool Connected => true;
        public bool IsMoving => false;

        public void Pan(double panSpeed, PanDir panDir) { }
        public void Tilt(double tiltSpeed, TiltDir tiltDir) { }
        public void Zoom(double zoomSpeed, ZoomDir zoomDir) { }
        public void Focus(double focusSpeed, FocusDir focusDir) { }
        public void Stop() { }
        public void FocusMode(FocusMode focusMode) { }
        public void FocusLock(FocusLock focusLock) { }
        public void Power(Power power) { }
        public void Preset(Preset preset, byte presetNumber) { }
        public void PresetRecallSpeed(byte speed) { }
    }
}
