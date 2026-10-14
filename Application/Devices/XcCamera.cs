using PtzJoystickControl.Core.Devices;
using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace PtzJoystickControl.Application.Devices;

/// <summary>
/// A camera driven over Canon's XC protocol (HTTP). Pan and tilt calls only record the wanted
/// state; one background loop per camera sends it as sessionless combined <c>control.cgi</c>
/// requests, one in flight at a time, newest wanted state first.
/// </summary>
public class XcCamera : XcCameraBase, IDisposable
{
    // XC pan/tilt speed range, in hundredths of a degree per second.
    internal const int SpeedMin = 10, SpeedMax = 10000;

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
    // Bumped on every wanted change; the loop sends while the version it last sent differs.
    private long _wantedVersion;
    private long _sentVersion;
    // The stop being repeated, from an axis entering stop (or Stop()) until it was sent
    // StopRepeatCount + 1 times and one send succeeded, or a move on both axes superseded it.
    // _stopId tells a send of an earlier stop from one of the current stop.
    private bool _stopActive;
    private long _stopId;
    private int _stopSends;
    private bool _stopSucceeded;
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
                return _pan != PanDir.Stop || _tilt != TiltDir.Stop;
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
            OnWantedChangedLocked(enteredStop);
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
            OnWantedChangedLocked(enteredStop);
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
            OnWantedChangedLocked(enteredStop: true);
        }
    }

    private void OnWantedChangedLocked(bool enteredStop)
    {
        _wantedVersion++;
        if (enteredStop)
        {
            _stopActive = true;
            _stopId++;
            _stopSends = 0;
            _stopSucceeded = false;
        }
        else if (_pan != PanDir.Stop && _tilt != TiltDir.Stop)
        {
            // Nothing stopped is left to deliver: the move supersedes the stop.
            _stopActive = false;
        }
        WakeLocked();
    }

    // Not supported on an XC camera (yet): each sends nothing and is logged, at most once per
    // operation per UnsupportedLogInterval, since a controller may call it on every input.
    public override void Zoom(double zoomSpeed, ZoomDir zoomDir) => Unsupported(nameof(Zoom));
    public override void Focus(double focusSpeed, FocusDir focusDir) => Unsupported(nameof(Focus));
    public override void FocusMode(FocusMode focusMode) => Unsupported(nameof(FocusMode));
    public override void FocusLock(FocusLock focusLock) => Unsupported(nameof(FocusLock));
    public override void Power(Power power) => Unsupported(nameof(Power));
    public override void Preset(Preset preset, byte presetNumber) => Unsupported(nameof(Preset));
    public override void PresetRecallSpeed(byte speed) => Unsupported(nameof(PresetRecallSpeed));

    private void Unsupported(string operation)
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            if (_unsupportedLoggedAt.TryGetValue(operation, out var loggedAt) && now - loggedAt < Ticks(UnsupportedLogInterval))
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

    // A request the loop sends: a control request (StopId is the stop it repeats, or 0 for none) or a
    // probe (StopId 0), to the address and port current at EndpointVersion.
    private sealed record Request(Uri Uri, long StopId, long EndpointVersion);

    // The request to send now, or null and how long to wait before looking again (infinite with no
    // address; a wake ends the wait early). Only the newest wanted state is ever sent, and a probe
    // only while no control request is wanted.
    private Request? NextRequestLocked(long now, out TimeSpan wait)
    {
        wait = Timeout.InfiniteTimeSpan;
        if (Address.Length == 0)
            return null;

        // Gaps count from the previous request's completion, so request starts are further apart. A
        // stop's first send waits only the normal gap; its repeats and retries wait the repeat interval.
        // A probe waits until no request has completed for the probe interval.
        var control = _wantedVersion != _sentVersion || _stopActive;
        var gap = control
            ? _stopActive && _stopSends > 0 ? StopRepeatInterval : MinRequestGap
            : _probeNow ? MinRequestGap : _probeInterval;
        var due = _lastCompletedAt + Ticks(gap);
        if (now < due)
        {
            wait = WaitUntil(due, now);
            return null;
        }

        // Built before any state changes, so a failure here leaves the request wanted.
        var uri = control ? ControlUri() : ProbeUri();
        _probeNow = false;
        if (!control)
            return new Request(uri, 0, _endpointVersion);
        _sentVersion = _wantedVersion;
        if (!_stopActive)
            return new Request(uri, 0, _endpointVersion);
        _stopSends++;
        return new Request(uri, _stopId, _endpointVersion);
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    // Rounded up to whole milliseconds: a wait shorter than the timer can measure would spin.
    private static TimeSpan WaitUntil(long due, long now) =>
        TimeSpan.FromMilliseconds(Math.Ceiling((due - now) * 1000.0 / Stopwatch.Frequency));

    // Each axis's speed goes before its direction. A stopped axis sends the slowest speed.
    private Uri ControlUri() => new(string.Create(CultureInfo.InvariantCulture,
        $"http://{Address}:{Port}{ControlPath}?c.1.pan.speed.dir={_panSpeed}&c.1.pan={PanValue(_pan)}&c.1.tilt.speed.dir={_tiltSpeed}&c.1.tilt={TiltValue(_tilt)}"));

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

    // Ends a stop once it was sent often enough and succeeded, and updates Connected. Logs a failure
    // when a run of failures starts or its reason changes, the first success after one, and each
    // Connected change; never once per request.
    private void OnCompleted(Request request, string? failure)
    {
        string? warning = null;
        string? information = null;
        var connectedChanged = false;
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            _lastCompletedAt = now;
            if (_stopActive && request.StopId == _stopId)
            {
                _stopSucceeded |= failure == null;
                if (_stopSucceeded && _stopSends > StopRepeatCount)
                    _stopActive = false;
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
                if (_connected && now - _lastSuccessAt >= Ticks(_probeInterval))
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
