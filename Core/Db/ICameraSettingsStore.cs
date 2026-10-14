using PtzJoystickControl.Core.Devices;

namespace PtzJoystickControl.Core.Db
{
    public interface ICameraSettingsStore
    {
        IEnumerable<ICamera> GetAllCameras();
        bool SaveCameras(IEnumerable<ICamera> cameras);
    }
}