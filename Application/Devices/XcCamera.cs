using PtzJoystickControl.Core.Devices;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PtzJoystickControl.Application.Devices;

/// <summary>
/// A camera driven over Canon's XC protocol (HTTP). Pan, tilt, zoom, focus and preset recall calls
/// only record the wanted state; one background loop per camera sends it as sessionless
/// <c>control.cgi</c> requests, one in flight at a time, newest wanted state first. A request
/// combines every axis group that changed; a preset recall goes in a request of its own, the next one
/// sent, and takes the place of the pending stops, which are restored if it fails. With a username
/// set, a request the camera answers with HTTP 401 is repeated at once with Basic or Digest
/// credentials, and later requests carry them up front. Once disposed, the camera ignores commands,
/// sends only its pending stops, and then closes its connection (<see cref="Dispose"/>).
/// </summary>
public class XcCamera : XcCameraBase, IDisposable
{
    // XC pan/tilt speed range, in hundredths of a degree per second.
    internal const int SpeedMin = 10, SpeedMax = 10000;

    // The CR-N500's zoom speed range (c.1.zoom.speed.dir) and manual-focus speed range
    // (c.1.focus.speed), from appendix A of Canon's XC protocol spec. Used until the camera reports
    // its own in info.cgi (c.1.zoom.speed.min/max, c.1.focus.speed.min/max), and when it reports none
    // usable: firmware 1.5.2 reports zoom 0..127 and focus 0..63 (#60).
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
    // then again until one of those sends succeeds or the stop is given up (DefaultStopGiveUpAfter).
    // Every send carries the wanted state at send time, so a move made within the window goes out
    // instead of a stale stop.
    internal const int StopRepeatCount = 2;
    internal static readonly TimeSpan StopRepeatInterval = TimeSpan.FromMilliseconds(50);

    // A stop none of whose sends succeeded is given up, with one ERROR, when a send of it fails once it
    // was sent StopRepeatCount + 1 times and this long has passed since it started (or since a failed
    // recall restored it). Nothing more of it is sent and the idle probe runs again, but the camera may
    // keep moving until that axis is moved and released, stopped again, recalled to a preset, or at its
    // limit. 5 s exceeds the probe interval plus two request timeouts plus the stop repeat interval
    // (4.05 s), so the camera shows not reachable first. It still allows about 9 sends to a camera that
    // does not answer, enough to ride out a transient; a stop failing for longer points to something
    // persistent. Tune on site against how long the link takes to come back.
    internal static readonly TimeSpan DefaultStopGiveUpAfter = TimeSpan.FromSeconds(5);

    // After an unexpected error the loop pauses this long, so a recurring one cannot flood the log.
    private static readonly TimeSpan UnexpectedErrorPause = TimeSpan.FromSeconds(1);

    // An unsupported operation is logged at most once per this long, per operation.
    private static readonly TimeSpan UnsupportedLogInterval = TimeSpan.FromMinutes(1);

    // A recall of a camera preset outside PresetMin..PresetMax is logged at most once per this long, per
    // camera preset.
    private static readonly TimeSpan OutOfRangePresetLogInterval = TimeSpan.FromMinutes(1);

    // A login failure (an HTTP 401 the camera cannot get past) is logged at most once per this long,
    // until the address, port or login changes.
    private static readonly TimeSpan LoginFailureLogInterval = TimeSpan.FromMinutes(1);

    private const string LoginRequired = "the camera requires a login; set username and password";
    private const string LoginRefused = "the camera refused the username and password";
    private const string LoginUnsupported = "the camera asks for a login the app does not support";

    private const string ControlPath = "/-wvhttp-01-/control.cgi";
    // The probe: a sessionless, read-only info request for one item group, which needs no control right.
    private const string ProbePathAndQuery = "/-wvhttp-01-/info.cgi?item=s";
    // The whole info.cgi, read by the first probe after a connect for the camera's speed ranges.
    private const string InfoPath = "/-wvhttp-01-/info.cgi";
    private const string LivescopeStatusHeader = "livescope-status";

    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _probeInterval;
    private readonly TimeSpan _stopGiveUpAfter;

