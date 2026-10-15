using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Services;

// Removing a camera from the list, wired up as the app wires a controller to it: the selection
// command clears the selection and DrivenCameras stops the camera it was driving.
public class CamerasServiceRemovalTests
{
    [Fact]
    public void RemovingTheDrivenCamera_StopsItAndThenDisposesIt()
    {
        var camera = new DisposableStopRecordingCamera("Cam 1");
        var service = new CamerasService(new MemoryCameraStore(camera));
        SelectingGamepad.Driving(service, camera);
        camera.Pan(0.5, PanDir.Left);

        service.RemoveCamera(camera);

        Assert.Equal(new[] { "Stop", "Dispose" }, camera.Calls);
        Assert.Empty(service.Cameras);
    }

    [Fact]
    public void RemovingAnIdleSelectedCamera_DisposesItWithoutStoppingIt()
    {
        var camera = new DisposableStopRecordingCamera("Cam 1");
        var service = new CamerasService(new MemoryCameraStore(camera));
        SelectingGamepad.Driving(service, camera);

        service.RemoveCamera(camera);

        Assert.Equal(new[] { "Dispose" }, camera.Calls);
        Assert.Empty(service.Cameras);
    }

    [Fact]
    public void RemovingACameraThatIsNotDisposable_RemovesIt()
    {
        var camera = new StopRecordingCamera("Cam 1");
        var other = new DisposableStopRecordingCamera("Cam 2");
        var service = new CamerasService(new MemoryCameraStore(camera, other));
        SelectingGamepad.Driving(service, camera);
        camera.Pan(0.5, PanDir.Left);

        service.RemoveCamera(camera);

        Assert.Equal(new[] { "Stop" }, camera.Calls);
        Assert.Equal(new ICamera[] { other }, service.Cameras);
        Assert.Empty(other.Calls);
    }
}
