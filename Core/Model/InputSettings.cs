using PtzJoystickControl.Core.Devices;

namespace PtzJoystickControl.Core.Model;

public class InputSettings
{

    public string? CommandType { get; set; }
    public CommandDirectionOption? CommandDirection { get; set; }
    public CommandValueOption? CommandValue { get; set; }
    public string Id { get; set; } = null!;
    public bool Inverted { get; set; }
    public float DeadZoneHigh { get; set; }
    public float DeadZoneLow { get; set; }
    // Missing in files from before expo existed: loads as 0 (linear).
    public float Expo { get; set; }
    public InputSettings? SecondInputSettings { get; set; }

    // Marks CommandValue of a speed (dynamic) command as a percentage. Missing in files from
    // before speeds were percentages: false, so the value is read as a VISCA step and migrated.
    public bool SpeedIsPercentage { get; set; }

    public InputSettings()
    {
    }

    public InputSettings(IInput input)
    {
        if (input == null) throw new ArgumentNullException("input");

        CommandType = input.SelectedCommand?.GetType().ToString();
        CommandDirection = input.CommandDirection;
        CommandValue = input.CommandValue;
        Id = input.Id;
        Inverted = input.Inverted;
        DeadZoneHigh = input.Saturation;
        DeadZoneLow = input.DeadZone;
        Expo = input.Expo;
        SecondInputSettings = input.SecondInput != null ? new InputSettings(input.SecondInput) : null;
        SpeedIsPercentage = true;
    }
}