using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Tests.Skaarhoj;

/// <summary>
/// A camera that records every motion command and stop, and keeps the last-sent motion state
/// the way <c>ViscaIpDevice</c> does, so <see cref="ICamera.IsMoving"/> reflects what was driven.
/// Thread-safe to read from the test thread.
/// </summary>
internal sealed class MotionRecordingCamera : ICamera
{
    private readonly ConcurrentQueue<string> _calls = new();
    private int _stopCalls;
    private PanDir panDir = PanDir.Stop;
    private TiltDir tiltDir = TiltDir.Stop;
    private ZoomDir zoomDir = ZoomDir.Stop;
    private FocusDir focusDir = FocusDir.Stop;

    public MotionRecordingCamera(string name) => Name = name;

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

    public string Name { get; }
    public int? CompanionNumber { get; set; }
    public bool Connected => true;
    public bool IsMoving =>
        panDir != PanDir.Stop || tiltDir != TiltDir.Stop || zoomDir != ZoomDir.Stop || focusDir != FocusDir.Stop;

    /// <summary>
    /// Motion commands and focus steps in order, with speed fractions or focus range shares, e.g.
    /// <c>Pan 1 Right</c>, <c>Zoom 0.5 Tele</c>, <c>FocusStep 0.005 Far</c>.
    /// </summary>
    public IReadOnlyList<string> Calls => _calls.ToArray();

    public int StopCalls => Volatile.Read(ref _stopCalls);

    // Speed fractions, culture-independent: 1, 0.5, 0.333.
    private static string F(double speed) => speed.ToString("0.###", CultureInfo.InvariantCulture);

    public void Stop()
    {
        Interlocked.Increment(ref _stopCalls);
        panDir = PanDir.Stop;
        tiltDir = TiltDir.Stop;
        zoomDir = ZoomDir.Stop;
        focusDir = FocusDir.Stop;
    }

    public void Pan(double panSpeed, PanDir panDir)
    {
        this.panDir = panDir;
        _calls.Enqueue($"Pan {F(panSpeed)} {panDir}");
    }

    public void Tilt(double tiltSpeed, TiltDir tiltDir)
    {
        this.tiltDir = tiltDir;
        _calls.Enqueue($"Tilt {F(tiltSpeed)} {tiltDir}");
    }

    public void Zoom(double zoomSpeed, ZoomDir zoomDir)
    {
        this.zoomDir = zoomDir;
        _calls.Enqueue($"Zoom {F(zoomSpeed)} {zoomDir}");
    }

    public void Focus(double focusSpeed, FocusDir focusDir)
    {
        this.focusDir = focusDir;
        _calls.Enqueue($"Focus {F(focusSpeed)} {focusDir}");
    }

    public void Power(Power power) { }

    // A step is not a move (as on XcCamera): it leaves IsMoving alone.
    public void FocusStep(double focusShare, FocusDir focusDir) =>
        _calls.Enqueue($"FocusStep {F(focusShare)} {focusDir}");

    public void FocusMode(FocusMode focusMode) { }
    public void FocusLock(FocusLock focusLock) { }
    public void Preset(Preset preset, byte presetNumber) { }
    public void PresetRecallSpeed(byte speed) { }
}

internal sealed class FakeCamerasService : ICamerasService
{
    public FakeCamerasService(params ICamera[] cameras) =>
        Cameras = new ObservableCollection<ICamera>(cameras);

    public ObservableCollection<ICamera> Cameras { get; }

    public void AddCamera(ICamera camera) => Cameras.Add(camera);

    public void RemoveCamera(ICamera camera) => Cameras.Remove(camera);
}

/// <summary>A Companion service whose inbound camera selections the test raises.</summary>
internal sealed class RaisingCompanionService : IBitfocusCompanionService
{
    public event EventHandler<BitfocusCompanionEvent>? ReceivedBitfocusCommand;
    public void Select(ICamera camera) =>
        ReceivedBitfocusCommand?.Invoke(this, new BitfocusCompanionEvent(InboundBitfocusCompanionEventEnum.SelectCamera, 1, camera));
    public void PressButton(int? pageNumber, int? buttonNumber) { }
    public void SetCustomVariable(string variableName, string value) { }
    public string? InboundListenerError => null;
    public event EventHandler? InboundListenerErrorChanged { add { } remove { } }
}

/// <summary>The panel's Focus knob mapped to Focus step, as a show would map it.</summary>
internal static class FocusKnob
{
    /// <summary>What <see cref="MotionRecordingCamera"/> records for one clockwise detent.</summary>
    public const string Far = "FocusStep 0.005 Far";

    /// <summary>What <see cref="MotionRecordingCamera"/> records for one anticlockwise detent.</summary>
    public const string Near = "FocusStep 0.005 Near";

    /// <summary>Focus step Far 0.5 % per clockwise detent, and Near 0.5 % (the second command) per anticlockwise one.</summary>
    public static void MapToFocusStep(IGamepad gamepad)
    {
        var focus = gamepad.Inputs.Single(i => i.Name == "Focus");
        var focusStep = gamepad.Commands.OfType<FocusStepCommand>().Single();
        focus.SelectedCommand = focusStep;
        focus.CommandDirection = focus.Directions!.Single(d => d.Direction == Direction.High);
        focus.CommandValue = focus.Values!.Single(v => v.Value == 50);
        focus.SecondInput!.SelectedCommand = focusStep;
        focus.SecondInput.CommandValue = focus.SecondInput.Values!.Single(v => v.Value == -50);
    }
}

/// <summary>
/// The panel's Iris wheel, which starts unmapped, mapped to Focus step for the tests: 1 %, not the
/// Focus wheel's 0.5 %, so the calls show which wheel ran.
/// </summary>
internal static class IrisWheel
{
    /// <summary>What <see cref="MotionRecordingCamera"/> records for one step scrolled up.</summary>
    public const string Far = "FocusStep 0.01 Far";

    /// <summary>What <see cref="MotionRecordingCamera"/> records for one step scrolled down.</summary>
    public const string Near = "FocusStep 0.01 Near";

    /// <summary>Focus step Far 1 % per step scrolled up, and Near 1 % (the second command) per step scrolled down.</summary>
    public static void MapToFocusStep(IGamepad gamepad)
    {
        var iris = gamepad.Inputs.Single(i => i.Name == "Iris");
        var focusStep = gamepad.Commands.OfType<FocusStepCommand>().Single();
        iris.SelectedCommand = focusStep;
        iris.CommandDirection = iris.Directions!.Single(d => d.Direction == Direction.High);
        iris.CommandValue = iris.Values!.Single(v => v.Value == 100);
        iris.SecondInput!.SelectedCommand = focusStep;
        iris.SecondInput.CommandValue = iris.SecondInput.Values!.Single(v => v.Value == -100);
    }
}
