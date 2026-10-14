using PtzJoystickControl.Core.Model;

namespace PtzJoystickControl.Core.Services;

public interface ISpeedProfilesService
{
    /// <summary>
    /// The speed profiles and the active one. An immutable snapshot: read it from any thread without
    /// locking (the input loop reads it for every movement command).
    /// </summary>
    SpeedProfileList Profiles { get; }
}
