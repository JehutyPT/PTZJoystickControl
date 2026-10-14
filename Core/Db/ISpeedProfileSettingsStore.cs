using PtzJoystickControl.Core.Model;

namespace PtzJoystickControl.Core.Db;

public interface ISpeedProfileSettingsStore
{
    /// <summary>The saved speed profiles, or the defaults if none can be used. Never throws.</summary>
    SpeedProfileList GetSpeedProfiles();

    /// <summary>Saves the speed profiles; false, with the error logged, if they could not be saved. Never throws.</summary>
    bool SaveSpeedProfiles(SpeedProfileList speedProfiles);
}
