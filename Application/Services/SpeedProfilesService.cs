using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Services;

public class SpeedProfilesService : ISpeedProfilesService
{
    private volatile SpeedProfileList _profiles;

    public SpeedProfilesService(ISpeedProfileSettingsStore store)
    {
        _profiles = store.GetSpeedProfiles();
    }

    public SpeedProfileList Profiles => _profiles;
}
