namespace PtzJoystickControl.Core.Model;

/// <summary>
/// The one global, ordered list of speed profiles (position 1 = finest, last = fastest) and which
/// of them is active. Immutable: a change makes a new list, so any thread can read one without locking.
/// </summary>
public sealed class SpeedProfileList
{
    /// <summary>The list used when none is saved: the finest profile is active.</summary>
    public static SpeedProfileList Defaults { get; } = new(new[]
    {
        new SpeedProfile("Fine", 15, 15, 30, 40),
        new SpeedProfile("Medium", 35, 35, 55, 70),
        new SpeedProfile("Fast", 65, 65, 80, 100),
        new SpeedProfile("Full", 100, 100, 100, 100),
    }, activePosition: 1);

    /// <param name="profiles">At least one profile, finest first.</param>
    /// <param name="activePosition">The active profile's 1-based position in <paramref name="profiles"/>.</param>
    public SpeedProfileList(IEnumerable<SpeedProfile> profiles, int activePosition)
    {
        var copy = profiles.ToArray();
        if (copy.Length == 0)
            throw new ArgumentException("A speed profile list needs at least one profile", nameof(profiles));
        if (activePosition < 1 || activePosition > copy.Length)
            throw new ArgumentOutOfRangeException(nameof(activePosition), activePosition, $"Must be between 1 and {copy.Length}");

        Profiles = Array.AsReadOnly(copy);
        ActivePosition = activePosition;
    }

    /// <summary>The profiles in order, finest first.</summary>
    public IReadOnlyList<SpeedProfile> Profiles { get; }

    /// <summary>The 1-based position of the active profile.</summary>
    public int ActivePosition { get; }

    public int Count => Profiles.Count;

    public SpeedProfile Active => Profiles[ActivePosition - 1];
}
