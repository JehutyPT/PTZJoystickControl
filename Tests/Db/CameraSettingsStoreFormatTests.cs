using System.ComponentModel;
using System.Text.Json;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
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

    private static void AssertSameKeysAndValues(string expectedJson, string actualJson)
    {
        var expected = Entries(expectedJson);
        var actual = Entries(actualJson);

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
