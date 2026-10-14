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
        var camAStopsBefore = camA.StopCalls;
        var camBStopsBefore = camB.StopCalls;
        driven.StopAll();

        Assert.Equal(1, camA.StopCalls - camAStopsBefore);
        Assert.Equal(1, camB.StopCalls - camBStopsBefore);
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
        var stopsBefore = camA.StopCalls;
        driven.StopAll();

        Assert.Equal(1, camA.StopCalls - stopsBefore);
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
        var failingStopsBefore = failing.StopCalls;

        var exception = Record.Exception(driven.StopAll);

        Assert.Null(exception);
        Assert.Equal(1, failing.StopCalls - failingStopsBefore);
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

    [Fact]
    public void SwitchingAwayFromAPanningCamera_StopsItOnce_AndNotTheNewOne()
    {
        var camA = new RecordingCamera("A");
        var camB = new RecordingCamera("B");
        var gamepad = new TestGamepad { SelectedCamera = camA };
        _ = new DrivenCameras(gamepad);
        camA.Pan(0.5, PanDir.Left);

        gamepad.SelectedCamera = camB;

        Assert.Equal(1, camA.StopCalls);
        Assert.Equal(0, camB.StopCalls);
    }

    [Fact]
    public void SwitchingAwayFromAZoomingCamera_StopsIt()
    {
        var camA = new RecordingCamera("A");
        var gamepad = new TestGamepad { SelectedCamera = camA };
        _ = new DrivenCameras(gamepad);
        camA.Zoom(0.5, ZoomDir.Tele);

        gamepad.SelectedCamera = new RecordingCamera("B");

        Assert.Equal(1, camA.StopCalls);
    }

    [Fact]
    public void SwitchingAwayFromAFocusingCamera_StopsIt()
    {
        var camA = new RecordingCamera("A");
        var gamepad = new TestGamepad { SelectedCamera = camA };
        _ = new DrivenCameras(gamepad);
        camA.Focus(0.5, FocusDir.Near);

        gamepad.SelectedCamera = new RecordingCamera("B");

        Assert.Equal(1, camA.StopCalls);
    }

    [Fact]
    public void SwitchingAwayFromAnIdleCamera_StopsNothing()
    {
        var camA = new RecordingCamera("A");
        var camB = new RecordingCamera("B");
        var gamepad = new TestGamepad { SelectedCamera = camA };
        _ = new DrivenCameras(gamepad);
        camA.Pan(0.5, PanDir.Left);
        camA.Pan(0, PanDir.Stop);

        gamepad.SelectedCamera = camB;

        Assert.Equal(0, camA.StopCalls);
        Assert.Equal(0, camB.StopCalls);
    }

    [Fact]
    public void SwitchingCamera_EachSwitchStopsTheMovingCameraBeingLeft()
    {
        var camA = new RecordingCamera("A");
        var camB = new RecordingCamera("B");
        var gamepad = new TestGamepad();
        _ = new DrivenCameras(gamepad);

        gamepad.SelectedCamera = camA;
        camA.Pan(0.5, PanDir.Left);
        gamepad.SelectedCamera = camB;
        camB.Tilt(0.5, TiltDir.Up);
        gamepad.SelectedCamera = camA;

        Assert.Equal(1, camA.StopCalls);
        Assert.Equal(1, camB.StopCalls);
    }

    [Fact]
    public void ClearingTheSelection_StopsThePreviousCameraWhenMoving()
    {
        var camA = new RecordingCamera("A");
        var gamepad = new TestGamepad { SelectedCamera = camA };
        _ = new DrivenCameras(gamepad);
        camA.Pan(0.5, PanDir.Right);

        gamepad.SelectedCamera = null;

        Assert.Equal(1, camA.StopCalls);
    }

    [Fact]
    public void SelectingWithNothingPreviouslySelected_StopsNothing()
    {
        var camA = new RecordingCamera("A");
        var gamepad = new TestGamepad();
        _ = new DrivenCameras(gamepad);

        gamepad.SelectedCamera = camA;

        Assert.Equal(0, camA.StopCalls);
    }

    [Fact]
    public void ReselectingTheSameCamera_StopsNothing()
    {
        var camA = new RecordingCamera("A");
        var gamepad = new TestGamepad { SelectedCamera = camA };
        _ = new DrivenCameras(gamepad);
        camA.Pan(0.5, PanDir.Left);

        // TestGamepad raises PropertyChanged on every set, even when the value is unchanged.
        gamepad.SelectedCamera = camA;

        Assert.Equal(0, camA.StopCalls);
    }

    [Fact]
    public void SwitchingAwayFromACameraThatThrowsOnStop_DoesNotThrow_AndLaterSwitchesStillStop()
    {
        var failing = new RecordingCamera("A") { ThrowOnStop = true };
        var camB = new RecordingCamera("B");
        var camC = new RecordingCamera("C");
        var gamepad = new TestGamepad { SelectedCamera = failing };
        _ = new DrivenCameras(gamepad);
        failing.Pan(0.5, PanDir.Left);

        var exception = Record.Exception(() => gamepad.SelectedCamera = camB);
        camB.Pan(0.5, PanDir.Left);
        gamepad.SelectedCamera = camC;

        Assert.Null(exception);
        Assert.Equal(1, failing.StopCalls);
        Assert.Equal(1, camB.StopCalls);
        Assert.Equal(0, camC.StopCalls);
    }

    [Fact]
    public void SwitchingCamera_AfterStopAll_StopsNothing()
    {
        var camA = new RecordingCamera("A");
        var camB = new RecordingCamera("B");
        var gamepad = new TestGamepad { SelectedCamera = camA };
        var driven = new DrivenCameras(gamepad);
        driven.StopAll();
        camA.Pan(0.5, PanDir.Left);

        gamepad.SelectedCamera = camB;

        Assert.Equal(1, camA.StopCalls);
        Assert.Equal(0, camB.StopCalls);
    }

    [Fact]
    public void StopAll_StopsIdleCamerasToo()
    {
        var camA = new RecordingCamera("A");
        var camB = new RecordingCamera("B");
        var gamepad = new TestGamepad { SelectedCamera = camA };
        var driven = new DrivenCameras(gamepad);
        gamepad.SelectedCamera = camB;

        driven.StopAll();

        Assert.Equal(1, camA.StopCalls);
        Assert.Equal(1, camB.StopCalls);
    }

    [Fact]
    public async Task SwitchingAwayFromAPanningXcCamera_SendsItStopRequests()
    {
        using var fake = new FakeXcCamera();
        using var xc = XcCameraTests.CameraFor(fake);
        var gamepad = new TestGamepad { SelectedCamera = xc };
        _ = new DrivenCameras(gamepad);
        xc.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);

        // Nothing releases the pan: only the switch can stop the XC camera.
        gamepad.SelectedCamera = new RecordingCamera("B");

        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 3, "three stops");
    }

    [Fact]
    public async Task StopAll_SendsStopRequestsToAnXcCamera()
    {
        using var fake = new FakeXcCamera();
        using var xc = XcCameraTests.CameraFor(fake);
        var gamepad = new TestGamepad { SelectedCamera = xc };
        var driven = new DrivenCameras(gamepad);
        xc.Pan(0.5, PanDir.Left);
        await fake.WaitForControlAsync(1);

        driven.StopAll();

        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 3, "three stops");
    }

    // A camera of another camera type: it implements only the camera abstraction, not the VISCA base type.
    private sealed class RecordingCamera : ICamera
    {
        private PanDir panDir = PanDir.Stop;
        private TiltDir tiltDir = TiltDir.Stop;
        private ZoomDir zoomDir = ZoomDir.Stop;
        private FocusDir focusDir = FocusDir.Stop;

        public RecordingCamera(string name) => Name = name;

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public string Name { get; }
        public int? CompanionNumber { get; set; }
        public bool Connected => true;
        public bool IsMoving =>
            panDir != PanDir.Stop || tiltDir != TiltDir.Stop || zoomDir != ZoomDir.Stop || focusDir != FocusDir.Stop;

        public int StopCalls { get; private set; }
        public bool ThrowOnStop { get; set; }

        public void Stop()
        {
            StopCalls++;
            panDir = PanDir.Stop;
            tiltDir = TiltDir.Stop;
            zoomDir = ZoomDir.Stop;
            focusDir = FocusDir.Stop;
            if (ThrowOnStop)
                throw new InvalidOperationException("Simulated camera failure");
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

    private sealed class TestGamepad : IGamepad
    {
        private ICamera? _selectedCamera;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public bool HasPropertyChangedSubscribers => PropertyChanged != null;

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
