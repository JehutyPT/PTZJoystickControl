using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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

    /// <summary>Motion commands in order, with speed fractions, e.g. <c>Pan 1 Right</c>, <c>Zoom 0.5 Tele</c>.</summary>
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
    public void FocusStep(double focusShare, FocusDir focusDir) { }
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
