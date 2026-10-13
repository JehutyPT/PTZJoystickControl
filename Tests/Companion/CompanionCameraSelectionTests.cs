using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Companion;

// What a validated Companion request does on the input loop: select the camera on every active controller.
public sealed class CompanionCameraSelectionTests : IDisposable
{
    private readonly RecordingTraceListener _trace = new();

    public CompanionCameraSelectionTests() => Trace.Listeners.Add(_trace);

    public void Dispose() => Trace.Listeners.Remove(_trace);

    [Fact]
    public void SelectsTheCameraOnEveryController()
    {
        var camA = new RecordingCamera("A");
        var camB = new RecordingCamera("B");
        var cameras = new ObservableCollection<ViscaDeviceBase> { camA, camB };
        var first = new TestGamepad(cameras) { SelectedCamera = camA };
        var second = new TestGamepad(cameras);

        CompanionCameraSelection.SelectOnEveryController(new[] { first, second }, camB);

        Assert.Same(camB, first.SelectedCamera);
        Assert.Same(camB, second.SelectedCamera);
    }

    [Fact]
    public void AControllerThatFails_IsLogged_AndTheOthersAreStillSelected()
    {
        var cam = new RecordingCamera("A");
        var cameras = new ObservableCollection<ViscaDeviceBase> { cam };
        var failing = new TestGamepad(cameras) { ThrowOnSelect = true };
        var healthy = new TestGamepad(cameras);

        var thrown = Record.Exception(() => CompanionCameraSelection.SelectOnEveryController(new[] { failing, healthy }, cam));

        Assert.Null(thrown);
        Assert.Same(cam, healthy.SelectedCamera);
        Assert.Contains(_trace.Messages, m => m.Contains("Simulated controller failure") && m.Contains(failing.Name));
    }

    [Fact]
    public void AControllerWhoseCameraListNoLongerHasTheCamera_IsSkippedWithAWarning()
    {
        var removed = new RecordingCamera("Removed");
        var kept = new RecordingCamera("Kept");
        var withoutIt = new TestGamepad(new ObservableCollection<ViscaDeviceBase> { kept }) { SelectedCamera = kept };
        var withIt = new TestGamepad(new ObservableCollection<ViscaDeviceBase> { kept, removed });

        CompanionCameraSelection.SelectOnEveryController(new[] { withoutIt, withIt }, removed);

        Assert.Same(kept, withoutIt.SelectedCamera);
        Assert.Same(removed, withIt.SelectedCamera);
        Assert.Contains(_trace.Messages, m => m.Contains("Removed") && m.Contains(withoutIt.Name));
    }

    [Fact]
    public void SwitchingAwayFromAMovingCamera_StopsItOnce_AndDoesNotStopTheNewOne()
    {
        var moving = new RecordingCamera("Moving");
        var next = new RecordingCamera("Next");
        var gamepad = new TestGamepad(new ObservableCollection<ViscaDeviceBase> { moving, next }) { SelectedCamera = moving };
        _ = new DrivenCameras(gamepad);
        moving.Pan(0x08, PanDir.Left);

        CompanionCameraSelection.SelectOnEveryController(new[] { gamepad }, next);

        Assert.Same(next, gamepad.SelectedCamera);
        Assert.Equal(1, moving.StopCalls);
        Assert.Equal(0, next.StopCalls);
    }

    [Fact]
    public void SwitchingAwayFromAnIdleCamera_SendsItNoStop()
    {
        var idle = new RecordingCamera("Idle");
        var next = new RecordingCamera("Next");
        var gamepad = new TestGamepad(new ObservableCollection<ViscaDeviceBase> { idle, next }) { SelectedCamera = idle };
        _ = new DrivenCameras(gamepad);

        CompanionCameraSelection.SelectOnEveryController(new[] { gamepad }, next);

        Assert.Equal(0, idle.StopCalls);
    }

    private sealed class RecordingCamera : ViscaDeviceBase
    {
        public RecordingCamera(string name) : base(name) { }

        public int StopCalls { get; private set; }

        public override void Stop()
        {
            StopCalls++;
            panDir = PanDir.Stop;
            tiltDir = TiltDir.Stop;
            zoomCmd = (byte)ZoomDir.Stop;
            focusCmd = (byte)FocusDir.Stop;
        }

        public override void Power(Power byteVal) { }
        public override void Pan(byte panSpeed, PanDir panDir) => this.panDir = panDir;
        public override void Tilt(byte tiltSpeed, TiltDir tiltDir) => this.tiltDir = tiltDir;
        public override void PanTilt(byte panSpeed, byte tiltSpeed, PanDir panDir, TiltDir tiltDir)
        {
            this.panDir = panDir;
            this.tiltDir = tiltDir;
        }
        public override void Zoom(byte zoomSpeed, ZoomDir zoomDir) { }
        public override void Focus(byte focusSpeed, FocusDir focusDir) { }
        public override void FocusMode(FocusMode focusMode) { }
        public override void FocusLock(FocusLock focusMode) { }
        public override void Preset(Preset preset, byte presetNumber) { }
        public override void PresetRecallSpeed(byte value) { }
    }

    private sealed class TestGamepad : IGamepad
    {
        private static int _count;
        private ViscaDeviceBase? _selectedCamera;

        public TestGamepad(ObservableCollection<ViscaDeviceBase> cameras)
        {
            Cameras = cameras;
            Name = $"Test gamepad {Interlocked.Increment(ref _count)}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public bool ThrowOnSelect { get; set; }

        public string Id => Name;
        public string Name { get; }
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
                if (ThrowOnSelect)
                    throw new InvalidOperationException("Simulated controller failure");
                _selectedCamera = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCamera)));
            }
        }

        public void Acquire() { }
        public void Unacquire() { }
        public void Update() { }
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public override void Write(string? message) => _messages.Enqueue(message ?? string.Empty);

        public override void WriteLine(string? message) => _messages.Enqueue(message ?? string.Empty);
    }
}
