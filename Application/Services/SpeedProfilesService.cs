using System.Diagnostics;
using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Services;

public class SpeedProfilesService : ISpeedProfilesService
{
    private readonly ISpeedProfileSettingsStore _store;
    // Changes are made, published and saved one at a time, so the file always ends with the latest list.
    private readonly object _sync = new();
    private volatile SpeedProfileList _profiles;

    public SpeedProfilesService(ISpeedProfileSettingsStore store)
    {
        _store = store;
        _profiles = store.GetSpeedProfiles();
    }

    public SpeedProfileList Profiles => _profiles;

    public event EventHandler? ProfilesChanged;

    public void StepFaster() => Change(profiles => profiles.ActivePosition < profiles.Count
        ? new SpeedProfileList(profiles.Profiles, profiles.ActivePosition + 1)
        : null);

    public void StepFiner() => Change(profiles => profiles.ActivePosition > 1
        ? new SpeedProfileList(profiles.Profiles, profiles.ActivePosition - 1)
        : null);

    public void SetActive(int position) => Change(profiles =>
        Exists(profiles, position, $"make speed profile {position} active") && position != profiles.ActivePosition
            ? new SpeedProfileList(profiles.Profiles, position)
            : null);

    public void Add() => Change(profiles => new SpeedProfileList(
        profiles.Profiles.Append(new SpeedProfile($"Profile {profiles.Count + 1}",
            SpeedProfile.MaxPercent, SpeedProfile.MaxPercent, SpeedProfile.MaxPercent, SpeedProfile.MaxPercent)),
        profiles.ActivePosition));

    public void Edit(int position, SpeedProfile edited) => Change(profiles =>
        Exists(profiles, position, $"edit speed profile {position}") && IsValid(edited, $"edit speed profile {position}")
        && edited != profiles.Profiles[position - 1]
            ? new SpeedProfileList(
                profiles.Profiles.Select((profile, index) => index == position - 1 ? edited : profile),
                profiles.ActivePosition)
            : null);

    public void MoveUp(int position) => Change(profiles => Move(profiles, position, -1, "up"));

    public void MoveDown(int position) => Change(profiles => Move(profiles, position, +1, "down"));

    public void Remove(int position) => Change(profiles =>
    {
        var request = $"remove speed profile {position}";
        if (!Exists(profiles, position, request))
            return null;

        if (profiles.Count == 1)
        {
            Refuse(request, "the list always keeps at least one speed profile");
            return null;
        }

        return new SpeedProfileList(
            profiles.Profiles.Where((_, index) => index != position - 1),
            position == profiles.ActivePosition ? 1
            : position < profiles.ActivePosition ? profiles.ActivePosition - 1
            : profiles.ActivePosition);
    });

    // Swaps the profile at position with its neighbour step away (-1 above, +1 below);
    // the profile that was active stays active.
    private static SpeedProfileList? Move(SpeedProfileList profiles, int position, int step, string direction)
    {
        var request = $"move speed profile {position} {direction}";
        if (!Exists(profiles, position, request))
            return null;

        var other = position + step;
        if (other < 1 || other > profiles.Count)
        {
            Refuse(request, step < 0 ? "it is already first" : "it is already last");
            return null;
        }

        var swapped = profiles.Profiles.ToArray();
        (swapped[position - 1], swapped[other - 1]) = (swapped[other - 1], swapped[position - 1]);

        var active = profiles.ActivePosition == position ? other
            : profiles.ActivePosition == other ? position
            : profiles.ActivePosition;
        return new SpeedProfileList(swapped, active);
    }

    private static bool Exists(SpeedProfileList profiles, int position, string request)
    {
        if (position >= 1 && position <= profiles.Count)
            return true;

        Refuse(request, $"there are speed profiles 1 to {profiles.Count} only");
        return false;
    }

    private static bool IsValid(SpeedProfile? profile, string request)
    {
        if (profile == null)
        {
            Refuse(request, "no speed profile was given");
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            Refuse(request, "a speed profile needs a name");
            return false;
        }

        foreach (var (axis, percent) in new[]
                 {
                     ("pan", profile.Pan), ("tilt", profile.Tilt), ("zoom", profile.Zoom), ("focus", profile.Focus),
                 })
        {
            if (percent >= SpeedProfile.MinPercent && percent <= SpeedProfile.MaxPercent)
                continue;

            Refuse(request, $"{axis} {percent} % is not between {SpeedProfile.MinPercent} and {SpeedProfile.MaxPercent}");
            return false;
        }

        return true;
    }

    private static void Refuse(string request, string reason) =>
        Trace.TraceWarning($"Speed profiles: cannot {request}: {reason}");

    // change returns the new list, or null to leave the list as it is.
    private void Change(Func<SpeedProfileList, SpeedProfileList?> change)
    {
        lock (_sync)
        {
            var changed = change(_profiles);
            if (changed == null)
                return;

            _profiles = changed;
            _store.SaveSpeedProfiles(changed);
        }

        RaiseProfilesChanged();
    }

    private void RaiseProfilesChanged()
    {
        try
        {
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            Trace.TraceError($"Speed profiles: a change subscriber failed: {e}");
        }
    }
}
