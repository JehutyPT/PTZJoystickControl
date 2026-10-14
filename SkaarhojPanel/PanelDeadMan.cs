using System.Diagnostics;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// The panel link's dead-man decisions for one connection, from <see cref="Stopwatch"/> timestamps the
/// caller passes in; it has no timer of its own and logs nothing: the link words what it reports.
/// Thread-safe: the link's read, ping and dead-man tasks share one.
/// </summary>
internal sealed class PanelDeadMan
{
    /// <summary>A check running later than this means the app froze, not the panel. Fixed, whatever the timings.</summary>
    internal static readonly TimeSpan FreezeThreshold = TimeSpan.FromMilliseconds(250);

    private static readonly long FreezeThresholdTicks = StopwatchTicks.From(FreezeThreshold);

    private readonly object _lock = new();
    private readonly long _connectedAt;
    private readonly long _pingInterval;
    private readonly long _silenceTimeout;
    private readonly long _grace;

    // The later of connect (the startup queries count as the first send) and the last ping sent.
    private long _lastSent;
    private long? _lastHeard;
    // The silence count starts no earlier than this: the end of the grace after connect, moved on by each wake.
    private long _graceEnd;
    // The check at which the count last restarted after an app freeze; null before the first.
    private long? _restartAt;
    private bool _heardSinceRestart;
    // Whether the last check ran late: a run of back-to-back late checks is one freeze to log.
    private bool _lastCheckLate;

    public PanelDeadMan(long connectedAt, TimeSpan pingInterval, TimeSpan silenceTimeout, TimeSpan grace)
    {
        _connectedAt = connectedAt;
        _pingInterval = StopwatchTicks.From(pingInterval);
        _silenceTimeout = StopwatchTicks.From(silenceTimeout);
        _grace = StopwatchTicks.From(grace);
        _lastSent = connectedAt;
        _graceEnd = connectedAt + _grace;

        // Several checks per ping, so a drop is seen within a quarter ping interval of being due.
        var check = TimeSpan.FromTicks(Math.Min(pingInterval.Ticks, silenceTimeout.Ticks) / 4);
        CheckInterval = check < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : check;
    }

    /// <summary>How often the link runs <see cref="Check"/>.</summary>
    public TimeSpan CheckInterval { get; }

    /// <summary>When the next <c>ping</c> is due: a ping interval after the later of the last line heard and the last send.</summary>
    public long NextPingDue
    {
        get
        {
            lock (_lock)
                return Math.Max(_lastSent, _lastHeard ?? long.MinValue) + _pingInterval;
        }
    }

    /// <summary>A complete line of any kind arrived from the panel at <paramref name="at"/>.</summary>
    public void LineHeard(long at)
    {
        lock (_lock)
        {
            _lastHeard = Math.Max(_lastHeard ?? long.MinValue, at);
            _heardSinceRestart = true;
        }
    }

    /// <summary>A <c>ping</c> went out at <paramref name="at"/>.</summary>
    public void PingSent(long at)
    {
        lock (_lock)
            _lastSent = Math.Max(_lastSent, at);
    }

    /// <summary>The panel reported waking at <paramref name="at"/>: it stalls as after a connect, so the grace starts again.</summary>
    public void Woke(long at)
    {
        lock (_lock)
            _graceEnd = Math.Max(_graceEnd, at + _grace);
    }

    /// <summary>
    /// The dead-man check the link scheduled for <paramref name="scheduledAt"/>, running at <paramref name="now"/>.
    /// The panel's silence is counted from the latest of the last line heard, the grace end and the last
    /// restart. A check more than <see cref="FreezeThreshold"/> late means the app froze, so the panel had no
    /// chance to answer: the count restarts from it, but only if the panel has been heard since the last
    /// restart (or there was none), so continued app slowness cannot hide a panel that is gone.
    /// </summary>
    public Verdict Check(long now, long scheduledAt)
    {
        lock (_lock)
        {
            var frozen = now - scheduledAt > FreezeThresholdTicks;
            if (frozen && (_restartAt is null || _heardSinceRestart))
            {
                _restartAt = now;
                _heardSinceRestart = false;
            }

            TimeSpan? stall = frozen && !_lastCheckLate ? Stopwatch.GetElapsedTime(scheduledAt, now) : null;
            _lastCheckLate = frozen;

            var countFrom = Math.Max(Math.Max(_graceEnd, _lastHeard ?? long.MinValue), _restartAt ?? long.MinValue);
            if (now - countFrom <= _silenceTimeout)
                return new Verdict(null, false, stall);

            return new Verdict(Stopwatch.GetElapsedTime(_lastHeard ?? _connectedAt, now), _lastHeard is null, stall);
        }
    }

    /// <param name="Silence">
    /// Set when the link should go down: how long the panel has been silent, since its last line or, when
    /// <paramref name="NothingHeard"/>, since connecting. Null to keep the link.
    /// </param>
    /// <param name="NothingHeard">No line has arrived from the panel on this connection.</param>
    /// <param name="Stall">How late this check ran, when it is the first late check of a series (one <c>WARN</c> each); otherwise null.</param>
    internal readonly record struct Verdict(TimeSpan? Silence, bool NothingHeard, TimeSpan? Stall);
}
