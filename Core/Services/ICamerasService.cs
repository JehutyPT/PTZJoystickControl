using PtzJoystickControl.Core.Devices;
using System.Collections.ObjectModel;

namespace PtzJoystickControl.Core.Services;

public interface ICamerasService
{
    public ObservableCollection<ICamera> Cameras { get; }
    public void AddCamera(ICamera camera);
    public void RemoveCamera(ICamera camera);
}