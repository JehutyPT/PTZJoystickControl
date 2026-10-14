using System.Diagnostics;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>Converts durations to <see cref="Stopwatch.GetTimestamp"/> units.</summary>
internal static class StopwatchTicks
{
    /// <summary>
    /// <paramref name="span"/> in <see cref="Stopwatch"/> ticks, exact to the tick and without overflow
    /// (128-bit intermediate). <see cref="Stopwatch.GetElapsedTime(long, long)"/> converts back.
    /// </summary>
    public static long From(TimeSpan span) =>
        (long)((Int128)span.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond);
}
