using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// The XC camera's login: with a username set, a request the camera answers with HTTP 401 is retried
// once with Basic or Digest credentials, and later requests carry them up front. The fake camera's
// XcLogin checks every Authorization header; its Digest check is itself checked against the RFC
// examples first.
public class XcCameraLoginTests
{
    // Obviously fake credentials.
    private const string User = "fake-xc-user";
    private const string Secret = "fake-xc-password";

    // How long to keep collecting before asserting that nothing more was sent or logged.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(300);

    // Long enough that a held request is still outstanding when the test releases it, and that an idle
    // camera sends no probe of its own during a test.
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ABasicChallenge_IsAnsweredAtOnce_ThenEveryRequestCarriesBasicUpFront()
    {
        // Not ASCII: Basic carries them as UTF-8, which the fake decodes.
        const string user = "fäke-xc-üser", password = "fäke-xc-pässword";
        using var fake = new FakeXcCamera { Login = new XcLogin { Username = user, Password = password, Basic = true } };
        using var camera = XcTestCameras.CameraFor(fake, username: user, password: password);

        var requests = await PanThenStopAsync(camera, fake);

        AssertChallengedOnceThenAuthenticatedUpFront(requests, "Basic");
    }

    // The fake's Digest challenge offers qop "auth-int, auth", an opaque value and algorithm=MD5, and
    // its realm holds a quote and a backslash. It accepts an answer only with every value quoted and
    // escaped as the RFC requires, and nc counting up on its one nonce.
    [Fact]
    public async Task ADigestChallenge_IsAnsweredAtOnce_ThenEveryRequestAnswersItsNonceUpFront()
    {
        using var fake = new FakeXcCamera { Login = new XcLogin { Username = User, Password = Secret, Digest = true } };
        using var camera = XcTestCameras.CameraFor(fake, username: User, password: Secret);

        var requests = await PanThenStopAsync(camera, fake);

        AssertChallengedOnceThenAuthenticatedUpFront(requests, "Digest");
    }

    // RFC 2069's Digest, which RFC 7616 still allows: no qop, opaque or algorithm. The fake accepts an
    // answer only without qop, nc and cnonce, and with the response computed without them.
    [Fact]
    public async Task ADigestChallengeWithoutQop_IsAnsweredWithoutQop()
    {
        var login = new XcLogin { Username = User, Password = Secret, Digest = true, Qop = false, Opaque = null, Algorithm = null };
        using var fake = new FakeXcCamera { Login = login };
        using var camera = XcTestCameras.CameraFor(fake, username: User, password: Secret);

        var requests = await PanThenStopAsync(camera, fake);

        AssertChallengedOnceThenAuthenticatedUpFront(requests, "Digest");
    }

    // Offered Basic, Digest with SHA-256 and Digest with MD5, in that order, the camera answers Digest
    // with MD5: the only Digest it supports, and safer than Basic.
    [Fact]
    public async Task OfferedBasicAndDigest_TheCameraAnswersDigest()
    {
        var login = new XcLogin { Username = User, Password = Secret, Basic = true, Digest = true, Sha256 = true };
        using var fake = new FakeXcCamera { Login = login };
        using var camera = XcTestCameras.CameraFor(fake, username: User, password: Secret);

        var requests = await PanThenStopAsync(camera, fake);

        AssertChallengedOnceThenAuthenticatedUpFront(requests, "Digest");
        Assert.All(requests.Skip(1), r => Assert.Contains("algorithm=MD5", r.Authorization));
    }

    // An expired nonce: the camera challenges a request answering the old one, with or without
    // stale=true. The request is repeated at once on the new nonce, with no failure.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARenewedNonce_IsAnsweredAtOnce_WithNoFailure(bool stale)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var login = new XcLogin { Username = User, Password = Secret, Digest = true, Stale = stale };
        using var fake = new FakeXcCamera { Login = login };
        using var camera = XcTestCameras.CameraFor(fake, name, username: User, password: Secret);
        camera.Pan(0.5, PanDir.Right);
        var before = (await fake.WaitUntilAsync(r => r.Any(x => x.Pan == "right" && !x.Challenged), "the pan, accepted")).Count;

