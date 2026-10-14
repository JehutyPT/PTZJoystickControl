using System.ComponentModel;
using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// Reachability with scaled-down timings: a probe after ProbeInterval without a completed request,
// unreachable after a failure with no success within the interval.
public class XcCameraReachabilityTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ACameraAnsweringProbes_TurnsConnected_AndRaisesTheChange()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera();
        using var camera = new XcCamera(name, ShortTimeout, Interval);
        var connected = ConnectedBecomes(camera, true);

        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";

        await connected.WaitAsync(Wait);
        Assert.True(camera.Connected);
        Assert.Equal("item=s", fake.Probes[0].Query);
        await XcTestCameras.WaitForLogAsync(trace, name, "connected");
        // On a busy host a probe can time out before one succeeds, adding a warning; the connect is one line.
        Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Message.Contains("is connected"));
    }

    [Fact]
    public async Task AnIdleCamera_IsProbedNoMoreOftenThanTheInterval()
    {
        using var fake = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: ShortTimeout, probeInterval: Interval);

        var probes = (await fake.WaitUntilAsync(r => r.Count(x => x.IsProbe) >= 3, "three probes")).Where(r => r.IsProbe).ToList();

        for (var i = 1; i < probes.Count; i++)
        {
            var gap = Stopwatch.GetElapsedTime(probes[i - 1].ArrivedAt, probes[i].ArrivedAt);
            Assert.True(gap >= Interval, $"Probes {i - 1} and {i} arrived {gap.TotalMilliseconds:0.0} ms apart. {FakeXcCamera.Describe(probes)}");
        }
    }

    [Fact]
    public async Task ACameraThatStopsAnswering_TurnsUnreachable_LoggedOnce()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var answering = 1;
        using var fake = new FakeXcCamera { Responder = _ => Volatile.Read(ref answering) == 1 ? XcReply.Ok : XcReply.Never };
        using var camera = new XcCamera(name, ShortTimeout, Interval);
        var connected = ConnectedBecomes(camera, true);
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        await connected.WaitAsync(Wait);
        var unreachable = ConnectedBecomes(camera, false);
        // On a busy host a probe can time out before the camera connects; only the lines from here on count.
        var loggedBefore = XcTestCameras.LogFor(trace, name).Count;

        Volatile.Write(ref answering, 0);

        await unreachable.WaitAsync(Wait);
        Assert.False(camera.Connected);
        await XcTestCameras.WaitForLogAsync(trace, name, "not reachable");
        await Task.Delay(3 * (Interval + ShortTimeout));
        var sinceSilent = XcTestCameras.LogFor(trace, name).Skip(loggedBefore).ToList();
        Assert.Single(sinceSilent, l => l.Message.Contains("not reachable"));
        Assert.Single(sinceSilent, l => l.Type == TraceEventType.Warning);
    }

    // Longer timings than the other tests: they leave room for a busy host, while a camera that waited
    // for a second probe or a second interval would take at least one more interval.
    [Fact]
    public async Task AnIdleCameraThatStopsAnswering_TurnsUnreachable_WithinTheProbeIntervalPlusOneTimeout()
    {
        var interval = TimeSpan.FromSeconds(1);
        var timeout = TimeSpan.FromMilliseconds(200);
        var answering = 1;
        using var fake = new FakeXcCamera { Responder = _ => Volatile.Read(ref answering) == 1 ? XcReply.Ok : XcReply.Never };
        using var camera = new XcCamera(XcTestCameras.UniqueName(), timeout, interval);
        var connected = ConnectedBecomes(camera, true);
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        await connected.WaitAsync(Wait);
        var unreachable = ConnectedBecomes(camera, false);

        var silentSince = Stopwatch.GetTimestamp();
        Volatile.Write(ref answering, 0);
        await unreachable.WaitAsync(Wait);

        var took = Stopwatch.GetElapsedTime(silentSince);
        Assert.True(took < interval + timeout + TimeSpan.FromMilliseconds(800), $"Unreachable {took.TotalMilliseconds:0} ms after the camera stopped answering. {FakeXcCamera.Describe(fake.Requests)}");
    }

    [Fact]
    public async Task NoProbeStarts_WhileAControlRequestIsInFlight()
    {
        using var fake = new FakeXcCamera { Responder = r => r.IsControl ? XcReply.Held : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout, probeInterval: Interval);
        camera.Pan(0.5, PanDir.Right);
        var control = await fake.WaitForControlAsync(1);

        await Task.Delay(4 * Interval);

        Assert.DoesNotContain(fake.Probes, p => p.ArrivedAt > control.ArrivedAt);
        Assert.Equal(1, fake.MaxInFlight);
    }

    // Between a refused stop and its retry nothing is in flight, but the stop is still wanted: no probe
    // goes out in those gaps, though the probe interval here is shorter than the retry interval.
    [Fact]
    public async Task NoProbeStarts_WhileAControlRequestIsWanted()
    {
        var refusedStops = 0;
        using var fake = new FakeXcCamera
        {
            Responder = r => r.IsStop && Interlocked.Increment(ref refusedStops) <= 4 ? XcReply.With(200, "301") : XcReply.Ok,
        };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout, probeInterval: XcCamera.StopRepeatInterval * 0.8);
        camera.Pan(0.4, PanDir.Left);
        await fake.WaitForControlAsync(1);

        camera.Pan(0, PanDir.Stop);
        var requests = await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 5, "four refused stops and a fifth");

        var stops = requests.Where(r => r.IsStop).ToList();
        Assert.DoesNotContain(requests, r => r.IsProbe && r.ArrivedAt > stops[0].ArrivedAt && r.ArrivedAt < stops[^1].ArrivedAt);
    }

    [Fact]
    public async Task AStopWantedWhileAProbeIsHeld_GoesOutOnceTheProbeReturns()
    {
        using var fake = new FakeXcCamera { Responder = r => r.IsProbe ? XcReply.Held : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout, probeInterval: Interval);
        await fake.WaitForProbeAsync(1);

        camera.Stop();
        await Task.Delay(2 * Interval);
        var whileHeld = fake.ControlRequests.Count;
        fake.Release();

        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 3, "three stops");
        Assert.Equal(0, whileHeld);
    }

    [Fact]
    public async Task AStopWantedWhileAProbeGoesUnanswered_GoesOutOnceTheProbeTimesOut()
    {
        using var fake = new FakeXcCamera { Responder = r => r.IsProbe ? XcReply.Never : XcReply.Ok };
        // The probe interval is long: only the probe's timeout can free the stop.
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: TimeSpan.FromMilliseconds(300), probeInterval: LongTimeout);
        await fake.WaitForProbeAsync(1);

        camera.Stop();

        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 3, "three stops");
    }

    [Fact]
    public async Task PointingTheCameraAtAnotherPort_SendsTheNextRequestThere_AndRaisesThePersistentChange()
    {
        using var first = new FakeXcCamera();
        // Holds its probes, so the camera cannot reconnect before the asserts on the reset.
        using var second = new FakeXcCamera { Responder = r => r.IsProbe ? XcReply.Held : XcReply.Ok };
        // The probe interval is long: a prompt probe to the new port comes from the change itself.
        using var camera = new XcCamera(XcTestCameras.UniqueName(), ShortTimeout, LongTimeout);
        var connected = ConnectedBecomes(camera, true);
        camera.Port = first.Port;
        camera.Address = "127.0.0.1";
        await connected.WaitAsync(Wait);
        var persistent = new List<string?>();
        camera.PersistentPropertyChanged += (_, e) => persistent.Add(e.PropertyName);
        var reconnected = ConnectedBecomes(camera, true);

        camera.Port = second.Port;

        Assert.False(camera.Connected);
        Assert.Equal(new[] { nameof(XcCamera.Port) }, persistent);
        await second.WaitForProbeAsync(1);
        second.Release();
        await reconnected.WaitAsync(Wait);
        var firstCount = first.Requests.Count;
        camera.Pan(0.5, PanDir.Right);
        await second.WaitForControlAsync(1);
        Assert.Equal(firstCount, first.Requests.Count);
    }

    // A camera that answers every request with HTTP 401, without a login and with a wrong one: no
    // request succeeds, so it is never connected.
    [Theory]
    [InlineData("")]
    [InlineData("fake-xc-wrong-password")]
    public async Task ACameraAnswering401ToEverything_IsNotReachable(string password)
    {
        using var fake = new FakeXcCamera { Login = new XcLogin { Username = "fake-xc-user", Password = "fake-xc-password", Digest = true } };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout, probeInterval: Interval,
            username: password.Length > 0 ? "fake-xc-user" : "", password: password);

        await fake.WaitUntilAsync(r => r.Count(x => x.IsProbe) >= 4, "four probes");

        Assert.All(fake.Requests, r => Assert.True(r.Challenged));
        Assert.False(camera.Connected);
    }

    // Probes alone, with no control request: the first is challenged and repeated with the login, and
    // every later one carries the login up front, so an idle camera stays reachable.
    [Fact]
    public async Task WithTheRightLogin_ProbesAlone_MakeACameraThatRequiresALoginReachable()
    {
        using var fake = new FakeXcCamera { Login = new XcLogin { Username = "fake-xc-user", Password = "fake-xc-password", Digest = true } };
        using var camera = new XcCamera(XcTestCameras.UniqueName(), LongTimeout, Interval);
        var connected = ConnectedBecomes(camera, true);
        camera.Username = "fake-xc-user";
        camera.Password = "fake-xc-password";
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";

        await connected.WaitAsync(Wait);
        var probes = await fake.WaitUntilAsync(r => r.Count(x => x.IsProbe) >= 4, "four probes");

        var described = FakeXcCamera.Describe(probes);
        Assert.All(probes, r => Assert.True(r.IsProbe, described));
        Assert.True(probes[0].Challenged, described);
        Assert.All(probes.Skip(1), r => Assert.False(r.Challenged, described));
    }

    // Unlike an address change, a login change leaves Connected as it is until a request with the new
    // login settles it. Here the new login is refused, which turns the camera unreachable only once no
    // request succeeded for the (long) probe interval.
    [Fact]
    public async Task ALoginChange_KeepsConnected_UntilARequestWithTheNewLoginSettlesIt()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Login = new XcLogin { Username = "fake-xc-user", Password = "fake-xc-password", Basic = true } };
        using var camera = new XcCamera(name, LongTimeout, LongTimeout);
        var connected = ConnectedBecomes(camera, true);
        camera.Username = "fake-xc-user";
        camera.Password = "fake-xc-password";
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        await connected.WaitAsync(Wait);
        var changes = new List<string?>();
        camera.PropertyChanged += (_, e) =>
        {
            lock (changes)
                changes.Add(e.PropertyName);
        };

        camera.Password = "fake-xc-wrong-password";

        Assert.True(camera.Connected);
        await XcTestCameras.WaitForLogAsync(trace, name, "refused the username and password");
        Assert.True(camera.Connected);
        lock (changes)
            Assert.Equal(new[] { nameof(XcCamera.Password) }, changes);
    }

    // Completes when the camera raises a Connected change to the given value.
    private static Task ConnectedBecomes(XcCamera camera, bool value)
    {
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (e.PropertyName == nameof(ICamera.Connected) && camera.Connected == value)
            {
                camera.PropertyChanged -= handler;
                raised.TrySetResult();
            }
        };
        camera.PropertyChanged += handler;
        return raised.Task;
    }
}
