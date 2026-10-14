using PtzJoystickControl.Core.Devices;
using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace PtzJoystickControl.Application.Devices;

/// <summary>
/// A camera driven over Canon's XC protocol (HTTP). Pan, tilt, zoom, focus and preset recall calls
/// only record the wanted state; one background loop per camera sends it as sessionless
/// <c>control.cgi</c> requests, one in flight at a time, newest wanted state first. A request
/// combines every axis group that changed; a preset recall goes in a request of its own.
/// </summary>
public class XcCamera : XcCameraBase, IDisposable
{
    // XC pan/tilt speed range, in hundredths of a degree per second.
    internal const int SpeedMin = 10, SpeedMax = 10000;

    // The CR-N500's zoom speed range (c.1.zoom.speed.dir) and manual-focus speed range
    // (c.1.focus.speed), from appendix A of Canon's XC protocol spec. Other XC cameras differ; the
    // on-site checklist confirms them with info.cgi.
    internal const int ZoomSpeedMin = 0, ZoomSpeedMax = 15;
    internal const int FocusSpeedMin = 0, FocusSpeedMax = 2;
    // The CR-N500's camera preset numbers (p, 1..p.count) and preset movement speeds (p.ptzspeed),
    // also from appendix A.
    internal const int PresetMin = 1, PresetMax = 100;
    internal const int PresetSpeedMin = 1, PresetSpeedMax = 100;

    // The app's Preset Recall Speed range (VISCA's), mapped proportionally onto p.ptzspeed.
    private const int RecallSpeedMin = 1, RecallSpeedMax = 24;

    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMilliseconds(500);

    // An idle camera is probed when no request has completed for this long, and a failure turns it
    // unreachable when no request has succeeded for this long.
    internal static readonly TimeSpan DefaultProbeInterval = TimeSpan.FromSeconds(3);

    // Canon recommends at least 30 ms between control requests (spec section 3.2.3.1). Tune on site.
    internal static readonly TimeSpan MinRequestGap = TimeSpan.FromMilliseconds(30);

    // Like the VISCA stop repeat: a stop (an axis entering stop, or Stop()) is sent once and then
    // StopRepeatCount more times, each StopRepeatInterval after the previous request completed, and
    // then again until one of those sends succeeds. Every send carries the wanted state at send time,
    // so a move made within the window goes out instead of a stale stop.
    internal const int StopRepeatCount = 2;
    internal static readonly TimeSpan StopRepeatInterval = TimeSpan.FromMilliseconds(50);

    // After an unexpected error the loop pauses this long, so a recurring one cannot flood the log.
    private static readonly TimeSpan UnexpectedErrorPause = TimeSpan.FromSeconds(1);

    // An unsupported operation is logged at most once per this long, per operation.
    private static readonly TimeSpan UnsupportedLogInterval = TimeSpan.FromMinutes(1);

    private const string ControlPath = "/-wvhttp-01-/control.cgi";
    // The probe: a sessionless, read-only info request for one item group, which needs no control right.
    private const string ProbePathAndQuery = "/-wvhttp-01-/info.cgi?item=s";
    private const string LivescopeStatusHeader = "livescope-status";

    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _probeInterval;