        login.RenewNonce();
        camera.Pan(0.7, PanDir.Left);
        var requests = await fake.WaitUntilAsync(r => r.Any(x => x.Pan == "left" && !x.Challenged), "the next pan, accepted");
        await Task.Delay(SettleTime);

        var since = requests.Skip(before).ToList();
        var described = FakeXcCamera.Describe(requests);
        Assert.NotNull(since[0].Authorization);
        Assert.True(since[0].Challenged, described);
        Assert.Equal(since[0].Target, since[1].Target);
        Assert.False(since[1].Challenged, described);
        Assert.DoesNotContain(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
    }

    // A camera may challenge again on the nonce it gave before. nc counts the requests sent on a nonce,
    // so it goes on from there: the fake refuses an nc it has seen on the nonce.
    [Fact]
    public async Task AChallengeOnTheSameNonce_CountsNcOnFromTheRequestsAlreadySent()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var login = new XcLogin { Username = User, Password = Secret, Digest = true };
        var withCredentials = 0;
        using var fake = new FakeXcCamera
        {
            // The second request with credentials, the first sent with them up front, is answered with
            // the login's challenge on the same nonce, though the login accepted it.
            Responder = r => r.Authorization != null && Interlocked.Increment(ref withCredentials) == 2
                ? XcReply.Unauthorized(login.Challenges().ToArray())
                : XcReply.Ok,
            Login = login,
        };
        using var camera = XcTestCameras.CameraFor(fake, name, username: User, password: Secret);

        var requests = await PanThenStopAsync(camera, fake);
        await Task.Delay(SettleTime);

        // The request challenged on the same nonce was repeated at once, and the login accepted it.
        var described = FakeXcCamera.Describe(requests);
        Assert.Equal(requests[2].Target, requests[3].Target);
        Assert.All(requests.Skip(1), r => Assert.False(r.Challenged, described));
        Assert.DoesNotContain(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
    }
    // RFC 2617 section 3.5: Digest with qop=auth and no algorithm.
    private const string Rfc2617Authorization =
        "Digest username=\"Mufasa\", realm=\"testrealm@host.com\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", "
        + "uri=\"/dir/index.html\", qop=auth, nc=00000001, cnonce=\"0a4f113b\", "
        + "response=\"6629fae49393a05397450978507c4ef1\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"";

    // RFC 7616 section 3.9.1: Digest with qop=auth and algorithm=MD5.
    private const string Rfc7616Authorization =
        "Digest username=\"Mufasa\", realm=\"http-auth@example.org\", uri=\"/dir/index.html\", algorithm=MD5, "
        + "nonce=\"7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v\", nc=00000001, "
        + "cnonce=\"f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ\", qop=auth, "
        + "response=\"8ca523f5e9506fed4657c9700eebdbec\", opaque=\"FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS\"";

    [Fact]
    public void TheFakesDigestCheck_AcceptsTheRfc2617Example()
    {
        Assert.True(Rfc2617Login().Accepts(Rfc2617Authorization, "/dir/index.html", out _));
    }

    [Fact]
    public void TheFakesDigestCheck_AcceptsTheRfc7616Md5Example()
    {
        var login = new XcLogin
        {
            Username = "Mufasa",
            Password = "Circle of Life",
            Digest = true,
            Realm = "http-auth@example.org",
            Nonce = "7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v",
            Opaque = "FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS",
        };

        Assert.True(login.Accepts(Rfc7616Authorization, "/dir/index.html", out _));
    }

