namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// Capped exponential backoff for reconnecting to the panel. A copy of
/// <c>BitfocusCompanionService.RetryDelay</c> / <c>ShouldLogRetryFailure</c> (#27): #27 ruled out a
/// shared reconnect framework, so each connection keeps its own small copy.
/// </summary>
internal static class ReconnectBackoff
{
    /// <summary>
    /// The wait before retry <paramref name="attempt"/> (1-based): <paramref name="initial"/>, doubling per attempt, capped at <paramref name="max"/>.
    /// </summary>
    public static TimeSpan Delay(int attempt, TimeSpan initial, TimeSpan max)
    {
        var doublings = Math.Clamp(attempt - 1, 0, 30);
        var ticks = initial.Ticks * Math.Pow(2, doublings);
        return ticks >= max.Ticks ? max : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>
    /// Failed attempts are logged at 1, 2, 4, 8, ... so a panel that is off for hours writes a handful of lines, not one per attempt.
    /// </summary>
    public static bool ShouldLog(int attempt) => attempt > 0 && (attempt & (attempt - 1)) == 0;
}
