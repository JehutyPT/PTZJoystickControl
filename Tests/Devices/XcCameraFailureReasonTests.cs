using System.ComponentModel;
using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// The XC camera's failure reason, which its settings row shows: the reason in the failure's WARN, in
// the same words, set when a request to the current address, port and login fails, and cleared when
// one succeeds or the address or port changes. Each change is raised once.
public class XcCameraFailureReasonTests
{
    // Obviously fake credentials.
    private const string User = "fake-xc-user";
    private const string Secret = "fake-xc-password";

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);
    // Long enough that only a fake that never answers times a request out, even on a busy host.
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // Probes alone, every one failing the same way: the reason is set by the first and kept by the
    // next, though the WARN is logged once.
    [Theory]
    [InlineData("a refused login", "the camera refused the username and password")]
    [InlineData("no login", "the camera requires a login; set username and password")]
    [InlineData("livescope-status 301", "no camera control right: another client may hold control")]
    [InlineData("HTTP 500", "HTTP 500")]
    [InlineData("no answer", "no answer within 100 ms")]
    public async Task EachFailure_SetsTheReason_InTheWordsOfItsWarning_RaisedOnce(string failure, string reason)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var failing = Failing(failure);
        using var fake = failing.Fake;
        using var camera = new XcCamera(name, failing.RequestTimeout, Interval);
        var reasons = ReasonsRaised(camera);
        camera.Username = failing.Username;
        camera.Password = failing.Password;
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";

        // At most two HTTP requests a probe (a 401 and its repeat): the fifth starts a third probe,
        // so two have completed.
        await fake.WaitUntilAsync(r => r.Count(x => x.IsProbe) >= 5, "two probes completed");

        var warning = Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
        Assert.EndsWith($"failed: {reason}", warning.Message);
        Assert.Equal(reason, camera.FailureReason);
        lock (reasons)
            Assert.Equal(new[] { reason }, reasons);
    }

    [Fact]
    public async Task ALaterSuccess_ClearsTheReason_RaisedOnce()
    {
        var answering = 0;
        using var fake = new FakeXcCamera { Responder = _ => Volatile.Read(ref answering) == 1 ? XcReply.Ok : XcReply.With(500, "0") };
        using var camera = new XcCamera(XcTestCameras.UniqueName(), LongTimeout, Interval);
        var reasons = ReasonsRaised(camera);
        var failed = ReasonBecomes(camera, "HTTP 500");
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        await failed.WaitAsync(Wait);
        var cleared = ReasonBecomes(camera, null);

        Volatile.Write(ref answering, 1);

        await cleared.WaitAsync(Wait);
        // The second probe from here starts once the first, a success, has completed.
        var probes = fake.Probes.Count;
        await fake.WaitUntilAsync(r => r.Count(x => x.IsProbe) >= probes + 2, "a probe completed after the reason cleared");
        Assert.Null(camera.FailureReason);
        lock (reasons)
            Assert.Equal(new[] { "HTTP 500", null }, reasons);
    }

    // Raised on the setter's thread, before it returns. With no address the camera sends nothing, so
    // no request can set the reason again.
    [Fact]
    public async Task AnAddressChange_ClearsTheReason_AtOnce()
    {
        using var fake = new FakeXcCamera { Responder = _ => XcReply.With(500, "0") };
        using var camera = new XcCamera(XcTestCameras.UniqueName(), LongTimeout, Interval);
        var reasons = ReasonsRaised(camera);
        var failed = ReasonBecomes(camera, "HTTP 500");
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        await failed.WaitAsync(Wait);

        camera.Address = "";

        Assert.Null(camera.FailureReason);
        lock (reasons)
            Assert.Equal(new[] { "HTTP 500", null }, reasons);
    }

    // The pan in flight when the login changes succeeds, but says nothing about the camera with the new
    // login: the reason stays as the failed probe left it. The probe interval is long: the only probe
    // after the pan is the one the login change brings forward.
    [Fact]
    public async Task TheOutcomeOfARequestSentBeforeALoginChange_LeavesTheReasonAsItWas()
    {
        var held = 0;
        using var fake = new FakeXcCamera
        {
            // The first control request is held, then answered with success; every other request fails.
            Responder = r => r.IsControl && Interlocked.Exchange(ref held, 1) == 0 ? XcReply.Held : XcReply.With(500, "0"),
        };
        using var camera = new XcCamera(XcTestCameras.UniqueName(), LongTimeout, LongTimeout);
        var reasons = ReasonsRaised(camera);
        var failed = ReasonBecomes(camera, "HTTP 500");
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";
        await failed.WaitAsync(Wait);
        camera.Pan(0.5, PanDir.Right);
        await fake.WaitForControlAsync(1);

        camera.SetLogin(User, Secret);
        fake.Release();

        // Sent once the pan completed and its outcome was handled.
        await fake.WaitForProbeAsync(2);
        Assert.Equal("HTTP 500", camera.FailureReason);
        lock (reasons)
            Assert.Equal(new[] { "HTTP 500" }, reasons);
    }

    // As for Connected: a subscriber that throws is logged, never thrown into the send loop, which keeps
    // sending, or into the address setter's caller.
    [Fact]
    public async Task ASubscriberThatThrows_IsLogged_AndTheCameraKeepsSending()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = _ => XcReply.With(500, "0") };
        using var camera = new XcCamera(name, LongTimeout, Interval);
        camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(XcCamera.FailureReason))
                throw new InvalidOperationException("Simulated subscriber failure");
        };
        camera.Port = fake.Port;
        camera.Address = "127.0.0.1";

        var error = await XcTestCameras.WaitForLogAsync(trace, name, "subscriber failed");
        var probes = fake.Probes.Count;
        await fake.WaitUntilAsync(r => r.Count(x => x.IsProbe) >= probes + 2, "two more probes");
        camera.Address = "";

        Assert.Equal(TraceEventType.Error, error.Type);
        Assert.Contains($"a {nameof(XcCamera.FailureReason)} subscriber failed", error.Message);
        var log = XcTestCameras.LogFor(trace, name);
        Assert.Equal(2, log.Count(l => l.Type == TraceEventType.Error && l.Message.Contains("subscriber failed")));
        Assert.DoesNotContain(log, l => l.Message.Contains("unexpected error"));
    }

    // A fake camera whose every request fails as named, with the login and the request timeout the
    // camera is given.
    private static (FakeXcCamera Fake, string Username, string Password, TimeSpan RequestTimeout) Failing(string failure) => failure switch
    {
        "a refused login" => (new FakeXcCamera { Login = new XcLogin { Username = User, Password = Secret, Basic = true } }, User, "fake-xc-wrong-password", LongTimeout),
        "no login" => (new FakeXcCamera { Login = new XcLogin { Username = User, Password = Secret, Basic = true } }, "", "", LongTimeout),
        "livescope-status 301" => (new FakeXcCamera { Responder = _ => XcReply.Refused }, "", "", LongTimeout),
        "HTTP 500" => (new FakeXcCamera { Responder = _ => XcReply.With(500, "0") }, "", "", LongTimeout),
        "no answer" => (new FakeXcCamera { Responder = _ => XcReply.Never }, "", "", ShortTimeout),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
    };

    // The failure reason at each change the camera raises, in order. Subscribe it before any other
    // handler, so it records a change before a test waiting on it goes on.
    private static List<string?> ReasonsRaised(XcCamera camera)
    {
        var reasons = new List<string?>();
        camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(XcCamera.FailureReason))
            {
                lock (reasons)
                    reasons.Add(camera.FailureReason);
            }
        };
        return reasons;
    }

    // Completes when the camera raises a FailureReason change to the given value.
    private static Task ReasonBecomes(XcCamera camera, string? value)
    {
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (e.PropertyName == nameof(XcCamera.FailureReason) && camera.FailureReason == value)
            {
                camera.PropertyChanged -= handler;
                raised.TrySetResult();
            }
        };
        camera.PropertyChanged += handler;
        return raised.Task;
    }
}
