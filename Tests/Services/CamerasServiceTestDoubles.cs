using System.Collections.ObjectModel;
using System.ComponentModel;
using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Devices;

namespace PtzJoystickControl.Tests.Services;

/// <summary>A camera that records, in order, each stop and dispose it gets.</summary>
internal class StopRecordingCamera : ICamera
{
    private PanDir panDir = PanDir.Stop;
    private TiltDir tiltDir = TiltDir.Stop;
    private ZoomDir zoomDir = ZoomDir.Stop;
    private FocusDir focusDir = FocusDir.Stop;

    public StopRecordingCamera(string name) => Name = name;

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

    /// <summary>"Stop" and "Dispose", in the order they were called.</summary>
    public List<string> Calls { get; } = new();

    public string Name { get; }
    public int? CompanionNumber { get; set; }
    public bool Connected => true;
    public bool IsMoving =>
        panDir != PanDir.Stop || tiltDir != TiltDir.Stop || zoomDir != ZoomDir.Stop || focusDir != FocusDir.Stop;

    public void Stop()
    {
        Calls.Add("Stop");
        panDir = PanDir.Stop;
        tiltDir = TiltDir.Stop;
        zoomDir = ZoomDir.Stop;
        focusDir = FocusDir.Stop;
    }

    public void Pan(double panSpeed, PanDir panDir) => this.panDir = panDir;
    public void Tilt(double tiltSpeed, TiltDir tiltDir) => this.tiltDir = tiltDir;
    public void Zoom(double zoomSpeed, ZoomDir zoomDir) => this.zoomDir = zoomDir;
    public void Focus(double focusSpeed, FocusDir focusDir) => this.focusDir = focusDir;
    public void Power(Power power) { }
    public void FocusMode(FocusMode focusMode) { }
    public void FocusLock(FocusLock focusLock) { }
    public void Preset(Preset preset, byte presetNumber) { }
    public void PresetRecallSpeed(byte speed) { }
}

/// <summary>A <see cref="StopRecordingCamera"/> that can be disposed, as the app's camera types can.</summary>
internal sealed class DisposableStopRecordingCamera : StopRecordingCamera, IDisposable
{
    public DisposableStopRecordingCamera(string name) : base(name) { }

    public int DisposeCalls => Calls.Count(c => c == "Dispose");

    public void Dispose() => Calls.Add("Dispose");
}

/// <summary>A camera store that loads the given cameras and saves nothing.</summary>
internal sealed class MemoryCameraStore : ICameraSettingsStore
{
    private readonly ICamera[] _cameras;

    public MemoryCameraStore(params ICamera[] cameras) => _cameras = cameras;

    public IEnumerable<ICamera> GetAllCameras() => _cameras;

    public bool SaveCameras(IEnumerable<ICamera> cameras) => true;
}

/// <summary>
/// A controller whose camera list and selected camera the test sets. Raises the selection change, as
/// the app's controllers do, so a <c>DrivenCameras</c> follows it.
/// </summary>
internal sealed class SelectingGamepad : IGamepad
{
    private ICamera? _selectedCamera;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

    /// <summary>
    /// A controller whose selected camera is <paramref name="camera"/>, out of the service's camera
    /// list, wired up as the app wires every controller: the selection command clears a camera that
    /// leaves the list from the selection, and <c>DrivenCameras</c> stops the camera it was driving.
    /// </summary>
    public static SelectingGamepad Driving(CamerasService service, ICamera camera)
    {
        var gamepad = new SelectingGamepad { Cameras = service.Cameras, SelectedCamera = camera };
        _ = new SelectCameraCommand(gamepad, new NullCompanionService());
        _ = new DrivenCameras(gamepad);
        return gamepad;
    }

    public string Id => "selecting-gamepad";
    public string Name => "Selecting gamepad";
    public bool IsActivated { get; set; } = true;
    public bool IsConnected { get; set; } = true;
    public ObservableCollection<ICamera>? Cameras { get; set; }
    public IReadOnlyList<ICommand> Commands => Array.Empty<ICommand>();
    public bool DetectInput { get; set; }
    public IEnumerable<IInput> Inputs => Array.Empty<IInput>();

    public ICamera? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            _selectedCamera = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCamera)));
        }
    }

    public void Acquire() { }
    public void Unacquire() { }
    public void Update() { }
}
