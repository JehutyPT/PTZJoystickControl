using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Services;

// Each camera's Companion number is what ?SelectCamera=N (and the legacy ?SetCameraPreview=N) selects.
public sealed class CompanionNumberTests : IDisposable
{
    private readonly RecordingTraceListener _trace = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PtzCompanionNumberTests", Guid.NewGuid().ToString("N"));

    public CompanionNumberTests()
    {
        Directory.CreateDirectory(_dir);
        Trace.Listeners.Add(_trace);
    }

    public void Dispose()
    {
        Trace.Listeners.Remove(_trace);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ViscaIpDevice Cam(string name, int? number = null) => new(name) { CompanionNumber = number };

    private static int?[] Numbers(CamerasService service) => service.Cameras.Select(c => c.CompanionNumber).ToArray();

    [Fact]
    public void CamerasSavedWithoutNumbers_GetTheirListPosition_AndAreSaved()
    {
        var store = new MemoryStore(Cam("A"), Cam("B"));

        var service = new CamerasService(store);

        Assert.Equal(new int?[] { 1, 2 }, Numbers(service));
        Assert.Equal(new int?[] { 1, 2 }, store.Saved.Select(c => c.CompanionNumber).ToArray());
    }

    [Fact]
    public void MissingNumber_TakesTheLowestUnusedNumber_WhenItsPositionIsTakenByALaterCamera()
    {
        var service = new CamerasService(new MemoryStore(Cam("A"), Cam("B", 1)));

        Assert.Equal(new int?[] { 2, 1 }, Numbers(service));
    }

    [Fact]
    public void StoredNumbers_AreKept()
    {
        var service = new CamerasService(new MemoryStore(Cam("A", 7), Cam("B", 3)));

        Assert.Equal(new int?[] { 7, 3 }, Numbers(service));
    }

    [Fact]
    public void AddedCamera_GetsItsListPosition_WhenFree()
    {
        var service = new CamerasService(new MemoryStore(Cam("A"), Cam("B")));

        service.AddCamera(Cam("C"));

        Assert.Equal(new int?[] { 1, 2, 3 }, Numbers(service));
    }

    [Fact]
    public void AddedCamera_GetsTheLowestUnusedNumber_WhenItsPositionIsTaken()
    {
        var service = new CamerasService(new MemoryStore(Cam("A", 2), Cam("B", 3)));

        service.AddCamera(Cam("C"));

        Assert.Equal(new int?[] { 2, 3, 1 }, Numbers(service));
    }

    [Fact]
    public void AddedCameraWithANumber_KeepsIt()
    {
        var service = new CamerasService(new MemoryStore(Cam("A")));

        service.AddCamera(Cam("B", 9));

        Assert.Equal(new int?[] { 1, 9 }, Numbers(service));
    }

    [Fact]
    public void AddedCameraOfAnotherCameraType_GetsANumber_AndItsNumberChangeIsSaved()
    {
        var store = new MemoryStore(Cam("A"));
        var service = new CamerasService(store);
        var other = new NonViscaCamera("Other");

        service.AddCamera(other);
        Assert.Equal(new int?[] { 1, 2 }, Numbers(service));

        other.CompanionNumber = 5;

        Assert.Equal(new int?[] { 1, 5 }, store.SavedNumbers);
    }

    [Fact]
    public void DuplicateStoredNumbers_AreKept_AndLoggedAsAWarning()
    {
        var service = new CamerasService(new MemoryStore(Cam("Left", 1), Cam("Right", 1)));

        Assert.Equal(new int?[] { 1, 1 }, Numbers(service));
        Assert.Contains(_trace.Messages, m => m.Contains("Companion number 1") && m.Contains("Left") && m.Contains("Right"));
    }

    [Fact]
    public void UsedByAnotherCamera_FindsTheOtherCamera_ButNotTheCameraItself()
    {
        var a = Cam("A", 1);
        var b = Cam("B", 2);
        var cameras = new ICamera[] { a, b };

        Assert.Same(b, CompanionNumbers.UsedByAnotherCamera(cameras, a, 2));
        Assert.Null(CompanionNumbers.UsedByAnotherCamera(cameras, a, 1));
        Assert.Null(CompanionNumbers.UsedByAnotherCamera(cameras, a, 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANumberBelowOne_IsRejectedByTheCamera(int number)
    {
        var camera = Cam("A", 1);

        Assert.ThrowsAny<ArgumentException>(() => camera.CompanionNumber = number);
        Assert.Equal(1, camera.CompanionNumber);
    }

    [Fact]
    public void CompanionNumber_RoundTripsThroughCamerasJson()
    {
        var path = Path.Combine(_dir, "Cameras.json");
        new CameraSettingsStore(path).SaveCameras(new ICamera[] { Cam("A", 4), Cam("B", 2) });

        var loaded = new CameraSettingsStore(path).GetAllCameras().ToList();

        Assert.Equal(new int?[] { 4, 2 }, loaded.Select(c => c.CompanionNumber).ToArray());
    }

    [Fact]
    public void CamerasJsonFromBeforeCompanionNumbers_LoadsWithoutNumbers()
    {
        var path = Path.Combine(_dir, "Cameras.json");
        File.WriteAllText(path, LegacyCamerasJson("A", null));

        var camera = Assert.Single(new CameraSettingsStore(path).GetAllCameras());

        Assert.Equal("A", camera.Name);
        Assert.Null(camera.CompanionNumber);
    }

    [Fact]
    public void StoredNumberBelowOne_IsTreatedAsMissing_LoggedAndDoesNotFailTheLoad()
    {
        var path = Path.Combine(_dir, "Cameras.json");
        File.WriteAllText(path, LegacyCamerasJson("A", 0));

        var camera = Assert.Single(new CameraSettingsStore(path).GetAllCameras());

        Assert.Null(camera.CompanionNumber);
        Assert.Contains(_trace.Messages, m => m.Contains("Companion number 0") && m.Contains("A"));
    }

    private static string LegacyCamerasJson(string name, int? number)
    {
        var type = typeof(ViscaIpDevice).AssemblyQualifiedName;
        var numberField = number == null ? "" : $",\"CompanionNumber\":{number}";
        return $"[{{\"IViscaDeveiceTypeAssemblyQualifiedName\":\"{type}\",\"IpAddress\":\"127.0.0.1\",\"Name\":\"{name}\",\"Port\":52381,\"Protocol\":0,\"SendWaitTime\":30,\"UseHeader\":true,\"SingleCommand\":false{numberField}}}]";
    }

    private sealed class MemoryStore : ICameraSettingsStore
    {
        private readonly ICamera[] _cameras;

        public MemoryStore(params ICamera[] cameras) => _cameras = cameras;

        public List<ICamera> Saved { get; } = new();

        // The numbers as they were when last saved: Saved holds the live cameras.
        public int?[] SavedNumbers { get; private set; } = Array.Empty<int?>();

        public IEnumerable<ICamera> GetAllCameras() => _cameras;

        public bool SaveCameras(IEnumerable<ICamera> cameras)
        {
            Saved.Clear();
            Saved.AddRange(cameras);
            SavedNumbers = Saved.Select(c => c.CompanionNumber).ToArray();
            return true;
        }
    }

    // A camera of another camera type: it implements only the camera abstraction, not the VISCA base type.
    private sealed class NonViscaCamera : ICamera
    {
        private int? _companionNumber;

        public NonViscaCamera(string name) => Name = name;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event PropertyChangedEventHandler? PersistentPropertyChanged;

        public string Name { get; }

        public int? CompanionNumber
        {
            get => _companionNumber;
            set
            {
                _companionNumber = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompanionNumber)));
                PersistentPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompanionNumber)));
            }
        }

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

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public override void Write(string? message) => _messages.Enqueue(message ?? string.Empty);

        public override void WriteLine(string? message) => _messages.Enqueue(message ?? string.Empty);
    }
}
