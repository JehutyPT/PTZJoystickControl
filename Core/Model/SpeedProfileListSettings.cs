namespace PtzJoystickControl.Core.Model;

/// <summary>What <c>SpeedProfiles.json</c> holds: the speed profiles in order and the active one's 1-based position.</summary>
public class SpeedProfileListSettings
{
    public int? ActivePosition { get; set; }
    public List<SpeedProfileSettings?>? Profiles { get; set; }

    public SpeedProfileListSettings()
    {
    }

    public SpeedProfileListSettings(SpeedProfileList speedProfiles)
    {
        ActivePosition = speedProfiles.ActivePosition;
        Profiles = speedProfiles.Profiles.Select(profile => (SpeedProfileSettings?)new SpeedProfileSettings(profile)).ToList();
    }
}
