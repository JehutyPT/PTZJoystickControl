using PtzJoystickControl.Core.Devices;
using System.ComponentModel;
using System.Diagnostics;

namespace PtzJoystickControl.Application.Devices;

/// <summary>
/// Remembers every camera a controller has had selected while it was active, so that when the
/// controller goes away each of them can be stopped. Selection history rather than only the
/// current selection: switching cameras does not stop the previous one, so it may still be moving.
/// </summary>
public sealed class DrivenCameras
{
    private readonly IGamepad gamepad;
    private readonly HashSet<ViscaDeviceBase> cameras = new();
    private bool tracking = true;

    public DrivenCameras(IGamepad gamepad)
    {
        this.gamepad = gamepad;
        Track(gamepad.SelectedCamera);
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
                Debug.WriteLine($"Stopping camera {camera.Name} after controller {gamepad.Name} went away: {e}");
            }
        }
    }

    private void OnGamepadPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IGamepad.SelectedCamera))
            Track(gamepad.SelectedCamera);
    }

    private void Track(ViscaDeviceBase? camera)
    {
        if (camera == null)
            return;

        lock (cameras)
        {
            if (tracking)
                cameras.Add(camera);
        }
    }
}
