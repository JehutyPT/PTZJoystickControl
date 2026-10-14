using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Tests.Skaarhoj;

/// <summary>
/// A camera that records every motion command and stop, and keeps the base class's last-sent state
/// the way <c>ViscaIpDevice</c> does, so <see cref="ViscaDeviceBase.IsMoving"/> reflects what was driven.
/// Thread-safe to read from the test thread.
/// </summary>
internal sealed class MotionRecordingCamera : ViscaDeviceBase
{
    private readonly ConcurrentQueue<string> _calls = new();
    private int _stopCalls;

    public MotionRecordingCamera(string name) : base(name) { }

    /// <summary>Motion commands in order, with speed fractions, e.g. <c>Pan 1 Right</c>, <c>Zoom 0.5 Tele</c>.</summary>
    public IReadOnlyList<string> Calls => _calls.ToArray();

    public int StopCalls => Volatile.Read(ref _stopCalls);

    // Speed fractions, culture-independent: 1, 0.5, 0.333.
    private static string F(double speed) => speed.ToString("0.###", CultureInfo.InvariantCulture);

    public override void Stop()
    {
        Interlocked.Increment(ref _stopCalls);
        panDir = PanDir.Stop;
        tiltDir = TiltDir.Stop;
        zoomCmd = (byte)ZoomDir.Stop;
        focusCmd = (byte)FocusDir.Stop;
    }

    public override void Pan(double panSpeed, PanDir panDir)
    {
        this.panDir = panDir;
        _calls.Enqueue($"Pan {F(panSpeed)} {panDir}");
    }

    public override void Tilt(double tiltSpeed, TiltDir tiltDir)
    {
        this.tiltDir = tiltDir;
        _calls.Enqueue($"Tilt {F(tiltSpeed)} {tiltDir}");
    }

    public override void PanTilt(double panSpeed, double tiltSpeed, PanDir panDir, TiltDir tiltDir)
    {
        this.panDir = panDir;
        this.tiltDir = tiltDir;
        _calls.Enqueue($"PanTilt {F(panSpeed)} {F(tiltSpeed)} {panDir} {tiltDir}");
    }

    public override void Zoom(double zoomSpeed, ZoomDir zoomDir)
    {
        zoomCmd = zoomDir == ZoomDir.Stop ? (byte)0x00 : (byte)zoomDir;
        _calls.Enqueue($"Zoom {F(zoomSpeed)} {zoomDir}");
    }

    public override void Focus(double focusSpeed, FocusDir focusDir)
    {
        focusCmd = focusDir == FocusDir.Stop ? (byte)0x00 : (byte)focusDir;
        _calls.Enqueue($"Focus {F(focusSpeed)} {focusDir}");
    }

    public override void Power(Power byteVal) { }
    public override void FocusMode(FocusMode focusMode) { }
    public override void FocusLock(FocusLock focusMode) { }
    public override void Preset(Preset preset, byte presetNumber) { }
    public override void PresetRecallSpeed(byte value) { }
}

internal sealed class FakeCamerasService : ICamerasService
{
    public FakeCamerasService(params ViscaDeviceBase[] cameras) =>
        Cameras = new ObservableCollection<ViscaDeviceBase>(cameras);

    public ObservableCollection<ViscaDeviceBase> Cameras { get; }

    public void AddCamera(ViscaDeviceBase camera) => Cameras.Add(camera);

    public void RemoveCamera(ViscaDeviceBase camera) => Cameras.Remove(camera);
}

/// <summary>A Companion service whose inbound camera selections the test raises.</summary>
internal sealed class RaisingCompanionService : IBitfocusCompanionService
{
    public event EventHandler<BitfocusCompanionEvent>? ReceivedBitfocusCommand;
    public void Select(ViscaDeviceBase camera) =>
        ReceivedBitfocusCommand?.Invoke(this, new BitfocusCompanionEvent(InboundBitfocusCompanionEventEnum.SelectCamera, 1, camera));
    public void PressButton(int? pageNumber, int? buttonNumber) { }
    public void SetCustomVariable(string variableName, string value) { }
    public string? InboundListenerError => null;
    public event EventHandler? InboundListenerErrorChanged { add { } remove { } }
}
