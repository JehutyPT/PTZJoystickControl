using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Model;

namespace PtzJoystickControl.Tests.Services;

/// <summary>A speed-profile store that loads a given list from memory and records every save.</summary>
internal sealed class MemorySpeedProfileStore : ISpeedProfileSettingsStore
{
    private readonly SpeedProfileList _stored;

    public MemorySpeedProfileStore(SpeedProfileList stored) => _stored = stored;

    public List<SpeedProfileList> Saves { get; } = new();

    public SpeedProfileList GetSpeedProfiles() => _stored;

    public bool SaveSpeedProfiles(SpeedProfileList speedProfiles)
    {
        Saves.Add(speedProfiles);
        return true;
    }
}

internal static class TestSpeedProfiles
{
    /// <summary>
    /// A service whose only speed profile is 100 % on every axis, so movement commands hand the
    /// camera the input's fraction unchanged (as before speed profiles, #43).
    /// </summary>
    public static SpeedProfilesService Full() => new(new MemorySpeedProfileStore(
        new SpeedProfileList(new[] { new SpeedProfile("Full", 100, 100, 100, 100) }, activePosition: 1)));
}
