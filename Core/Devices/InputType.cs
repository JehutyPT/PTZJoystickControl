namespace PtzJoystickControl.Core.Devices;

public enum InputType
{
    Axis,
    Button,
    Unsupported,

    /// <summary>
    /// An encoder: sends signed step counts (clockwise positive) instead of a position.
    /// Runs a static command once per step; never drives dynamic commands.
    /// </summary>
    Relative
}