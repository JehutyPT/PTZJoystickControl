using System.Collections.ObjectModel;
using System.ComponentModel;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

public class DrivenCamerasTests
{
    [Fact]
    public void StopAll_StopsEveryCameraSelectedSinceTracking_OnceEach()
    {
        var camA = new RecordingCamera("A");
        var camB = new RecordingCamera("B");
        var unused = new RecordingCamera("C");
        var gamepad = new TestGamepad();
        var driven = new DrivenCameras(gamepad);

        gamepad.SelectedCamera = camA;
        gamepad.SelectedCamera = camB;
        gamepad.SelectedCamera = camA;
        driven.StopAll();

        Assert.Equal(1, camA.StopCalls);
        Assert.Equal(1, camB.StopCalls);
        Assert.Equal(0, unused.StopCalls);
    }

    [Fact]
    public void StopAll_IncludesTheCameraSelectedBeforeTrackingStarted()
    {
        var camA = new RecordingCamera("A");
        var gamepad = new TestGamepad { SelectedCamera = camA };
        var driven = new DrivenCameras(gamepad);

        driven.StopAll();

        Assert.Equal(1, camA.StopCalls);
    }

    [Fact]
    public void StopAll_StillStopsACameraThatWasDeselected()
    {
        var camA = new RecordingCamera("A");
        var gamepad = new TestGamepad();
        var driven = new DrivenCameras(gamepad);

        gamepad.SelectedCamera = camA;
        gamepad.SelectedCamera = null;
        driven.StopAll();

        Assert.Equal(1, camA.StopCalls);
    }

    [Fact]
    public void StopAll_WithNoCameraEverSelected_DoesNothing()
    {
        var gamepad = new TestGamepad();
        var driven = new DrivenCameras(gamepad);

        var exception = Record.Exception(driven.StopAll);

        Assert.Null(exception);
    }

    [Fact]
    public void StopAll_WhenOneCameraThrows_StillStopsTheOthersAndDoesNotThrow()
    {
        var failing = new RecordingCamera("A") { ThrowOnStop = true };
        var camB = new RecordingCamera("B");
        var gamepad = new TestGamepad();
        var driven = new DrivenCameras(gamepad);
        gamepad.SelectedCamera = failing;
        gamepad.SelectedCamera = camB;

        var exception = Record.Exception(driven.StopAll);

        Assert.Null(exception);
        Assert.Equal(1, failing.StopCalls);
        Assert.Equal(1, camB.StopCalls);
    }

    [Fact]
    public void StopAll_StopsTrackingTheGamepad()
    {
        var camA = new RecordingCamera("A");
        var camB = new RecordingCamera("B");
        var gamepad = new TestGamepad { SelectedCamera = camA };
        var driven = new DrivenCameras(gamepad);

        driven.StopAll();
        gamepad.SelectedCamera = camB;
        driven.StopAll();

        Assert.Equal(1, camA.StopCalls);
        Assert.Equal(0, camB.StopCalls);
        Assert.False(gamepad.HasPropertyChangedSubscribers);
    }

    private sealed class RecordingCamera : ViscaDeviceBase
    {
        public RecordingCamera(string name) : base(name) { }

        public int StopCalls { get; private set; }
        public bool ThrowOnStop { get; set; }

        public override void Stop()
        {
            StopCalls++;
            if (ThrowOnStop)
                throw new InvalidOperationException("Simulated camera failure");
        }

        public override void Power(Power byteVal) { }
        public override void Pan(byte panSpeed, PanDir panDir) { }
        public override void Tilt(byte tiltSpeed, TiltDir tiltDir) { }
        public override void PanTilt(byte panSpeed, byte tiltSpeed, PanDir panDir, TiltDir tiltDir) { }
        public override void Zoom(byte zoomSpeed, ZoomDir zoomDir) { }
        public override void Focus(byte focusSpeed, FocusDir focusDir) { }
        public override void FocusMode(FocusMode focusMode) { }
        public override void FocusLock(FocusLock focusMode) { }
        public override void Preset(Preset preset, byte presetNumber) { }
        public override void PresetRecallSpeed(byte value) { }
    }

    private sealed class TestGamepad : IGamepad
    {
        private ViscaDeviceBase? _selectedCamera;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public bool HasPropertyChangedSubscribers => PropertyChanged != null;

        public string Id => "test-gamepad";
        public string Name => "Test gamepad";
        public bool IsActivated { get; set; } = true;
        public bool IsConnected { get; set; } = true;
        public ObservableCollection<ViscaDeviceBase>? Cameras { get; set; }
        public IReadOnlyList<ICommand> Commands => Array.Empty<ICommand>();
        public bool DetectInput { get; set; }
        public IEnumerable<IInput> Inputs => Array.Empty<IInput>();

        public ViscaDeviceBase? SelectedCamera
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
}
