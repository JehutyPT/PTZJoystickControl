namespace PtzJoystickControl.Core.Model;

/// <summary>One speed profile in <c>SpeedProfiles.json</c>. A value missing from the file is null.</summary>
public class SpeedProfileSettings
{
    public string? Name { get; set; }
    public int? Pan { get; set; }
    public int? Tilt { get; set; }
    public int? Zoom { get; set; }
    public int? Focus { get; set; }

    public SpeedProfileSettings()
    {
    }

    public SpeedProfileSettings(SpeedProfile speedProfile)
    {
        Name = speedProfile.Name;
        Pan = speedProfile.Pan;
        Tilt = speedProfile.Tilt;
        Zoom = speedProfile.Zoom;
        Focus = speedProfile.Focus;
    }
}