    // Each a change to the RFC 2617 example that the check must refuse: a wrong response, a value
    // quoted or unquoted against the RFC, another uri, a missing opaque.
    [Theory]
    [InlineData("response=\"6629fae49393a05397450978507c4ef1\"", "response=\"6629fae49393a05397450978507c4ef2\"")]
    [InlineData("qop=auth", "qop=\"auth\"")]
    [InlineData("username=\"Mufasa\"", "username=Mufasa")]
    [InlineData("nc=00000001", "nc=1")]
    [InlineData("uri=\"/dir/index.html\"", "uri=\"/dir/index.htm\"")]
    [InlineData(", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"", "")]
    public void TheFakesDigestCheck_RefusesTheRfc2617ExampleChanged(string part, string changed)
    {
        Assert.Contains(part, Rfc2617Authorization);

        Assert.False(Rfc2617Login().Accepts(Rfc2617Authorization.Replace(part, changed), "/dir/index.html", out _));
    }

    [Fact]
    public void TheFakesDigestCheck_RefusesANonceCountUsedBefore()
    {
        var login = Rfc2617Login();
        Assert.True(login.Accepts(Rfc2617Authorization, "/dir/index.html", out _));

        Assert.False(login.Accepts(Rfc2617Authorization, "/dir/index.html", out _));
    }

    [Fact]
    public async Task WithoutALogin_A401IsAFailureLoggedOnce_NothingIsRepeated_AndARefusedStopKeepsItsPace()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Login = new XcLogin { Username = User, Password = Secret, Basic = true, Digest = true } };
        using var camera = XcTestCameras.CameraFor(fake, name);

        camera.Stop();
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) > 3, "the stop retried after its three sends");
        var stops = await StopsOverOneSecondAsync(fake);

        var warning = Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
        Assert.Contains("camera requires a login; set username and password", warning.Message);
        Assert.All(fake.Requests, r => Assert.Null(r.Authorization));
        // One HTTP request per send, each send the repeat interval after the previous one completed.
        AssertAtLeastApart(stops, 1);
        Assert.True(stops.Count <= 21, FakeXcCamera.Describe(stops));
    }

    [Fact]
    public async Task WithAWrongPassword_TheRepeatIsRefused_AFailureLoggedOnce_AtMostTwoRequestsPerSend()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Login = new XcLogin { Username = User, Password = Secret, Digest = true } };
        using var camera = XcTestCameras.CameraFor(fake, name, username: User, password: "fake-xc-wrong-password");

        camera.Stop();
        // Two HTTP requests per send: more than six is the stop retried after its three sends.
        await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) > 6, "the stop retried after its three sends");
        var stops = await StopsOverOneSecondAsync(fake);

        var warning = Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
        Assert.Contains("camera refused the username and password", warning.Message);
        Assert.All(stops, r => Assert.True(r.Challenged, FakeXcCamera.Describe(stops)));
        // Two HTTP requests per send (the request and its repeat), each send the repeat interval after
        // the previous one completed.
        AssertAtLeastApart(stops, 2);
        Assert.True(stops.Count <= 42, FakeXcCamera.Describe(stops));
    }

    // Failures alternate between a 401 and another reason, each logged when the reason changes: the
    // login failure only the first time, as it is logged at most once a minute.
    [Fact]
    public async Task ALoginFailure_IsLoggedAtMostOnceAMinute_ThoughTheFailureReasonKeepsChanging()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var answered = 0;
        using var fake = new FakeXcCamera
        {
            Responder = _ => Interlocked.Increment(ref answered) % 2 == 0 ? XcReply.With(500, "0") : XcReply.Unauthorized("Basic realm=\"fake\""),
        };
        using var camera = XcTestCameras.CameraFor(fake, name);

        camera.Stop();
        await fake.WaitUntilAsync(r => r.Count >= 8, "eight requests");
        await Task.Delay(SettleTime);

        var warnings = XcTestCameras.LogFor(trace, name).Where(l => l.Type == TraceEventType.Warning).ToList();
        Assert.Single(warnings, l => l.Message.Contains("camera requires a login"));
        Assert.True(warnings.Count(l => l.Message.Contains("HTTP 500")) >= 3, string.Join(" | ", warnings.Select(l => l.Message)));
    }

    // Digest with SHA-256 or MD5-sess, qop auth-int alone, another scheme, or no challenge at all: a
    // failure, not repeated. The log names the schemes and algorithms, never the realm or nonce.
    [Theory]
    [InlineData("Digest SHA-256, Negotiate", "Digest realm=\"fake-secret-realm\", nonce=\"fake-secret-nonce\", algorithm=SHA-256", "Negotiate")]
    [InlineData("Digest MD5-sess", "Digest realm=\"fake-secret-realm\", nonce=\"fake-secret-nonce\", qop=\"auth\", algorithm=MD5-sess")]
    [InlineData("Digest", "Digest realm=\"fake-secret-realm\", nonce=\"fake-secret-nonce\", qop=\"auth-int\"")]
    [InlineData("none")]
    public async Task AChallengeTheCameraCannotAnswer_IsAFailure_NotRepeated_LoggedByItsSchemeAlone(string offered, params string[] challenges)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = _ => XcReply.Unauthorized(challenges) };
        using var camera = XcTestCameras.CameraFor(fake, name, username: User, password: Secret);

        camera.Pan(0.5, PanDir.Right);
        await XcTestCameras.WaitForLogAsync(trace, name, "failed");
        await fake.WaitForControlAsync(1);
        await Task.Delay(SettleTime);

        // One line, though the probe fails too when it goes before the pan: a login failure is logged
        // at most once a minute.
        var warning = Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
        Assert.Contains($"the camera asks for a login the app does not support (offered: {offered})", warning.Message);
        Assert.DoesNotContain("fake-secret", warning.Message);
        Assert.Single(fake.ControlRequests);
        Assert.All(fake.Requests, r => Assert.Null(r.Authorization));
    }

    // The camera stops offering the login it accepted, as when it is reconfigured: the request that
    // answered the old challenge up front fails, and the next ones go without credentials.
    [Fact]
    public async Task AChallengeTheCameraCannotAnswer_AfterOneItCould_EndsTheCredentialsUpFront()
    {
        using var fake = new FakeXcCamera
        {
            Login = new XcLogin { Username = User, Password = Secret, Basic = true },
            // Decided by the request, not by when it arrives: the pan is accepted, each stop refused.
            Responder = r => r.IsStop ? XcReply.Unauthorized("Negotiate") : XcReply.Ok,
        };
        using var camera = XcTestCameras.CameraFor(fake, username: User, password: Secret);
        camera.Pan(0.5, PanDir.Right);
        await fake.WaitUntilAsync(r => r.Any(x => x.Pan == "right" && !x.Challenged), "the pan, accepted");

        // From now on the fake checks no login, and offers the stops Negotiate alone.
        fake.Login = null;
        camera.Stop();
        var requests = await fake.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 3, "three stops");

        var described = FakeXcCamera.Describe(requests);
        var stops = requests.Where(r => r.IsStop).ToList();
        Assert.True(stops[0].Authorization != null, described);
        Assert.All(stops.Skip(1), r => Assert.True(r.Authorization == null, described));
    }

    // XC's "unknown operator", a command failure as in #47, never a login challenge.
    [Fact]
    public async Task Livescope401OnHttp200_IsACommandFailure_NotRepeatedWithCredentials()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Responder = r => r.IsControl ? XcReply.With(200, "401") : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, name, username: User, password: Secret);

        camera.Pan(0.5, PanDir.Right);
        var warning = await XcTestCameras.WaitForLogAsync(trace, name, "failed");
        await Task.Delay(SettleTime);

        Assert.Contains("livescope-status 401", warning.Message);
        Assert.Single(fake.ControlRequests);
        Assert.All(fake.Requests, r => Assert.Null(r.Authorization));
    }

    // The request in flight when the login changes finishes with the old one, and its outcome is
    // ignored: its refusal logs nothing, though the change lets the next login failure log at once.
    // The next request, a probe as soon as the gap allows, carries the new login, and only once the
    // camera challenged it.
    [Fact]
    public async Task ALoginChange_AppliesToTheNextRequest_WithNothingLoggedFromTheRequestInFlight()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera
        {
            Login = new XcLogin { Username = User, Password = Secret, Digest = true },
            Responder = XcTestCameras.HoldingTheFirstControlRequest(),
        };
        // The probe interval is long: a probe after the change comes from the change itself.
        using var camera = XcTestCameras.CameraFor(fake, name, requestTimeout: LongTimeout, probeInterval: LongTimeout,
            username: User, password: "fake-xc-wrong-password");
        await XcTestCameras.WaitForLogAsync(trace, name, "refused the username and password");
        camera.Pan(0.5, PanDir.Right);
        await fake.WaitForControlAsync(1);
        var before = fake.Requests.Count;

        camera.Password = Secret;
        fake.Release();

        await XcTestCameras.WaitForLogAsync(trace, name, "succeed again");
        await Task.Delay(SettleTime);
        var requests = fake.Requests;
        var described = FakeXcCamera.Describe(requests);
        Assert.Single(XcTestCameras.LogFor(trace, name), l => l.Type == TraceEventType.Warning);
        // The held move's repeat, still with the old login; then the probe, without the new login
        // until the camera challenges it, and repeated with it.
        var after = requests.Skip(before).ToList();
        Assert.True(after[0].IsControl && after[0].Challenged, described);
        Assert.True(after[1].IsProbe && after[1].Authorization == null, described);
        Assert.True(after[2].IsProbe && after[2].Authorization != null && !after[2].Challenged, described);
    }

    // A failure logged within the minute holds back the next login failure, but not after a change of
    // login.
    [Fact]
    public async Task ALoginChange_LetsTheNextLoginFailureLogAtOnce()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera { Login = new XcLogin { Username = User, Password = Secret, Digest = true } };
        using var camera = XcTestCameras.CameraFor(fake, name, probeInterval: LongTimeout, username: User, password: "fake-xc-wrong-password");
        await XcTestCameras.WaitForLogAsync(trace, name, "refused the username and password");

        camera.Password = "fake-xc-other-wrong-password";

        await XcTestCameras.WaitForLogAsync(trace, name, "refused the username and password", count: 2);
    }

    // The challenge belongs to the address and port that gave it: pointed elsewhere, the camera sends
    // no credentials there before that camera challenges.
    [Fact]
    public async Task APortChange_SendsNoCredentialsToTheNewCamera_BeforeItChallenges()
    {
        using var first = new FakeXcCamera { Login = new XcLogin { Username = User, Password = Secret, Basic = true } };
        using var second = new FakeXcCamera();
        using var camera = XcTestCameras.CameraFor(first, username: User, password: Secret);
        camera.Pan(0.5, PanDir.Right);
        await first.WaitUntilAsync(r => r.Any(x => x.Pan == "right" && x.Authorization != null && !x.Challenged), "the pan, with Basic up front");

        camera.Port = second.Port;
        camera.Pan(0.5, PanDir.Left);
        camera.Stop();
        await second.WaitUntilAsync(r => r.Count(x => x.IsStop) >= 3, "three stops");

        Assert.All(second.Requests, r => Assert.Null(r.Authorization));
    }

    // Another name for the same camera is another address: the camera forgets the challenge, and the
    // first request there goes without credentials until the camera challenges it.
    [Fact]
    public async Task AnAddressChange_SendsNoCredentialsToTheNewAddress_BeforeItChallenges()
    {
        using var fake = new FakeXcCamera { Login = new XcLogin { Username = User, Password = Secret, Basic = true } };
        // The request timeout is long: localhost may be tried over IPv6 first, where the fake does not
        // listen. The probe interval is long: a probe after the change comes from the change itself.
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout, probeInterval: LongTimeout, username: User, password: Secret);
        camera.Pan(0.5, PanDir.Right);
        await fake.WaitUntilAsync(r => r.Any(x => x.Pan == "right" && x.Authorization != null && !x.Challenged), "the pan, with Basic up front");
        var before = fake.Requests.Count;

        camera.Address = "localhost";
        var requests = await fake.WaitUntilAsync(r => r.Skip(before).Any(x => !x.Challenged), "a request to the new address, accepted");

        var described = FakeXcCamera.Describe(requests);
        var after = requests.Skip(before).ToList();
        Assert.True(after[0].IsProbe && after[0].Authorization == null && after[0].Challenged, described);
        Assert.True(after[1].IsProbe && after[1].Authorization != null && !after[1].Challenged, described);
    }

    // Every log line, from every test running meanwhile, and every URL the camera requested: none holds
    // the username, the password or an Authorization value the camera sent. The login is refused,
    // then accepted, so both are logged.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheLogAndTheUrls_NeverHoldTheLoginOrAnAuthorizationValue(bool basic)
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var user = $"fake-xc-user-{Guid.NewGuid():N}";
        var password = $"fake-xc-password-{Guid.NewGuid():N}";
        using var fake = new FakeXcCamera { Login = new XcLogin { Username = user, Password = password, Basic = basic, Digest = !basic } };
        using var camera = XcTestCameras.CameraFor(fake, name, username: user, password: $"{password}-wrong");
        await XcTestCameras.WaitForLogAsync(trace, name, "refused the username and password");

        camera.Password = password;
        await XcTestCameras.WaitForLogAsync(trace, name, "succeed again");
        var requests = await PanThenStopAsync(camera, fake);

        var authorizations = requests.Select(r => r.Authorization).OfType<string>().ToList();
        Assert.NotEmpty(authorizations);
        var secrets = new[] { user, password }
            .Concat(authorizations)
            .Concat(authorizations.Select(a => a[(a.IndexOf(' ') + 1)..]))
            .ToList();
        var lines = trace.Events.Select(e => e.Message).ToList();
        Assert.All(secrets, secret => Assert.DoesNotContain(lines, l => l.Contains(secret)));
        Assert.All(requests, r => Assert.DoesNotContain(user, r.Target));
        Assert.All(requests, r => Assert.DoesNotContain(password, r.Target));
    }

    // Digest carries the username as it is, and HTTP headers only ASCII: the repeat cannot be sent. A
    // failure, logged without the username.
    [Fact]
    public async Task AUsernameBeyondAscii_CannotAnswerDigest_AndItsFailureLogDoesNotHoldIt()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        var user = $"fäke-xc-üser-{Guid.NewGuid():N}";
        using var fake = new FakeXcCamera { Login = new XcLogin { Username = user, Password = Secret, Digest = true } };
        using var camera = XcTestCameras.CameraFor(fake, name, username: user, password: Secret);

        var warning = await XcTestCameras.WaitForLogAsync(trace, name, "failed");
        await Task.Delay(SettleTime);

        Assert.Equal(TraceEventType.Warning, warning.Type);
        Assert.DoesNotContain(trace.Events, e => e.Message.Contains(user));
        Assert.All(fake.Requests, r => Assert.Null(r.Authorization));
    }

    // The challenged request and its repeat each take 400 ms to answer: each within the 500 ms timeout,
    // but together they outlast the one timeout they share, so the request fails.
    [Fact]
    public async Task AChallengeAndItsRepeat_ShareTheRequestsOneTimeout()
    {
        using var trace = new TraceRecorder();
        var name = XcTestCameras.UniqueName();
        using var fake = new FakeXcCamera
        {
            Login = new XcLogin { Username = User, Password = Secret, Digest = true },
            Responder = _ => XcReply.After(TimeSpan.FromMilliseconds(400)),
        };
        // The probe interval is long: the first probe is the camera's only request.
        using var camera = XcTestCameras.CameraFor(fake, name, requestTimeout: TimeSpan.FromMilliseconds(500), probeInterval: LongTimeout, username: User, password: Secret);

        var warning = await XcTestCameras.WaitForLogAsync(trace, name, "failed");

        Assert.Contains("no answer within 500 ms", warning.Message);
    }

    // The camera's nonce expired: the fake holds its answer to the challenged move. The calls return
    // without waiting for it, or for the repeat answering the challenge.
    [Fact]
    public async Task MovementAndStopCalls_ReturnWhileTheCameraHoldsAChallengedRequest()
    {
        var login = new XcLogin { Username = User, Password = Secret, Digest = true };
        using var fake = new FakeXcCamera { Login = login, Responder = r => r.IsControl ? XcReply.Held : XcReply.Ok };
        using var camera = XcTestCameras.CameraFor(fake, requestTimeout: LongTimeout, username: User, password: Secret);
        await fake.WaitUntilAsync(r => r.Any(x => x.IsProbe && !x.Challenged), "the probe, accepted");
        login.RenewNonce();
        camera.Pan(0.5, PanDir.Right);
        Assert.True((await fake.WaitForControlAsync(1)).Challenged);

        // On another thread, so a call that waited on the held request fails the test instead of hanging it.
        var calls = Task.Run(() =>
        {
            camera.Tilt(0.5, TiltDir.Up);
            camera.Pan(0.2, PanDir.Left);
            camera.Stop();
        });

        // The fake is still holding the challenged request: the calls returned without waiting for its answer.
        await calls.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fake.ControlRequests);
    }

    // The requests from the camera's first stop on, over one second of it repeating and retrying stops.
    private static async Task<List<XcRequest>> StopsOverOneSecondAsync(FakeXcCamera fake)
    {
        var first = (await fake.WaitUntilAsync(r => r.Any(x => x.IsStop), "a stop")).First(x => x.IsStop);
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        return fake.Requests
            .Where(r => r.ArrivedAt >= first.ArrivedAt && Stopwatch.GetElapsedTime(first.ArrivedAt, r.ArrivedAt) <= TimeSpan.FromSeconds(1))
            .ToList();
    }

    // Asserts each request arrived at least the stop repeat interval after the one perSend requests
    // before it: with perSend HTTP requests per send, consecutive sends are that far apart.
    private static void AssertAtLeastApart(IReadOnlyList<XcRequest> requests, int perSend)
    {
        for (var i = perSend; i < requests.Count; i++)
        {
            var gap = Stopwatch.GetElapsedTime(requests[i - perSend].ArrivedAt, requests[i].ArrivedAt);
            Assert.True(gap >= XcCamera.StopRepeatInterval,
                $"Requests {i - perSend} and {i} arrived {gap.TotalMilliseconds:0.0} ms apart. {FakeXcCamera.Describe(requests)}");
        }
    }

    // Pans and stops; returns every request once the pan and three stops were accepted.
    private static async Task<IReadOnlyList<XcRequest>> PanThenStopAsync(XcCamera camera, FakeXcCamera fake)
    {
        camera.Pan(0.5, PanDir.Right);
        await fake.WaitUntilAsync(r => r.Any(x => x.Pan == "right" && !x.Challenged), "the pan, accepted");
        camera.Pan(0.5, PanDir.Stop);
        return await fake.WaitUntilAsync(r => r.Count(x => x.IsStop && !x.Challenged) >= 3, "three stops, accepted");
    }

    // The camera's first request had no credentials and was challenged, and the next repeated it with
    // credentials; every request from then on carried credentials of the scheme and was accepted.
    private static void AssertChallengedOnceThenAuthenticatedUpFront(IReadOnlyList<XcRequest> requests, string scheme)
    {
        var described = FakeXcCamera.Describe(requests);
        Assert.Null(requests[0].Authorization);
        Assert.True(requests[0].Challenged, described);
        Assert.Equal(requests[0].Target, requests[1].Target);
        Assert.All(requests.Skip(1), r =>
        {
            Assert.False(r.Challenged, described);
            Assert.StartsWith(scheme + " ", r.Authorization);
        });
    }

    private static XcLogin Rfc2617Login() => new()
    {
        Username = "Mufasa",
        Password = "Circle Of Life",
        Digest = true,
        Realm = "testrealm@host.com",
        Nonce = "dcd98b7102dd2f0e8b11d0f600bfb0c093",
        Opaque = "5ccc069c403ebaf9f0171e9517f40e41",
        Algorithm = null,
    };
}