    // Guards the wanted state and everything the loop decides on. Never held across an await.
    private readonly object _lock = new();
    // Released (under _lock) when there is something new for the loop to look at.
    private readonly SemaphoreSlim _wake = new(0, 1);
    // Cancelled when the camera is released, to end the loop. Never disposed itself, so reading its
    // Token stays safe.
    private readonly CancellationTokenSource disposeCancellation = new();
    // One client, so one keep-alive connection, per camera. Cameras are on the local network: a
    // system proxy would only add a hop that can fail.
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false })
    {
        // Each request has its own deadline (_requestTimeout).
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private Task? _loop;
    // Set by Dispose: from then on every command is ignored, and only the pending stops are sent.
    private bool _disposed;
    // Set once a disposed camera has no stop left to send, by Dispose or by the loop: the loop ends
    // and the connection closes.
    private bool _released;

    private PanDir _pan = PanDir.Stop;
    private int _panSpeed = SpeedMin;
    private TiltDir _tilt = TiltDir.Stop;
    private int _tiltSpeed = SpeedMin;
    private ZoomDir _zoom = ZoomDir.Stop;
    private int _zoomSpeed = ZoomSpeedMin;
    private FocusDir _focus = FocusDir.Stop;
    private int _focusSpeed = FocusSpeedMin;

    // The zoom and focus speed ranges in use: the camera's, once its info.cgi reported them, else the
    // spec's. Reset to the spec's on an address, port or login change.
    private (int Min, int Max) _zoomRange = (ZoomSpeedMin, ZoomSpeedMax);
    private (int Min, int Max) _focusRange = (FocusSpeedMin, FocusSpeedMax);
    // Whether the next probe reads the whole info.cgi for the speed ranges: after an address, port or
    // login change, and after the camera turned unreachable, until the camera answers one.
    private bool _rangesWanted = true;
    // The speed ranges last logged, so a reconnect with the same ones logs nothing.
    private string? _rangesLogged;
    // The axes a request carries together when one of them changed: pan and tilt as one pair, zoom,
    // and focus.
    private readonly AxisGroup _panTiltGroup = new("pan/tilt");
    private readonly AxisGroup _zoomGroup = new("zoom");
    private readonly AxisGroup _focusGroup = new("focus");
    private readonly AxisGroup[] _groups;
    // The pan and tilt the last control request carrying them was sent with: a recall that takes the
    // place of a pan/tilt stop leaves a move the camera was never sent to go out after it.
    private (PanDir Pan, int PanSpeed, TiltDir Tilt, int TiltSpeed) _sentPanTilt = (PanDir.Stop, SpeedMin, TiltDir.Stop, SpeedMin);
    // The camera preset to recall, with its p.ptzspeed (null for none), in a request of its own, the
    // next one sent; null when no recall is wanted. Sent once, never retried: only a newer recall
    // replaces it.
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
    // Bumped when the address, port or login changes: the outcome of a request sent before says
    // nothing about the camera as now configured.
    private long _endpointVersion;
    // Set by an address, port or login change: probe as soon as the gap allows, not after the probe
    // interval.
    private bool _probeNow;
    // The username and password requests carry, as one pair taken when the login changes, so no
    // request carries one of them without the other; null when no username is set.
    private NetworkCredential? _login;
    // The camera's last login challenge, answered up front by every request with the current login to
    // the address and port it came from; null until the camera challenges, after an address, port or
    // login change, and after it challenged with nothing the app can answer. Only the send loop
    // answers it.
    private Challenge? _challenge;
    // Stopwatch timestamp of the last login failure logged; long ago until the first, and after an
    // address, port or login change.
    private long _loginFailureLoggedAt = long.MinValue / 4;
    // Stopwatch timestamp each unsupported operation was last logged at.
    private readonly Dictionary<string, long> _unsupportedLoggedAt = new();
    // Stopwatch timestamp each camera preset outside PresetMin..PresetMax was last logged at.
    private readonly Dictionary<int, long> _outOfRangePresetLoggedAt = new();

    public XcCamera(string name) : this(name, DefaultRequestTimeout, DefaultProbeInterval) { }

    internal XcCamera(string name, TimeSpan requestTimeout, TimeSpan probeInterval) : this(name, requestTimeout, probeInterval, DefaultStopGiveUpAfter) { }

    internal XcCamera(string name, TimeSpan requestTimeout, TimeSpan probeInterval, TimeSpan stopGiveUpAfter) : base(name)
    {
        _requestTimeout = requestTimeout;
        _probeInterval = probeInterval;
        _stopGiveUpAfter = stopGiveUpAfter;
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

    /// <summary>
    /// Why requests to the current address, port and login fail, in the words of the failure's WARN,
    /// whether or not that WARN was logged again; null while they succeed, and after the address or
    /// port changed. Changes are raised as <see cref="Connected"/>'s are.
    /// </summary>
    public override string? FailureReason
    {
        get
        {
            lock (_lock)
                return _failure;
        }
    }

    /// <summary>
    /// True once the disposed camera is released: from then on it sends and logs nothing more, its
    /// send loop ends, if it ever started, and its connection closes. For tests, as
    /// <see cref="Core.Utils.UdpSocket.ListenerFor"/> exposes a VISCA camera's listener.
    /// </summary>
    internal bool Released
    {
        get
        {
            lock (_lock)
                return _released;
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
    // A disposed camera only records the new address or port (the base class).
    protected override void OnEndpointChanged()
    {
        string? information = null;
        bool failureChanged;
        lock (_lock)
        {
            if (_disposed)
                return;
            OnReconfiguredLocked();
            failureChanged = _failure != null;
            _failure = null;
            if (_connected)
            {
                _connected = false;
                information = $"Camera {Name}: the XC camera's address or port changed; not connected until it answers";
            }
        }

        if (information != null)
        {
            Trace.TraceInformation(information);
            RaiseChanged(nameof(Connected));
        }
        if (failureChanged)
            RaiseChanged(nameof(FailureReason));
    }

    // The next request carries the new login, or none, with a probe as soon as nothing else is wanted.
    // A request in flight with the old login finishes or times out, and its outcome is ignored. Unlike
    // an address change, Connected and FailureReason keep their values until a request with the new
    // login settles them. A disposed camera only records the new login (the base class).
    protected override void OnLoginChanged()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _login = Username.Length > 0 ? new NetworkCredential(Username, Password) : null;
            OnReconfiguredLocked();
        }
    }

    // After an address, port or login change: the outcome of a request sent before is ignored, no
    // credentials go up front until the camera challenges again, the next login failure is logged at
    // once, and a probe goes out as soon as the gap allows.
    private void OnReconfiguredLocked()
    {
        _endpointVersion++;
        _probeNow = true;
        _rangesWanted = true;
        _zoomRange = (ZoomSpeedMin, ZoomSpeedMax);
        _focusRange = (FocusSpeedMin, FocusSpeedMax);
        _challenge = null;
        _loginFailureLoggedAt = long.MinValue / 4;
        WakeLocked();
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
            if (_disposed || _pan == panDir && _panSpeed == speed)
                return;
            var enteredStop = _pan != PanDir.Stop && panDir == PanDir.Stop;
            _pan = panDir;
            _panSpeed = speed;
            OnWantedChangedLocked(_panTiltGroup, enteredStop, supersedesStop: _pan != PanDir.Stop && _tilt != TiltDir.Stop, Stopwatch.GetTimestamp());
        }
    }

    public override void Tilt(double tiltSpeed, TiltDir tiltDir)
    {
        var speed = tiltDir == TiltDir.Stop ? SpeedMin : ToXcSpeed(tiltSpeed);
        lock (_lock)
        {
            if (_disposed || _tilt == tiltDir && _tiltSpeed == speed)
                return;
            var enteredStop = _tilt != TiltDir.Stop && tiltDir == TiltDir.Stop;
            _tilt = tiltDir;
            _tiltSpeed = speed;
            OnWantedChangedLocked(_panTiltGroup, enteredStop, supersedesStop: _pan != PanDir.Stop && _tilt != TiltDir.Stop, Stopwatch.GetTimestamp());
        }
    }

    public override void Zoom(double zoomSpeed, ZoomDir zoomDir)
    {
        lock (_lock)
        {
            var speed = zoomDir == ZoomDir.Stop ? _zoomRange.Min : ToXcStep(zoomSpeed, _zoomRange.Min, _zoomRange.Max);
            if (_disposed || _zoom == zoomDir && _zoomSpeed == speed)
                return;
            var enteredStop = _zoom != ZoomDir.Stop && zoomDir == ZoomDir.Stop;
            _zoom = zoomDir;
            _zoomSpeed = speed;
            OnWantedChangedLocked(_zoomGroup, enteredStop, supersedesStop: zoomDir != ZoomDir.Stop, Stopwatch.GetTimestamp());
        }
    }

    public override void Focus(double focusSpeed, FocusDir focusDir)
    {
        lock (_lock)
        {
            var speed = focusDir == FocusDir.Stop ? _focusRange.Min : ToXcStep(focusSpeed, _focusRange.Min, _focusRange.Max);
            if (_disposed || _focus == focusDir && _focusSpeed == speed)
                return;
            var enteredStop = _focus != FocusDir.Stop && focusDir == FocusDir.Stop;
            _focus = focusDir;
            _focusSpeed = speed;
            OnWantedChangedLocked(_focusGroup, enteredStop, supersedesStop: focusDir != FocusDir.Stop, Stopwatch.GetTimestamp());
        }
    }

    public override void Stop()
    {
        // Bypasses change detection: the camera may still be moving even if the last request we
        // sent was a stop (it may have failed).
        lock (_lock)
        {
            if (_disposed)
                return;
            _pan = PanDir.Stop;
            _panSpeed = SpeedMin;
            _tilt = TiltDir.Stop;
            _tiltSpeed = SpeedMin;
            _zoom = ZoomDir.Stop;
            _zoomSpeed = _zoomRange.Min;
            _focus = FocusDir.Stop;
            _focusSpeed = _focusRange.Min;
            // Unlike VISCA, which sends a recall at once and the stop after it, a recall not yet sent
            // is kept: it goes out next and takes the place of these stops, which are sent only if it
            // fails (NextRequestLocked, SettleStopsLocked).
            // One start for all its stops, so if they all fail they are given up together.
            var now = Stopwatch.GetTimestamp();
            foreach (var group in _groups)
                OnWantedChangedLocked(group, enteredStop: true, supersedesStop: false, now);
        }
    }

    // Records that the group's wanted state changed, so the next control request carries it. An axis
    // entering stop starts the group's stop, at now; a move that leaves nothing stopped to deliver in
    // the group supersedes it.
    private void OnWantedChangedLocked(AxisGroup group, bool enteredStop, bool supersedesStop, long now)
    {
        group.WantedVersion++;
        if (enteredStop)
            group.StartStop(now);
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
        {
            if (!_disposed)
                _presetSpeed = presetSpeed;
        }
    }

    // Recalls the app's 0-based preset presetNumber as the camera preset presetNumber + 1. Saving or
    // resetting a camera preset is not supported on an XC camera. A camera preset outside
    // PresetMin..PresetMax sends nothing and is logged, at most once per camera preset per
    // OutOfRangePresetLogInterval, since a controller may recall it on every input.
    public override void Preset(Preset preset, byte presetNumber)
    {
        // First, so a disposed camera logs nothing either.
        lock (_lock)
        {
            if (_disposed)
                return;
        }

        if (preset != Core.Devices.Preset.Recall)
        {
            Unsupported($"{nameof(Preset)} {preset.ToString().ToLowerInvariant()}");
            return;
        }

        var cameraPreset = presetNumber + 1;
        if (cameraPreset < PresetMin || cameraPreset > PresetMax)
        {
            OutOfRangePreset(cameraPreset);
            return;
        }

        lock (_lock)
        {
            if (_disposed)
                return;
            _recall = (cameraPreset, _presetSpeed);
            WakeLocked();
        }
    }

    private void Unsupported(string operation)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            var now = Stopwatch.GetTimestamp();
            if (_unsupportedLoggedAt.TryGetValue(operation, out var loggedAt) && now - loggedAt < StopwatchTicks(UnsupportedLogInterval))
                return;
            _unsupportedLoggedAt[operation] = now;
        }
        Trace.TraceInformation($"Camera {Name}: {operation} is not supported on an XC camera; nothing sent");
    }

    private void OutOfRangePreset(int cameraPreset)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            var now = Stopwatch.GetTimestamp();
            if (_outOfRangePresetLoggedAt.TryGetValue(cameraPreset, out var loggedAt) && now - loggedAt < StopwatchTicks(OutOfRangePresetLogInterval))
                return;
            _outOfRangePresetLoggedAt[cameraPreset] = now;
        }
        Trace.TraceWarning($"Camera {Name}: preset {cameraPreset} is outside the XC camera's presets {PresetMin}..{PresetMax}; nothing sent");
    }

    /// <summary>
    /// From now on the camera ignores every command; its setters still record their values and start
    /// nothing. With no stop pending, the camera is released at once: the send loop ends, cancelling
    /// the request in flight, and the connection closes. Otherwise the loop sends only the pending
    /// stops, under the usual rules, and the camera is released once none is left (RunAsync). A recall
    /// not yet sent is dropped, so the stops it would have taken the place of go out instead. Returns
    /// at once: nothing waits on the network.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _recall = null;
            if (!ReleaseDueLocked())
            {
                // Directly: WakeLocked starts nothing once the camera is disposed.
                if (_wake.CurrentCount == 0)
                    _wake.Release();
                return;
            }
            _released = true;
        }
        Release();
    }

    // Whether a disposed camera has nothing left to send: no stop pending, or no loop or address to
    // send one with. Called with _lock held.
    private bool ReleaseDueLocked() => _loop == null || Address.Length == 0 || !_groups.Any(g => g.StopActive);

    // Ends the loop, cancelling the request in flight, and closes the connection.
    private void Release()
    {
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

    // The camera's only background task. Runs until the camera is released; nothing may escape it.
    private async Task RunAsync(CancellationToken stopping)
    {
        // Whether this loop released the camera, rather than Dispose.
        var release = false;
        while (!stopping.IsCancellationRequested)
        {
            Request? request = null;
            try
            {
                TimeSpan wait;
                lock (_lock)
                {
                    // Dispose released a camera with no stop pending; the loop releases one that had,
                    // once none is left.
                    if (_disposed && (_released || ReleaseDueLocked()))
                    {
                        release = !_released;
                        _released = true;
                        break;
                    }
                    request = NextRequestLocked(Stopwatch.GetTimestamp(), out wait);
                }
                if (request == null)
                {
                    await _wake.WaitAsync(wait, stopping).ConfigureAwait(false);
                    continue;
                }

                var (failure, loginFailure, challenge, body) = await SendAsync(request, stopping).ConfigureAwait(false);
                OnCompleted(request, failure, loginFailure, challenge, body);
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
                // A recall that ends this way did not succeed: the stops it took the place of are
                // restored, as for any failed recall.
                if (request is { Recall: true })
                {
                    lock (_lock)
                        SettleStopsLocked(request, succeeded: false, Stopwatch.GetTimestamp());
                }
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

        if (release)
            Release();
    }

    // The bookkeeping of one group of axes a control request carries together (pan and tilt, zoom, or
    // focus): whether its wanted state changed since it was last sent, and the stop it is repeating,
    // from an axis entering stop (or Stop()) until it was sent StopRepeatCount + 1 times and one send
    // succeeded, a move superseded it, a recall that took its place succeeded, or it was given up.
    // StopId tells a send of an earlier stop from one of the current stop.
    private sealed class AxisGroup
    {
        public AxisGroup(string name) => Name = name;

        // The group's name in the log.
        public string Name { get; }
        // Bumped on every wanted change; the group is sent while the version it last sent differs.
        public long WantedVersion;
        public long SentVersion;
        public bool StopActive;
        public long StopId;
        public int StopSends;
        public bool StopSucceeded;
        // Stopwatch timestamp of when the stop started (or a failed recall restored it): its give-up
        // bound counts from here.
        public long StopStartedAt;

        // Whether the next control request carries the group: a change not sent yet, or a stop still
        // being repeated or retried.
        public bool Pending => WantedVersion != SentVersion || StopActive;

        public void StartStop(long now)
        {
            StopActive = true;
            StopId++;
            StopSends = 0;
            StopSucceeded = false;
            StopStartedAt = now;
        }
    }

    // A request the loop sends: a control request (moves, stops or a preset recall) or a probe, to the
    // address and port current at EndpointVersion. StopIds, one per axis group, is the stop the request
    // repeats in that group, or 0 for none; for a recall, the stop it takes the place of. Login is the
    // username and password, null when no username is set; Challenge the one the camera gave at that
    // address and port, answered up front, or null. Neither shows in a log: both print as their type
    // name.
    // ReadsRanges marks a probe of the whole info.cgi, whose answer carries the speed ranges.
    private sealed record Request(Uri Uri, bool Recall, long[] StopIds, long EndpointVersion, NetworkCredential? Login, Challenge? Challenge, bool ReadsRanges = false);

    // The request to send now, or null and how long to wait before looking again (infinite with no
    // address; a wake ends the wait early). Only the newest wanted state is ever sent, and a probe
    // only while no control request is wanted.
    private Request? NextRequestLocked(long now, out TimeSpan wait)
    {
        wait = Timeout.InfiniteTimeSpan;
        if (Address.Length == 0)
            return null;

        // A recall goes out next, ahead of every stop and move, and takes the place of the pending
        // stops (below), so neither continuous movement nor a stop that keeps failing holds it back.
        var recall = _recall != null;
        var control = recall || _groups.Any(CarriesLocked);

        // Gaps count from the previous request's completion, so request starts are further apart. A
        // stop's first send waits only the normal gap; while any stop is being repeated or retried,
        // every request but a recall waits the repeat interval, so no stop's sends come closer
        // together than it. A recall, which takes those stops' place, waits only the normal gap. A
        // probe waits until no request has completed for the probe interval.
        var gap = control
            ? !recall && _groups.Any(g => g.StopActive && g.StopSends > 0) ? StopRepeatInterval : MinRequestGap
            : _probeNow ? MinRequestGap : _probeInterval;
        var due = _lastCompletedAt + StopwatchTicks(gap);
        if (now < due)
        {
            wait = WaitUntil(due, now);
            return null;
        }

        // Built before any state changes, so a failure here leaves the request wanted.
        var uri = recall ? RecallUri(_recall!.Value) : control ? ControlUri() : ProbeUri(_rangesWanted);
        var login = _login;
        // Answered only at the address and port it came from: the base class sets a new address or
        // port before OnEndpointChanged forgets the challenge, and a request built in between goes to
        // the new one.
        var challenge = login != null && _challenge?.Authority == uri.Authority ? _challenge : null;
        _probeNow = false;
        var stopIds = new long[_groups.Length];
        if (recall)
        {
            _recall = null;
            // The recall takes the place of every pending stop: none is sent while it is in flight,
            // and OnCompleted ends them once it succeeds. The stop is all such a group has left to
            // send, except a pan or tilt move the camera was never sent, which goes out after it.
            for (var i = 0; i < _groups.Length; i++)
            {
                var group = _groups[i];
                if (!group.StopActive)
                    continue;
                if (group != _panTiltGroup || !PanTiltMoveUnsentLocked())
                    group.SentVersion = group.WantedVersion;
                stopIds[i] = group.StopId;
            }
            return new Request(uri, Recall: true, stopIds, _endpointVersion, login, challenge);
        }
        if (!control)
            return new Request(uri, Recall: false, stopIds, _endpointVersion, login, challenge, ReadsRanges: _rangesWanted);
        if (CarriesLocked(_panTiltGroup))
            _sentPanTilt = (_pan, _panSpeed, _tilt, _tiltSpeed);
        for (var i = 0; i < _groups.Length; i++)
        {
            var group = _groups[i];
            if (!CarriesLocked(group))
                continue;
            group.SentVersion = group.WantedVersion;
            if (!group.StopActive)
                continue;
            group.StopSends++;
            stopIds[i] = group.StopId;
        }
        return new Request(uri, Recall: false, stopIds, _endpointVersion, login, challenge);
    }

    // Whether the next control request carries the group: a pending group, or once the camera is
    // disposed, only a group whose stop is pending, so no move goes out.
    private bool CarriesLocked(AxisGroup group) => _disposed ? group.StopActive : group.Pending;

    // Whether pan or tilt is wanted moving at a direction or speed other than the last one sent for
    // it. An axis wanted at stop, or at the move last sent for it, has nothing to send.
    private bool PanTiltMoveUnsentLocked() =>
        _pan != PanDir.Stop && (_pan, _panSpeed) != (_sentPanTilt.Pan, _sentPanTilt.PanSpeed)
        || _tilt != TiltDir.Stop && (_tilt, _tiltSpeed) != (_sentPanTilt.Tilt, _sentPanTilt.TiltSpeed);

    private static long StopwatchTicks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    // Rounded up to whole milliseconds: a wait shorter than the timer can measure would spin.
    private static TimeSpan WaitUntil(long due, long now) =>
        TimeSpan.FromMilliseconds(Math.Ceiling((due - now) * 1000.0 / Stopwatch.Frequency));

    // Carries each axis group the request carries (CarriesLocked), in the order pan/tilt, zoom, focus;
    // any other group is left out. Each axis's speed goes before its direction. A stopped axis sends
    // the slowest speed.
    private Uri ControlUri()
    {
        var parameters = new List<string>();
        if (CarriesLocked(_panTiltGroup))
            parameters.Add(string.Create(CultureInfo.InvariantCulture,
                $"c.1.pan.speed.dir={_panSpeed}&c.1.pan={PanValue(_pan)}&c.1.tilt.speed.dir={_tiltSpeed}&c.1.tilt={TiltValue(_tilt)}"));
        if (CarriesLocked(_zoomGroup))
            parameters.Add(string.Create(CultureInfo.InvariantCulture,
                $"c.1.zoom.speed.dir={_zoomSpeed}&c.1.zoom={ZoomValue(_zoom)}"));
        if (CarriesLocked(_focusGroup))
            parameters.Add(string.Create(CultureInfo.InvariantCulture,
                $"c.1.focus.speed={_focusSpeed}&c.1.focus.action={FocusValue(_focus)}"));
        return new(string.Create(CultureInfo.InvariantCulture, $"http://{Address}:{Port}{ControlPath}?{string.Join('&', parameters)}"));
    }

    // A recall carries only the camera preset and, once a recall speed was set, its p.ptzspeed.
    private Uri RecallUri((int Preset, int? PresetSpeed) recall) => new(recall.PresetSpeed is { } speed
        ? string.Create(CultureInfo.InvariantCulture, $"http://{Address}:{Port}{ControlPath}?p={recall.Preset}&p.ptzspeed={speed}")
        : string.Create(CultureInfo.InvariantCulture, $"http://{Address}:{Port}{ControlPath}?p={recall.Preset}"));

    private Uri ProbeUri(bool whole) => new(string.Create(CultureInfo.InvariantCulture, $"http://{Address}:{Port}{(whole ? InfoPath : ProbePathAndQuery)}"));

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

    // Sends one request; returns null when it succeeded, else why it failed and whether that is a login
    // failure, with the camera's challenge to answer from now on (null when it did not challenge, or
    // challenged with nothing the app can answer). A request the camera challenges is repeated once,
    // at once, answering the challenge: both share the request's one timeout. For a request that
    // reads the speed ranges, also the answer's body once the camera answered other than with a login
    // failure, else null. Throws only when stopping (or on an unexpected error, which the loop logs).
    private async Task<(string? Failure, bool LoginFailure, Challenge? Challenge, string? Body)> SendAsync(Request request, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(_requestTimeout);
        Challenge? challenge = null;
        try
        {
            var login = request.Login;
            using var response = await GetAsync(request.Uri, login != null ? request.Challenge?.Answer(login, request.Uri) : null, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                return (Failure(response), false, null, await BodyAsync(request, response, timeout.Token).ConfigureAwait(false));
            if (login == null)
                return (LoginRequired, true, null, null);

            challenge = Challenge.From(response, request.Uri, request.Challenge);
            if (challenge == null)
                return ($"{LoginUnsupported} (offered: {Challenge.Offered(response)})", true, null, null);

            using var retry = await GetAsync(request.Uri, challenge.Answer(login, request.Uri), timeout.Token).ConfigureAwait(false);
            if (retry.StatusCode == HttpStatusCode.Unauthorized)
                return (LoginRefused, true, Challenge.From(retry, request.Uri, challenge) ?? challenge, null);
            return (Failure(retry), false, challenge, await BodyAsync(request, retry, timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            return ($"no answer within {_requestTimeout.TotalMilliseconds:0} ms", false, challenge, null);
        }
        catch (HttpRequestException e) when (!stopping.IsCancellationRequested)
        {
            return (e.GetBaseException().Message, false, challenge, null);
        }
    }

    // The answer's body for a request that reads the speed ranges, else null. Already buffered by the
    // client within the request's timeout.
    private static async Task<string?> BodyAsync(Request request, HttpResponseMessage response, CancellationToken cancellation) =>
        request.ReadsRanges ? await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false) : null;

    /// <summary>
    /// The speed range <paramref name="item"/> (such as <c>c.1.zoom.speed</c>) that an info.cgi answer
    /// reports in its <c>item.min:=n</c> and <c>item.max:=n</c> lines, or null when either is missing
    /// or not an integer, or the minimum is not below the maximum.
    /// </summary>
    internal static (int Min, int Max)? SpeedRange(string info, string item)
    {
        int? min = null, max = null;
        foreach (var raw in info.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var separator = line.IndexOf(":=", StringComparison.Ordinal);
            if (separator < 0)
                continue;
            var key = line[..separator];
            if (key != item + ".min" && key != item + ".max")
                continue;
            if (!int.TryParse(line[(separator + 2)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return null;
            if (key.EndsWith(".min", StringComparison.Ordinal))
                min = value;
            else
                max = value;
        }
        return min is { } lo && max is { } hi && lo < hi ? (lo, hi) : null;
    }

    // Takes the speed ranges from an info.cgi answer, each the spec's when it reports none usable, and
    // returns the line to log when they differ from those last logged, else null. Called with _lock held.
    private string? TakeRangesLocked(string info)
    {
        var zoom = SpeedRange(info, "c.1.zoom.speed");
        var focus = SpeedRange(info, "c.1.focus.speed");
        _zoomRange = zoom ?? (ZoomSpeedMin, ZoomSpeedMax);
        _focusRange = focus ?? (FocusSpeedMin, FocusSpeedMax);
        const string Spec = " (Canon's spec; the camera reported no usable range)";
        var ranges = string.Create(CultureInfo.InvariantCulture,
            $"zoom speeds {_zoomRange.Min}..{_zoomRange.Max}{(zoom != null ? " (from the camera)" : Spec)}, focus speeds {_focusRange.Min}..{_focusRange.Max}{(focus != null ? " (from the camera)" : Spec)}");
        if (ranges == _rangesLogged)
            return null;
        _rangesLogged = ranges;
        return $"Camera {Name}: the XC camera at {Address}:{Port} uses {ranges}";
    }

    // A GET of the uri, with the Authorization header when there is one.
    private async Task<HttpResponseMessage> GetAsync(Uri uri, AuthenticationHeaderValue? authorization, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Authorization = authorization;
        return await _http.SendAsync(message, cancellation).ConfigureAwait(false);
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

    // Settles the stops the request sent or took the place of (SettleStopsLocked), keeps the camera's
    // challenge, updates Connected, and gives up the failed stops past their bound
    // (GiveUpFailedStopsLocked). Logs a failure when a run of failures starts or its reason changes (a
    // login failure instead at most once per LoginFailureLogInterval), the first success after one,
    // each Connected change, and the stops a failed request gives up, in one ERROR; never once per
    // request.
    private void OnCompleted(Request request, string? failure, bool loginFailure, Challenge? challenge, string? body)
    {
        string? warning = null;
        string? information = null;
        string? ranges = null;
        string? error = null;
        var connectedChanged = false;
        var failureChanged = false;
        lock (_lock)
        {
            // Released while the request was in flight: the camera sends and logs nothing more.
            if (_released)
                return;
            var now = Stopwatch.GetTimestamp();
            _lastCompletedAt = now;
            SettleStopsLocked(request, succeeded: failure == null, now);

            if (request.EndpointVersion != _endpointVersion)
            {
                // Sent to an address or port, or with a login, no longer configured: says nothing about
                // the camera now.
                return;
            }

            if (challenge != null)
                _challenge = challenge;
            else if (loginFailure)
                _challenge = null;

            // Any answer ends the read, so an answer that keeps failing does not keep the whole
            // info.cgi in every probe; only a successful one carries the ranges.
            if (request.ReadsRanges && body != null)
            {
                _rangesWanted = false;
                if (failure == null)
                    ranges = TakeRangesLocked(body);
            }

            if (failure == null)
            {
                _lastSuccessAt = now;
                connectedChanged = !_connected;
                _connected = true;
                // One line for the recovery, whether or not it also connects the camera.
                if (_failure != null)
                    information = $"Camera {Name}: requests to the XC camera at {Address}:{Port} succeed again{(connectedChanged ? "; it is connected" : "")}";
                else if (connectedChanged)
                    information = $"Camera {Name}: the XC camera at {Address}:{Port} is connected";
                failureChanged = _failure != null;
                _failure = null;
            }
            else
            {
                if (loginFailure ? LoginFailureLogDueLocked(now) : failure != _failure)
                    warning = $"Camera {Name}: request to the XC camera at {Address}:{Port} failed: {failure}";
                failureChanged = failure != _failure;
                _failure = failure;
                if (_connected && now - _lastSuccessAt >= StopwatchTicks(_probeInterval))
                {
                    _connected = false;
                    connectedChanged = true;
                    // It may come back with other firmware: the next connect reads its ranges again.
                    _rangesWanted = true;
                    information = $"Camera {Name}: the XC camera at {Address}:{Port} is not reachable";
                }
                if (GiveUpFailedStopsLocked(request, now) is { } givenUp)
                    error = string.Create(CultureInfo.InvariantCulture,
                        $"Camera {Name}: gave up stopping the XC camera at {Address}:{Port} ({string.Join(", ", givenUp)}) after {_stopGiveUpAfter.TotalSeconds:0.###} s with no successful send (last failure: {failure}); it may still be moving");
            }
        }

        if (warning != null)
            Trace.TraceWarning(warning);
        if (information != null)
            Trace.TraceInformation(information);
        if (ranges != null)
            Trace.TraceInformation(ranges);
        if (error != null)
            Trace.TraceError(error);
        if (connectedChanged)
            RaiseChanged(nameof(Connected));
        if (failureChanged)
            RaiseChanged(nameof(FailureReason));
    }

    // Settles the stops a completed request sent or took the place of. A stop ends once it was sent
    // StopRepeatCount + 1 times and one send succeeded. A recall ends the stops it took the place of if
    // it succeeded, and restores each as a new stop if not. A stop a newer action took the place of is
    // left alone: a newer stop (a new StopId) or a move that superseded it.
    private void SettleStopsLocked(Request request, bool succeeded, long now)
    {
        for (var i = 0; i < _groups.Length; i++)
        {
            var group = _groups[i];
            if (!group.StopActive || request.StopIds[i] != group.StopId)
                continue;
            if (request.Recall)
            {
                if (succeeded)
                    group.StopActive = false;
                else
                    group.StartStop(now);
            }
            else
            {
                group.StopSucceeded |= succeeded;
                if (group.StopSucceeded && group.StopSends > StopRepeatCount)
                    group.StopActive = false;
            }
        }
    }

    // Gives up the stops a failed request sent that were sent StopRepeatCount + 1 times and have been
    // failing for _stopGiveUpAfter since they started. Returns the names of the groups given up, or
    // null for none. None is given up while a recall is wanted: it goes out next and takes the stops'
    // place, which ends them if it succeeds and restores them, each with a fresh bound, if not.
    private List<string>? GiveUpFailedStopsLocked(Request request, long now)
    {
        if (_recall != null)
            return null;
        List<string>? givenUp = null;
        for (var i = 0; i < _groups.Length; i++)
        {
            var group = _groups[i];
            if (!group.StopActive || request.StopIds[i] != group.StopId || group.StopSucceeded
                || group.StopSends <= StopRepeatCount || now - group.StopStartedAt < StopwatchTicks(_stopGiveUpAfter))
                continue;
            group.StopActive = false;
            (givenUp ??= new()).Add(group.Name);
        }
        return givenUp;
    }

    // Whether a login failure may be logged now; if so, counts it as logged.
    private bool LoginFailureLogDueLocked(long now)
    {
        if (now - _loginFailureLoggedAt < StopwatchTicks(LoginFailureLogInterval))
            return false;
        _loginFailureLoggedAt = now;
        return true;
    }

    // Raises a Connected or FailureReason change. Raised outside _lock (a subscriber reads the camera's
    // state); a failing subscriber is logged, never thrown into the send loop or a setter's caller.
    private void RaiseChanged(string propertyName)
    {
        try
        {
            NotifyPropertyChanged(propertyName);
        }
        catch (Exception e)
        {
            Trace.TraceError($"Camera {Name}: a {propertyName} subscriber failed: {e}");
        }
    }

    // A login challenge from the camera's HTTP 401 (a WWW-Authenticate header) and how to answer it
    // with a username and password: Basic (RFC 7617), or Digest with MD5 (RFC 7616), with qop auth when
    // the challenge offers it. A Digest answer counts nc up on the challenge's nonce: only the send
    // loop answers.
    private sealed class Challenge
    {
        // auth-param (RFC 9110 section 11.2): a name, "=" and a token or a quoted string.
        private static readonly Regex AuthParam = new(@"([^\s=,]+)\s*=\s*(?:""((?:[^""\\]|\\.)*)""|([^\s,]*))", RegexOptions.CultureInvariant);
        private static readonly Regex QuotedPair = new(@"\\(.)", RegexOptions.CultureInvariant);

        private readonly bool _digest;
        private readonly string _realm = "";
        private readonly string _nonce = "";
        private readonly string? _opaque;
        private readonly string? _algorithm;
        private readonly bool _qop;
        // The requests answered on the nonce so far (nc).
        private long _nonceCount;

        private Challenge(string authority)
        {
            Authority = authority;
        }

        private Challenge(string authority, string realm, string nonce, string? opaque, string? algorithm, bool qop, long nonceCount)
        {
            Authority = authority;
            _digest = true;
            _realm = realm;
            _nonce = nonce;
            _opaque = opaque;
            _algorithm = algorithm;
            _qop = qop;
            _nonceCount = nonceCount;
        }

        // The address and port (the URI authority) the challenge came from, the only one it is
        // answered at.
        public string Authority { get; }

        // The challenge to answer among those of a 401 to a GET of the uri: Digest when it offers one
        // the app can answer, so the password does not cross the network in the clear, else Basic; null
        // for neither. A Digest challenge on the nonce of the previous one counts nc on from it.
        public static Challenge? From(HttpResponseMessage response, Uri uri, Challenge? previous)
        {
            Challenge? basic = null;
            foreach (var offered in response.Headers.WwwAuthenticate)
            {
                if (offered.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase))
                {
                    if (Digest(offered.Parameter, uri.Authority, previous) is { } digest)
                        return digest;
                }
                else if (offered.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
                {
                    basic ??= new Challenge(uri.Authority);
                }
            }
            return basic;
        }

        // The schemes a 401 offers, for the log: each Digest with its algorithm when it names one, and
        // never a realm, nonce or other parameter; "none" when it offers none.
        public static string Offered(HttpResponseMessage response)
        {
            var offered = response.Headers.WwwAuthenticate
                .Select(c => c.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase) && Parameters(c.Parameter).TryGetValue("algorithm", out var algorithm)
                    ? $"{c.Scheme} {algorithm}"
                    : c.Scheme)
                .ToList();
            return offered.Count > 0 ? string.Join(", ", offered) : "none";
        }

        // A challenge's auth-params by name, unquoted; the first of a name given twice.
        private static Dictionary<string, string> Parameters(string? parameter)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in AuthParam.Matches(parameter ?? ""))
            {
                var value = match.Groups[2].Success ? QuotedPair.Replace(match.Groups[2].Value, "$1") : match.Groups[3].Value;
                parameters.TryAdd(match.Groups[1].Value, value);
            }
            return parameters;
        }

        // A Digest challenge with a realm and a nonce, algorithm MD5 (or none given), and qop auth among
        // its qops (or none given); null for any other.
        private static Challenge? Digest(string? parameter, string authority, Challenge? previous)
        {
            var parameters = Parameters(parameter);
            if (!parameters.TryGetValue("realm", out var realm) || !parameters.TryGetValue("nonce", out var nonce))
                return null;
            parameters.TryGetValue("algorithm", out var algorithm);
            if (algorithm != null && !algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase))
                return null;
            var qop = parameters.TryGetValue("qop", out var qops);
            if (qop && !qops!.Split(',').Any(q => q.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase)))
                return null;

            parameters.TryGetValue("opaque", out var opaque);
            var nonceCount = previous is { _digest: true } && previous._realm == realm && previous._nonce == nonce ? previous._nonceCount : 0;
            return new Challenge(authority, realm, nonce, opaque, algorithm, qop, nonceCount);
        }

        // The Authorization header for a GET of the uri. Digest per RFC 7616 section 3.4: username,
        // realm, nonce, uri, response, cnonce and opaque are quoted strings; qop, nc and algorithm are
        // tokens, the algorithm as the challenge gave it.
        public AuthenticationHeaderValue Answer(NetworkCredential login, Uri uri)
        {
            if (!_digest)
                return new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login.UserName}:{login.Password}")));

            var target = uri.PathAndQuery;
            var ha1 = Md5($"{login.UserName}:{_realm}:{login.Password}");
            var ha2 = Md5($"GET:{target}");
            var answer = new StringBuilder($"username={Quoted(login.UserName)}, realm={Quoted(_realm)}, nonce={Quoted(_nonce)}, uri={Quoted(target)}");
            if (_qop)
            {
                var nc = (++_nonceCount).ToString("x8", CultureInfo.InvariantCulture);
                var cnonce = RandomNumberGenerator.GetHexString(16, lowercase: true);
                answer.Append($", qop=auth, nc={nc}, cnonce={Quoted(cnonce)}, response={Quoted(Md5($"{ha1}:{_nonce}:{nc}:{cnonce}:auth:{ha2}"))}");
            }
            else
            {
                answer.Append($", response={Quoted(Md5($"{ha1}:{_nonce}:{ha2}"))}");
            }
            if (_opaque != null)
                answer.Append($", opaque={Quoted(_opaque)}");
            if (_algorithm != null)
                answer.Append($", algorithm={_algorithm}");
            return new("Digest", answer.ToString());
        }

        private static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

        private static string Quoted(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }
}
