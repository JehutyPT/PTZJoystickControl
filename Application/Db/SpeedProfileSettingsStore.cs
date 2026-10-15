using System.Diagnostics;
using System.Text.Json;
using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Model;

namespace PtzJoystickControl.Application.Db;

/// <summary>
/// Keeps the speed profiles and the active one in <c>SpeedProfiles.json</c>. Loading never stops the
/// app starting: with no file the defaults are used; a file that cannot be used falls back to the
/// defaults with an ERROR; a percentage outside 1–100 is clamped, and a missing or out-of-range active
/// position makes the last speed profile active, each with a WARN. The list runs finest first, so the
/// last is taken as the fastest, whatever its name (#57).
/// </summary>
public class SpeedProfileSettingsStore : ISpeedProfileSettingsStore
{
    public const string FileName = "SpeedProfiles.json";

    private readonly string _filePath;

    public SpeedProfileSettingsStore(string filePath)
    {
        _filePath = filePath;
    }

    public SpeedProfileList GetSpeedProfiles()
    {
        if (!File.Exists(_filePath))
            return SpeedProfileList.Defaults;

        try
        {
            // A non-integer percentage fails here, as bad JSON does.
            var settings = JsonSerializer.Deserialize<SpeedProfileListSettings>(File.ReadAllText(_filePath));
            if (settings?.Profiles == null || settings.Profiles.Count == 0)
                return Unusable("it has no speed profiles");

            var profiles = new List<SpeedProfile>(settings.Profiles.Count);
            for (int position = 1; position <= settings.Profiles.Count; position++)
            {
                var profile = settings.Profiles[position - 1];
                if (profile == null || string.IsNullOrWhiteSpace(profile.Name))
                    return Unusable($"speed profile {position} has no name");
                if (profile.Pan == null || profile.Tilt == null || profile.Zoom == null || profile.Focus == null)
                    return Unusable($"speed profile {position} ({profile.Name}) is missing a percentage");

                profiles.Add(new SpeedProfile(
                    profile.Name,
                    Percent(profile.Name, "pan", profile.Pan.Value),
                    Percent(profile.Name, "tilt", profile.Tilt.Value),
                    Percent(profile.Name, "zoom", profile.Zoom.Value),
                    Percent(profile.Name, "focus", profile.Focus.Value)));
            }

            int activePosition = settings.ActivePosition ?? 0;
            if (activePosition < 1 || activePosition > profiles.Count)
            {
                var problem = settings.ActivePosition == null
                    ? "no active position"
                    : $"active position {settings.ActivePosition} is not between 1 and {profiles.Count}";
                Trace.TraceWarning($"Speed profiles in {_filePath}: {problem}; speed profile {profiles.Count} ({profiles[^1].Name}), the last, is active");
                activePosition = profiles.Count;
            }

            return new SpeedProfileList(profiles, activePosition);
        }
        catch (Exception e)
        {
            Trace.TraceError($"Failed to load speed profiles from {_filePath}, using the default speed profiles: {e}");
            return SpeedProfileList.Defaults;
        }
    }

    public bool SaveSpeedProfiles(SpeedProfileList speedProfiles)
    {
        try
        {
            string serializedSpeedProfiles = JsonSerializer.Serialize(new SpeedProfileListSettings(speedProfiles));
            AtomicFile.WriteAllText(_filePath, serializedSpeedProfiles);
            return true;
        }
        catch (Exception e)
        {
            Trace.TraceError($"Failed to save speed profiles to {_filePath}: {e}");
            return false;
        }
    }

    private int Percent(string profileName, string axis, int percent)
    {
        var clamped = Math.Clamp(percent, SpeedProfile.MinPercent, SpeedProfile.MaxPercent);
        if (clamped != percent)
            Trace.TraceWarning($"Speed profiles in {_filePath}: speed profile {profileName} has {axis} {percent} %, not between {SpeedProfile.MinPercent} and {SpeedProfile.MaxPercent}; using {clamped} %");
        return clamped;
    }

    private SpeedProfileList Unusable(string reason)
    {
        Trace.TraceError($"Failed to load speed profiles from {_filePath}, using the default speed profiles: {reason}");
        return SpeedProfileList.Defaults;
    }
}
