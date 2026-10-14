namespace PtzJoystickControl.Core.Model;

/// <summary>
/// What a Companion request's <c>SpeedProfile</c> parameter asks for: make the next faster or finer
/// speed profile active, or read back the active one.
/// </summary>
public enum CompanionSpeedProfileRequest
{
    /// <summary><c>SpeedProfile=faster</c>: make the next faster speed profile active; at the fastest, nothing changes.</summary>
    Faster,

    /// <summary><c>SpeedProfile=finer</c>: make the next finer speed profile active; at the finest, nothing changes.</summary>
    Finer,

    /// <summary><c>SpeedProfile=current</c>: answer with the active speed profile, changing nothing.</summary>
    Current,
}
