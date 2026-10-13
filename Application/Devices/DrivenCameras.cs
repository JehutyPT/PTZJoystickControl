using PtzJoystickControl.Core.Devices;
using System.ComponentModel;
using System.Diagnostics;

namespace PtzJoystickControl.Application.Devices;

/// <summary>
/// Follows a controller's camera selection while it is active. When the selection changes and the
/// app is driving the camera being left (<see cref="ViscaDeviceBase.IsMoving"/>), that camera is
/// stopped once, so it cannot keep moving on the last command it was sent. Every camera the
/// controller has had selected is also remembered, so that when the controller goes away each of
/// them is stopped unconditionally: a stop on switch may have been lost.
/// </summary>
public sealed class DrivenCameras
{
    private readonly IGamepad gamepad;
    private readonly HashSet<ViscaDeviceBase> cameras = new();
    private ViscaDeviceBase? current;
    private bool tracking = true;

    public DrivenCameras(IGamepad gamepad)
    {
        this.gamepad = gamepad;
        current = gamepad.SelectedCamera;
        if (current != null)
            cameras.Add(current);
        gamepad.PropertyChanged += OnGamepadPropertyChanged;
    }

    /// <summary>
    /// Stops tracking, then calls <see cref="ViscaDeviceBase.Stop"/> once on each tracked camera.
    /// A camera that fails to stop does not keep the others from stopping, and nothing is thrown.
    /// </summary>
    public void StopAll()
    {
        gamepad.PropertyChanged -= OnGamepadPropertyChanged;

        ViscaDeviceBase[] toStop;
        lock (cameras)
        {
            tracking = false;
            current = null;
            toStop = cameras.ToArray();
            cameras.Clear();
        }

        foreach (var camera in toStop)
        {
            try
            {
                camera.Stop();
            }
            catch (Exception e)
            {
                Trace.TraceError($"Stopping camera {camera.Name} after controller {gamepad.Name} went away: {e}");
            }
        }
    }

    private void OnGamepadPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IGamepad.SelectedCamera))
            OnSelectionChanged(gamepad.SelectedCamera);
    }

    /// <summary>
    /// Stops the camera being left if the app is driving it (live-safety: stop on camera switch)
    /// and tracks the new one.
    /// Runs inside the <see cref="IGamepad.SelectedCamera"/> setter, so the stop is queued before
    /// any command can reach the newly selected camera.
    /// </summary>
    private void OnSelectionChanged(ViscaDeviceBase? selected)
    {
        ViscaDeviceBase? previous;
        lock (cameras)
        {
            if (!tracking || ReferenceEquals(current, selected))
                return;

            previous = current;
            current = selected;
            if (selected != null)
                cameras.Add(selected);
        }

        // Only stop motion the app started: an idle camera may be running a preset recall
        // (e.g. on a vMix preview change via Companion), which a stop would cut short.
        if (previous == null || !previous.IsMoving)
            return;

        try
        {
            previous.Stop();
        }
        catch (Exception e)
        {
            Trace.TraceError($"Stopping camera {previous.Name} after controller {gamepad.Name} switched away from it: {e}");
        }
    }
}
