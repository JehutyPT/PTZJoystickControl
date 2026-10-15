using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Services;

// LoadCamerasFromDb runs from the constructor and again on a reload, when the list already holds cameras.
public class CamerasServiceReloadTests
{
    [Fact]
    public void ReloadingOverANonEmptyList_LeavesExactlyTheStoredCameras()
    {
        var store = new SwappableStore(new ViscaIpDevice("Old 1"), new ViscaIpDevice("Old 2"), new ViscaIpDevice("Old 3"));
        var service = new CamerasService(store);
        Assert.Equal(3, service.Cameras.Count);

        var new1 = new ViscaIpDevice("New 1");
        var new2 = new ViscaIpDevice("New 2");
        store.Cameras = new ICamera[] { new1, new2 };

        service.LoadCamerasFromDb();

        Assert.Equal(new ICamera[] { new1, new2 }, service.Cameras);
    }

    [Fact]
    public void ReloadingTheSameStore_LeavesEachStoredCameraOnce()
    {
        var a = new ViscaIpDevice("A");
        var b = new ViscaIpDevice("B");
        var service = new CamerasService(new SwappableStore(a, b));

        service.LoadCamerasFromDb();

        Assert.Equal(new ICamera[] { a, b }, service.Cameras);
    }

    [Fact]
    public void ReloadingOverDifferentCameras_DisposesEachOldOneOnce()
    {
        var old1 = new DisposableStopRecordingCamera("Old 1");
        var old2 = new DisposableStopRecordingCamera("Old 2");
        var store = new SwappableStore(old1, old2);
        var service = new CamerasService(store);
        var new1 = new DisposableStopRecordingCamera("New 1");
        store.Cameras = new ICamera[] { new1 };

        service.LoadCamerasFromDb();

        Assert.Equal(1, old1.DisposeCalls);
        Assert.Equal(1, old2.DisposeCalls);
        Assert.Equal(0, new1.DisposeCalls);
        Assert.Equal(new ICamera[] { new1 }, service.Cameras);
    }

    [Fact]
    public void ReloadingTheSameCameras_DisposesNone()
    {
        var a = new DisposableStopRecordingCamera("A");
        var b = new DisposableStopRecordingCamera("B");
        var service = new CamerasService(new SwappableStore(a, b));

        service.LoadCamerasFromDb();

        Assert.Equal(0, a.DisposeCalls);
        Assert.Equal(0, b.DisposeCalls);
        Assert.Equal(new ICamera[] { a, b }, service.Cameras);
    }

    [Fact]
    public void ReloadingWithOneCameraKept_DisposesOnlyTheOneLeftOut()
    {
        var kept = new DisposableStopRecordingCamera("Kept");
        var leftOut = new DisposableStopRecordingCamera("Left out");
        var store = new SwappableStore(kept, leftOut);
        var service = new CamerasService(store);
        store.Cameras = new ICamera[] { kept };

        service.LoadCamerasFromDb();

        Assert.Equal(0, kept.DisposeCalls);
        Assert.Equal(1, leftOut.DisposeCalls);
        Assert.Equal(new ICamera[] { kept }, service.Cameras);
    }

    private sealed class SwappableStore : ICameraSettingsStore
    {
        public SwappableStore(params ICamera[] cameras) => Cameras = cameras;

        public ICamera[] Cameras { get; set; }

        public IEnumerable<ICamera> GetAllCameras() => Cameras;

        public bool SaveCameras(IEnumerable<ICamera> cameras) => true;
    }
}
