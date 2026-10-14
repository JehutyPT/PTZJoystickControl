using System.Diagnostics;
using PtzJoystickControl.Core.Devices;

namespace PtzJoystickControl.Application.Devices;

/// <summary>
/// Applies a camera selection received from Companion. Runs on the input loop only.
/// </summary>
public static class CompanionCameraSelection
{
    /// <summary>
    /// Sets <paramref name="camera"/> as the selected camera on every controller in
    /// <paramref name="controllers"/> whose camera list still has it. Each controller is handled
    /// on its own: a failure is logged and the rest are still selected. Never throws.
    /// Stopping the previous camera, when it was moving, is done by each controller's
    /// <see cref="DrivenCameras"/> as the selection changes.
    /// </summary>
    public static void SelectOnEveryController(IEnumerable<IGamepad> controllers, ICamera camera)
    {
        foreach (var controller in controllers)
        {
            try
            {
                // The camera may have been removed since the request was validated.
                if (controller.Cameras?.Contains(camera) != true)
                {
                    Trace.TraceWarning($"Companion: camera {camera.Name} is no longer in controller {controller.Name}'s camera list; selection skipped");
                    continue;
                }

                controller.SelectedCamera = camera;
            }
            catch (Exception e)
            {
                Trace.TraceError($"Companion: failed to select camera {camera.Name} on controller {controller.Name}: {e}");
            }
        }
    }
}
