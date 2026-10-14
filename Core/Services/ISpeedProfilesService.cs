using PtzJoystickControl.Core.Model;

namespace PtzJoystickControl.Core.Services;

/// <summary>
/// The one global, ordered list of speed profiles (position 1 = finest, last = fastest) and the active one,
/// for every camera (docs/adr/0002-global-speed-profiles.md). Each change is saved to SpeedProfiles.json on
/// the calling thread and then announced. No operation throws: a request that is not valid (a position
/// outside the list, a blank name, a percentage outside 1–100, removing the only profile) is logged at
/// WARN and changes nothing.
/// </summary>
public interface ISpeedProfilesService
{
    /// <summary>
    /// The speed profiles and the active one. An immutable snapshot: read it from any thread without
    /// locking (the input loop reads it for every movement command).
    /// </summary>
    SpeedProfileList Profiles { get; }

    /// <summary>
    /// Raised after each change, on the thread that made it; read <see cref="Profiles"/> for the new state.
    /// A request that changes nothing raises nothing.
    /// </summary>
    event EventHandler? ProfilesChanged;

    /// <summary>Makes the next faster profile active; at the fastest, nothing changes (no wrapping).</summary>
    void StepFaster();

    /// <summary>Makes the next finer profile active; at the finest, nothing changes (no wrapping).</summary>
    void StepFiner();

    /// <summary>Makes the profile at 1-based <paramref name="position"/> active.</summary>
    void SetActive(int position);

    /// <summary>Adds "Profile N" (N = the new count), 100 % on every axis, at the end. The active profile stays active.</summary>
    void Add();

    /// <summary>
    /// Replaces the profile at 1-based <paramref name="position"/> with <paramref name="edited"/>: a new name,
    /// new percentages, or both. The active profile stays active.
    /// </summary>
    void Edit(int position, SpeedProfile edited);

    /// <summary>Swaps the profile at 1-based <paramref name="position"/> with the one above it (finer). The same profile stays active.</summary>
    void MoveUp(int position);

    /// <summary>Swaps the profile at 1-based <paramref name="position"/> with the one below it (faster). The same profile stays active.</summary>
    void MoveDown(int position);

    /// <summary>
    /// Removes the profile at 1-based <paramref name="position"/>. Removing the active profile makes position 1
    /// (finest) active; removing another keeps the same profile active.
    /// </summary>
    void Remove(int position);
}
