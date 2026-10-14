using System.Diagnostics;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// The panel link's dead-man decisions for one connection, from <see cref="Stopwatch"/> timestamps the
/// caller passes in; it has no timer of its own. Thread-safe: the link's read, ping and dead-man tasks
/// share one.
/// </summary>
internal sealed class PanelDeadMan
{
    private readonly object _lock = new();
    private readonly long _connectedAt;
    private readonly long _pingInterval;
    private readonly long _silenceTimeout;
    private readonly long _grace;
    private readonly TimeSpan _silenceTimeoutSpan;
    private readonly TimeSpan _graceSpan;

    // The later of connect (the startup queries count as the first send) and the last ping sent.
    private long _lastSent;
    private long? _lastHeard;
    // The silence count starts no earlier than this: the end of the grace after connect, moved on by each wake.
    private long _graceEnd;

    public PanelDeadMan(string endpoint, long connectedAt, TimeSpan pingInterval, TimeSpan silenceTimeout, TimeSpan grace)
    {
        _connectedAt = connectedAt;
        _pingInterval = StopwatchTicks.From(pingInterval);
        _silenceTimeout = StopwatchTicks.From(silenceTimeout);
        _grace = StopwatchTicks.From(grace);
        _silenceTimeoutSpan = silenceTimeout;
        _graceSpan = grace;
        _lastSent = connectedAt;
        _graceEnd = connectedAt + _grace;

        // Several checks per ping, so a drop is seen within a quarter ping interval of being due.
        var check = TimeSpan.FromTicks(Math.Min(pingInterval.Ticks, silenceTimeout.Ticks) / 4);
        CheckInterval = check < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : check;
    }

    /// <summary>How often the link runs <see cref="Check"/>.</summary>
    public TimeSpan CheckInterval { get; }

    /// <summary>Whether any line has arrived from the panel on this connection.</summary>
    public bool HasHeard
    {
        get
        {
            lock (_lock)
                return _lastHeard.HasValue;
        }
    }

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
            _lastHeard = Math.Max(_lastHeard ?? long.MinValue, at);
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
    /// The panel's silence is counted from the latest of the last line heard and the grace end.
    /// </summary>
    public Verdict Check(long now, long scheduledAt)
    {
        lock (_lock)
        {
            var countFrom = Math.Max(_graceEnd, _lastHeard ?? long.MinValue);
            if (now - countFrom <= _silenceTimeout)
                return new Verdict(null);

            var timeout = $"timeout {_silenceTimeoutSpan.TotalMilliseconds:0} ms";
            return new Verdict(_lastHeard is { } heard
                ? $"the panel was silent for {Ms(heard, now)} ms ({timeout})"
                : $"the panel was silent for {Ms(_connectedAt, now)} ms since connecting ({timeout} after the {_graceSpan.TotalMilliseconds:0} ms connect grace)");
        }
    }

    private static string Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds.ToString("0");

    /// <param name="DropReason">Why the link should go down, or null to keep it.</param>
    internal readonly record struct Verdict(string? DropReason)
    {
        public bool Drop => DropReason is not null;
    }
}
