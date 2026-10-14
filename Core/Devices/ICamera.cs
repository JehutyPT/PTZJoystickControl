using System.ComponentModel;

namespace PtzJoystickControl.Core.Devices;

/// <summary>
/// A camera, whatever its camera type (see ADR 0001). Everything outside a camera type's own
/// implementation and settings panel uses only this; each camera type maps the calls onto its own
/// protocol.
/// </summary>
public interface ICamera : INotifyPropertyChanged
{
    string Name { get; }

    /// <summary>
    /// The number Companion uses to select this camera (<c>?SelectCamera=N</c>, or the legacy
    /// <c>?SetCameraPreview=N</c>). Null only until <c>CamerasService</c> assigns one; never below 1.
    /// </summary>
    int? CompanionNumber { get; set; }

    bool Connected { get; }

    /// <summary>
    /// True when the last pan/tilt, zoom or focus command the app sent was a move rather than a
    /// stop, i.e. the app believes it is driving the camera. Preset recalls are not app-driven
    /// motion and do not count.
    /// </summary>
    bool IsMoving { get; }

    /// <summary>Raised when a setting saved in <c>Cameras.json</c> changes.</summary>
    event PropertyChangedEventHandler? PersistentPropertyChanged;

    // Movement speeds are speed fractions: 0..1 of the axis's full range, whatever the camera
    // type. A move at fraction 0 still moves (at the slowest speed); only a Stop direction stops.
    void Pan(double panSpeed, PanDir panDir);
    void Tilt(double tiltSpeed, TiltDir tiltDir);
    void Zoom(double zoomSpeed, ZoomDir zoomDir);
    void Focus(double focusSpeed, FocusDir focusDir);

    /// <summary>
    /// Stops pan, tilt, zoom and focus, even when the camera is already believed to be stopped
    /// (its real state may differ if a stop was lost).
    /// </summary>
    void Stop();

    void FocusMode(FocusMode focusMode);
    void FocusLock(FocusLock focusLock);
    void Power(Power power);

    /// <summary>Recalls, sets or resets camera preset <paramref name="presetNumber"/>.</summary>
    void Preset(Preset preset, byte presetNumber);

    /// <summary>The recall speed of later camera preset recalls, in the app's range of 1 to 24.</summary>
    void PresetRecallSpeed(byte speed);
}