    // Guards the wanted state and everything the loop decides on. Never held across an await.
    private readonly object _lock = new();
    // Released (under _lock) when there is something new for the loop to look at.
    private readonly SemaphoreSlim _wake = new(0, 1);
    // Cancelled by Dispose to end the loop. Never disposed itself, so reading its Token stays safe.
    private readonly CancellationTokenSource disposeCancellation = new();
    // One client, so one keep-alive connection, per camera. Cameras are on the local network: a
    // system proxy would only add a hop that can fail.
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false })
    {
        // Each request has its own deadline (_requestTimeout).
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private Task? _loop;
    private bool _disposed;

    private PanDir _pan = PanDir.Stop;
    private int _panSpeed = SpeedMin;
    private TiltDir _tilt = TiltDir.Stop;
    private int _tiltSpeed = SpeedMin;
    private ZoomDir _zoom = ZoomDir.Stop;
    private int _zoomSpeed = ZoomSpeedMin;
    private FocusDir _focus = FocusDir.Stop;
    private int _focusSpeed = FocusSpeedMin;
    // The axes a request carries together when one of them changed: pan and tilt as one pair, zoom,
    // and focus.
    private readonly AxisGroup _panTiltGroup = new();
    private readonly AxisGroup _zoomGroup = new();
    private readonly AxisGroup _focusGroup = new();
    private readonly AxisGroup[] _groups;
    // The camera preset to recall, with its p.ptzspeed (null for none), in a request of its own once no
    // stop is pending; null when no recall is wanted. Sent once, never retried: a newer recall
    // replaces it, and Stop() drops it.
    private (int Preset, int? PresetSpeed)? _recall;
    // The p.ptzspeed (PresetSpeedMin..PresetSpeedMax) recalls carry, mapped from the last Preset
    // Recall Speed. Null until one is set, so a recall moves at the speeds saved with the camera
    // preset. Not saved.
    private int? _presetSpeed;
    // Stopwatch timestamp of the last request's completion; long ago until the first.
    private long _lastCompletedAt = long.MinValue / 4;
    // Why the current run of failed requests fails; null while requests succeed. Logged when it changes.
    private string? _failure;
    // Whether the camera answers: true once a request to the current address and port succeeded, false
    // after one failed with none succeeding within the probe interval, or the address or port changed.
    private bool _connected;
    // Stopwatch timestamp of the last request to the current address and port that succeeded.
    private long _lastSuccessAt = long.MinValue / 4;
    // Bumped when the address or port changes: the outcome of a request sent before says nothing
    // about the camera now configured.
    private long _endpointVersion;
    // Set by an address or port change: probe as soon as the gap allows, not after the probe interval.
    private bool _probeNow;
    // Stopwatch timestamp each unsupported operation was last logged at.
    private readonly Dictionary<string, long> _unsupportedLoggedAt = new();

    public XcCamera(string name) : this(name, DefaultRequestTimeout, DefaultProbeInterval) { }

    internal XcCamera(string name, TimeSpan requestTimeout, TimeSpan probeInterval) : base(name)
    {
        _requestTimeout = requestTimeout;
        _probeInterval = probeInterval;
        _groups = new[] { _panTiltGroup, _zoomGroup, _focusGroup };
    }

    /// <summary>
    /// True once a request (control or probe) to the current address and port succeeded; false after
    /// one failed with none succeeding within the probe interval, and after the address or port changed.
    /// Changes are raised from the camera's send loop (or the setter's thread), not the UI thread.
    /// </summary>
    public override bool Connected
    {
        get
        {
            lock (_lock)
                return _connected;
        }
    }

    public override bool IsMoving
    {
        get
        {
            lock (_lock)
                return _pan != PanDir.Stop || _tilt != TiltDir.Stop || _zoom != ZoomDir.Stop || _focus != FocusDir.Stop;
        }
    }

    // The next request goes to the new address and port, with a probe as soon as nothing else is
    // wanted. A request in flight to the old one finishes or times out, and its outcome is ignored.
    protected override void OnEndpointChanged()
    {
        string? information = null;
        lock (_lock)
        {
            _endpointVersion++;
            _probeNow = true;
            _failure = null;
            if (_connected)
            {
                _connected = false;
                information = $"Camera {Name}: the XC camera's address or port changed; not connected until it answers";
            }
            WakeLocked();
        }

        if (information != null)
        {
            Trace.TraceInformation(information);
            RaiseConnectedChanged();
        }
    }

    /// <summary>
    /// Maps a speed fraction onto an XC speed: round(10 + f · 9990), so fraction 0 still moves at
    /// the slowest speed. A fraction outside 0..1 (or NaN) is clamped.
    /// </summary>
    internal static int ToXcSpeed(double fraction)
    {
        if (double.IsNaN(fraction)) fraction = 0;
        fraction = Math.Clamp(fraction, 0, 1);
        return (int)Math.Round(SpeedMin + fraction * (SpeedMax - SpeedMin), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Maps a speed fraction onto one of the camera's speed steps from <paramref name="min"/> to
    /// <paramref name="max"/> as the VISCA camera does: min + floor(f · (max − min) + 1e-6), so
    /// fraction 0 still moves at the slowest step. A fraction outside 0..1 (or NaN) is clamped.
    /// </summary>
    internal static int ToXcStep(double fraction, int min, int max)
    {
        if (double.IsNaN(fraction)) fraction = 0;
        fraction = Math.Clamp(fraction, 0, 1);
        var step = min + Math.Floor(fraction * (max - min) + 1e-6);
        return (int)Math.Clamp(step, min, max);
    }

    /// <summary>
    /// Maps the app's Preset Recall Speed (1..24; clamped) proportionally onto p.ptzspeed:
    /// round(1 + (v − 1) · 99 / 23), so 1 is 1, 12 is 48 and 24 is 100.
    /// </summary>
    internal static int ToXcPresetSpeed(int recallSpeed)
    {
        recallSpeed = Math.Clamp(recallSpeed, RecallSpeedMin, RecallSpeedMax);
        var speed = PresetSpeedMin + (recallSpeed - RecallSpeedMin) * (double)(PresetSpeedMax - PresetSpeedMin) / (RecallSpeedMax - RecallSpeedMin);
        return (int)Math.Round(speed, MidpointRounding.AwayFromZero);
    }

    public override void Pan(double panSpeed, PanDir panDir)
    {
        var speed = panDir == PanDir.Stop ? SpeedMin : ToXcSpeed(panSpeed);
        lock (_lock)
        {
            if (_pan == panDir && _panSpeed == speed)
                return;
            var enteredStop = _pan != PanDir.Stop && panDir == PanDir.Stop;
            _pan = panDir;
            _panSpeed = speed;
            OnWantedChangedLocked(_panTiltGroup, enteredStop, supersedesStop: _pan != PanDir.Stop && _tilt != TiltDir.Stop);
        }
    }

    public override void Tilt(double tiltSpeed, TiltDir tiltDir)
    {
        var speed = tiltDir == TiltDir.Stop ? SpeedMin : ToXcSpeed(tiltSpeed);
        lock (_lock)
        {
            if (_tilt == tiltDir && _tiltSpeed == speed)
                return;
            var enteredStop = _tilt != TiltDir.Stop && tiltDir == TiltDir.Stop;
            _tilt = tiltDir;
            _tiltSpeed = speed;
            OnWantedChangedLocked(_panTiltGroup, enteredStop, supersedesStop: _pan != PanDir.Stop && _tilt != TiltDir.Stop);
        }
    }

    public override void Zoom(double zoomSpeed, ZoomDir zoomDir)
    {
        var speed = zoomDir == ZoomDir.Stop ? ZoomSpeedMin : ToXcStep(zoomSpeed, ZoomSpeedMin, ZoomSpeedMax);
        lock (_lock)
        {
            if (_zoom == zoomDir && _zoomSpeed == speed)
                return;
            var enteredStop = _zoom != ZoomDir.Stop && zoomDir == ZoomDir.Stop;
            _zoom = zoomDir;
            _zoomSpeed = speed;
            OnWantedChangedLocked(_zoomGroup, enteredStop, supersedesStop: zoomDir != ZoomDir.Stop);
        }
    }

    public override void Focus(double focusSpeed, FocusDir focusDir)
    {
        var speed = focusDir == FocusDir.Stop ? FocusSpeedMin : ToXcStep(focusSpeed, FocusSpeedMin, FocusSpeedMax);
        lock (_lock)
        {
            if (_focus == focusDir && _focusSpeed == speed)
                return;
            var enteredStop = _focus != FocusDir.Stop && focusDir == FocusDir.Stop;
            _focus = focusDir;
            _focusSpeed = speed;
            OnWantedChangedLocked(_focusGroup, enteredStop, supersedesStop: focusDir != FocusDir.Stop);
        }
    }

    public override void Stop()
    {
        // Bypasses change detection: the camera may still be moving even if the last request we
        // sent was a stop (it may have failed).
        lock (_lock)
        {
            _pan = PanDir.Stop;
            _panSpeed = SpeedMin;
            _tilt = TiltDir.Stop;
            _tiltSpeed = SpeedMin;
            _zoom = ZoomDir.Stop;
            _zoomSpeed = ZoomSpeedMin;
            _focus = FocusDir.Stop;
            _focusSpeed = FocusSpeedMin;
            // The stop is the last instruction the camera gets, as in VISCA's send order (a recall
            // goes out at once there, and the stop after it).
            _recall = null;
            foreach (var group in _groups)
                OnWantedChangedLocked(group, enteredStop: true, supersedesStop: false);
        }
    }

    // Records that the group's wanted state changed, so the next control request carries it. An axis
    // entering stop starts the group's stop; a move that leaves nothing stopped to deliver in the
    // group supersedes it.
    private void OnWantedChangedLocked(AxisGroup group, bool enteredStop, bool supersedesStop)
    {
        group.WantedVersion++;
        if (enteredStop)
            group.StartStop();
        else if (supersedesStop)
            group.StopActive = false;
        WakeLocked();
    }

    // Not supported on an XC camera (Companion's own Canon module covers them): each sends nothing and
    // is logged, at most once per operation per UnsupportedLogInterval, since a controller may call it
    // on every input.
    public override void FocusMode(FocusMode focusMode) => Unsupported(nameof(FocusMode));
    public override void FocusLock(FocusLock focusLock) => Unsupported(nameof(FocusLock));
    public override void Power(Power power) => Unsupported(nameof(Power));

    // Sends nothing by itself: every later recall carries it as p.ptzspeed.
    public override void PresetRecallSpeed(byte speed)
    {
        var presetSpeed = ToXcPresetSpeed(speed);
        lock (_lock)
            _presetSpeed = presetSpeed;
    }

    // Recalls the app's 0-based preset presetNumber as the camera preset presetNumber + 1. Saving or
    // resetting a camera preset is not supported on an XC camera.
    public override void Preset(Preset preset, byte presetNumber)
    {
        if (preset != Core.Devices.Preset.Recall)
        {
            Unsupported($"{nameof(Preset)} {preset.ToString().ToLowerInvariant()}");
            return;
        }

        var cameraPreset = presetNumber + 1;
        if (cameraPreset < PresetMin || cameraPreset > PresetMax)
        {
            Trace.TraceWarning($"Camera {Name}: preset {cameraPreset} is outside the XC camera's presets {PresetMin}..{PresetMax}; nothing sent");
            return;
        }

        lock (_lock)
        {
            _recall = (cameraPreset, _presetSpeed);
            WakeLocked();
        }
    }

    private void Unsupported(string operation)
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            if (_unsupportedLoggedAt.TryGetValue(operation, out var loggedAt) && now - loggedAt < StopwatchTicks(UnsupportedLogInterval))
                return;
            _unsupportedLoggedAt[operation] = now;
        }
        Trace.TraceInformation($"Camera {Name}: {operation} is not supported on an XC camera; nothing sent");
    }

    /// <summary>Ends the send loop and closes the connection. Later calls are recorded and send nothing.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        disposeCancellation.Cancel();
        _http.Dispose();
    }

    // Starts the loop on first use, else wakes it. Called with _lock held. A camera with no address
    // has nothing to send to, so it starts nothing.
    private void WakeLocked()
    {
        if (_disposed || Address.Length == 0)
            return;

        if (_loop == null)
        {
            _loop = Task.Run(() => RunAsync(disposeCancellation.Token));
            return;
        }

        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    // The camera's only background task. Runs until disposed; nothing may escape it.
    private async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                Request? request;
                TimeSpan wait;
                lock (_lock)
                    request = NextRequestLocked(Stopwatch.GetTimestamp(), out wait);
                if (request == null)
                {
                    await _wake.WaitAsync(wait, stopping).ConfigureAwait(false);
                    continue;
                }

                var failure = await SendAsync(request.Uri, stopping).ConfigureAwait(false);
                OnCompleted(request, failure);
            }
            catch (Exception) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Should not happen; keep the camera's loop alive rather than lose the camera for the
                // rest of the show.
                Trace.TraceError($"Camera {Name}: unexpected error in the XC send loop: {e}");
                try
                {
                    await Task.Delay(UnexpectedErrorPause, stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    // The bookkeeping of one group of axes a control request carries together (pan and tilt, zoom, or
    // focus): whether its wanted state changed since it was last sent, and the stop it is repeating,
    // from an axis entering stop (or Stop()) until it was sent StopRepeatCount + 1 times and one send
    // succeeded, or a move superseded it. StopId tells a send of an earlier stop from one of the
    // current stop.
    private sealed class AxisGroup
    {
        // Bumped on every wanted change; the group is sent while the version it last sent differs.
        public long WantedVersion;
        public long SentVersion;
        public bool StopActive;
        public long StopId;
        public int StopSends;
        public bool StopSucceeded;

        // Whether the next control request carries the group: a change not sent yet, or a stop still
        // being repeated or retried.
        public bool Pending => WantedVersion != SentVersion || StopActive;

        public void StartStop()
        {
            StopActive = true;
            StopId++;
            StopSends = 0;
            StopSucceeded = false;
        }
    }

    // A request the loop sends: a control request (moves, stops or a preset recall) or a probe, to the
    // address and port current at EndpointVersion. StopIds, one per axis group, is the stop the request
    // repeats in that group, or 0 for none.
    private sealed record Request(Uri Uri, long[] StopIds, long EndpointVersion);

    // The request to send now, or null and how long to wait before looking again (infinite with no
    // address; a wake ends the wait early). Only the newest wanted state is ever sent, and a probe
    // only while no control request is wanted.
    private Request? NextRequestLocked(long now, out TimeSpan wait)
    {
        wait = Timeout.InfiniteTimeSpan;
        if (Address.Length == 0)
            return null;

        // A recall waits while any stop is pending, so every send of a stop comes before it; then it
        // goes out ahead of pending moves, so continuous movement never holds it back.
        var recall = _recall != null && !_groups.Any(g => g.StopActive);
        var control = recall || _groups.Any(g => g.Pending);

        // Gaps count from the previous request's completion, so request starts are further apart. A
        // stop's first send waits only the normal gap; while any stop is being repeated or retried,
        // every request waits the repeat interval, so no stop's sends come closer together than it.
        // A probe waits until no request has completed for the probe interval.
        var gap = control
            ? _groups.Any(g => g.StopActive && g.StopSends > 0) ? StopRepeatInterval : MinRequestGap
            : _probeNow ? MinRequestGap : _probeInterval;
        var due = _lastCompletedAt + StopwatchTicks(gap);
        if (now < due)
        {
            wait = WaitUntil(due, now);
            return null;
        }

        // Built before any state changes, so a failure here leaves the request wanted.
        var uri = recall ? RecallUri(_recall!.Value) : control ? ControlUri() : ProbeUri();
        _probeNow = false;
        var stopIds = new long[_groups.Length];
        if (recall)
        {
            _recall = null;
            return new Request(uri, stopIds, _endpointVersion);
        }
        if (!control)
            return new Request(uri, stopIds, _endpointVersion);
        for (var i = 0; i < _groups.Length; i++)
        {
            var group = _groups[i];
            if (!group.Pending)
                continue;
            group.SentVersion = group.WantedVersion;
            if (!group.StopActive)
                continue;
            group.StopSends++;
            stopIds[i] = group.StopId;
        }
        return new Request(uri, stopIds, _endpointVersion);
    }

    private static long StopwatchTicks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    // Rounded up to whole milliseconds: a wait shorter than the timer can measure would spin.
    private static TimeSpan WaitUntil(long due, long now) =>
        TimeSpan.FromMilliseconds(Math.Ceiling((due - now) * 1000.0 / Stopwatch.Frequency));

    // Carries each pending axis group, in the order pan/tilt, zoom, focus; a group not pending is left
    // out. Each axis's speed goes before its direction. A stopped axis sends the slowest speed.
    private Uri ControlUri()
    {
        var parameters = new List<string>();
        if (_panTiltGroup.Pending)
            parameters.Add(string.Create(CultureInfo.InvariantCulture,
                $"c.1.pan.speed.dir={_panSpeed}&c.1.pan={PanValue(_pan)}&c.1.tilt.speed.dir={_tiltSpeed}&c.1.tilt={TiltValue(_tilt)}"));
        if (_zoomGroup.Pending)
            parameters.Add(string.Create(CultureInfo.InvariantCulture,
                $"c.1.zoom.speed.dir={_zoomSpeed}&c.1.zoom={ZoomValue(_zoom)}"));
        if (_focusGroup.Pending)
            parameters.Add(string.Create(CultureInfo.InvariantCulture,
                $"c.1.focus.speed={_focusSpeed}&c.1.focus.action={FocusValue(_focus)}"));
        return new(string.Create(CultureInfo.InvariantCulture, $"http://{Address}:{Port}{ControlPath}?{string.Join('&', parameters)}"));
    }

    // A recall carries only the camera preset and, once a recall speed was set, its p.ptzspeed.
    private Uri RecallUri((int Preset, int? PresetSpeed) recall) => new(recall.PresetSpeed is { } speed
        ? string.Create(CultureInfo.InvariantCulture, $"http://{Address}:{Port}{ControlPath}?p={recall.Preset}&p.ptzspeed={speed}")
        : string.Create(CultureInfo.InvariantCulture, $"http://{Address}:{Port}{ControlPath}?p={recall.Preset}"));

    private Uri ProbeUri() => new(string.Create(CultureInfo.InvariantCulture, $"http://{Address}:{Port}{ProbePathAndQuery}"));

    private static string PanValue(PanDir dir) => dir switch
    {
        PanDir.Left => "left",
        PanDir.Right => "right",
        _ => "stop",
    };

    private static string TiltValue(TiltDir dir) => dir switch
    {
        TiltDir.Up => "up",
        TiltDir.Down => "down",
        _ => "stop",
    };

    private static string ZoomValue(ZoomDir dir) => dir switch
    {
        ZoomDir.Tele => "tele",
        ZoomDir.Wide => "wide",
        _ => "stop",
    };

    private static string FocusValue(FocusDir dir) => dir switch
    {
        FocusDir.Far => "far",
        FocusDir.Near => "near",
        _ => "stop",
    };

    // Sends one request; returns null when it succeeded, else why it failed. Throws only when stopping
    // (or on an unexpected error, which the loop logs).
    private async Task<string?> SendAsync(Uri uri, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            using var response = await _http.GetAsync(uri, timeout.Token).ConfigureAwait(false);
            return Failure(response);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            return $"no answer within {_requestTimeout.TotalMilliseconds:0} ms";
        }
        catch (HttpRequestException e) when (!stopping.IsCancellationRequested)
        {
            return e.GetBaseException().Message;
        }
    }

    // XC answers errors with HTTP 200 too: only livescope-status 0 means the camera did as asked.
    private static string? Failure(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            return $"HTTP {(int)response.StatusCode}";

        if (!response.Headers.TryGetValues(LivescopeStatusHeader, out var values))
            return "no livescope-status header";

        var status = values.FirstOrDefault()?.Trim();
        return status switch
        {
            "0" => null,
            "301" => "no camera control right: another client may hold control",
            _ => $"livescope-status {status}",
        };
    }

    // Ends each group's stop once it was sent often enough and succeeded, and updates Connected. Logs
    // a failure when a run of failures starts or its reason changes, the first success after one, and
    // each Connected change; never once per request.
    private void OnCompleted(Request request, string? failure)
    {
        string? warning = null;
        string? information = null;
        var connectedChanged = false;
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            _lastCompletedAt = now;
            for (var i = 0; i < _groups.Length; i++)
            {
                var group = _groups[i];
                if (!group.StopActive || request.StopIds[i] != group.StopId)
                    continue;
                group.StopSucceeded |= failure == null;
                if (group.StopSucceeded && group.StopSends > StopRepeatCount)
                    group.StopActive = false;
            }

            if (request.EndpointVersion != _endpointVersion)
            {
                // Sent to an address or port no longer configured: says nothing about the camera now.
            }
            else if (failure == null)
            {
                _lastSuccessAt = now;
                connectedChanged = !_connected;
                _connected = true;
                // One line for the recovery, whether or not it also connects the camera.
                if (_failure != null)
                    information = $"Camera {Name}: requests to the XC camera at {Address}:{Port} succeed again{(connectedChanged ? "; it is connected" : "")}";
                else if (connectedChanged)
                    information = $"Camera {Name}: the XC camera at {Address}:{Port} is connected";
                _failure = null;
            }
            else
            {
                if (failure != _failure)
                {
                    warning = $"Camera {Name}: request to the XC camera at {Address}:{Port} failed: {failure}";
                    _failure = failure;
                }
                if (_connected && now - _lastSuccessAt >= StopwatchTicks(_probeInterval))
                {
                    _connected = false;
                    connectedChanged = true;
                    information = $"Camera {Name}: the XC camera at {Address}:{Port} is not reachable";
                }
            }
        }

        if (warning != null)
            Trace.TraceWarning(warning);
        if (information != null)
            Trace.TraceInformation(information);
        if (connectedChanged)
            RaiseConnectedChanged();
    }

    // Raised outside _lock (a subscriber reads the camera's state); a failing subscriber is logged,
    // never thrown into the send loop or a setter's caller.
    private void RaiseConnectedChanged()
    {
        try
        {
            NotifyPropertyChanged(nameof(Connected));
        }
        catch (Exception e)
        {
            Trace.TraceError($"Camera {Name}: a {nameof(Connected)} subscriber failed: {e}");
        }
    }
}
