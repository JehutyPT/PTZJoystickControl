using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;
using PtzJoystickControl.Tests.Services;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Companion;

public sealed class BitfocusCompanionListenerTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly int _port = GetFreePort();
    private readonly BitfocusCompanionService _service;
    // A short timeout makes a response that is never closed fail the test instead of hanging it.
    private readonly HttpClient _client;
    private readonly RecordingTraceListener _trace = new();
    private static readonly ICamera Cam1 = new ViscaIpDevice("Cam 1") { CompanionNumber = 1 };
    private static readonly ICamera Cam2 = new ViscaIpDevice("Cam 2") { CompanionNumber = 2 };
    private readonly FakeCameras _cameras = new(Cam1, Cam2);
    // Three speed profiles with the middle one active, so a step can go either way and then stop at an end.
    private readonly MemorySpeedProfileStore _speedProfileStore = new(new SpeedProfileList(new[]
    {
        new SpeedProfile("Fine", 10, 10, 20, 30),
        new SpeedProfile("Medium", 40, 40, 50, 60),
        new SpeedProfile("Full", 100, 100, 100, 100),
    }, activePosition: 2));
    private readonly SpeedProfilesService _speedProfiles;
    private int _speedProfileChanges;

    public BitfocusCompanionListenerTests()
    {
        Trace.Listeners.Add(_trace);
        _speedProfiles = new SpeedProfilesService(_speedProfileStore);
        _speedProfiles.ProfilesChanged += (_, _) => Interlocked.Increment(ref _speedProfileChanges);
        _service = new BitfocusCompanionService(Settings(_port), _cameras, _speedProfiles);
        _client = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{_port}/"),
            Timeout = TimeSpan.FromSeconds(3),
        };
    }

    public void Dispose()
    {
        _client.Dispose();
        _service.StopListening();
        Trace.Listeners.Remove(_trace);
    }

    [Fact]
    public async Task Listener_AnswersAndClosesEveryRequest_AndKeepsServing()
    {
        var raised = new List<BitfocusCompanionEvent>();
        EventHandler<BitfocusCompanionEvent> throwing = (_, _) => throw new InvalidOperationException("subscriber failed");
        EventHandler<BitfocusCompanionEvent> recording = (_, e) => raised.Add(e);

        _service.ReceivedBitfocusCommand += recording;
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf("?foo=1"));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf("?SetCameraPreview=3"));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(""));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf("favicon.ico"));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf("?SetCameraPreview=1&foo=1"));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf("?SetCameraPreview=1&SetCameraPreview=2"));
        Assert.Empty(raised);
        _service.ReceivedBitfocusCommand -= recording;

        _service.ReceivedBitfocusCommand += throwing;
        Assert.Equal(HttpStatusCode.InternalServerError, await StatusOf("?SetCameraPreview=1"));
        _service.ReceivedBitfocusCommand -= throwing;

        _service.ReceivedBitfocusCommand += recording;
        Assert.Equal(HttpStatusCode.OK, await StatusOf("?SetCameraPreview=2"));
        Assert.Equal(HttpStatusCode.OK, await StatusOf("?SelectCamera=1"));
        Assert.Equal(new[] { Cam2, Cam1 }, raised.Select(e => e.Camera));

        Assert.Contains(_trace.Messages, m => m.Contains("foo=1") && m.Contains("Unknown command 'foo'"));
        Assert.Contains(_trace.Messages, m => m.Contains("subscriber failed"));
    }

    [Fact]
    public async Task UnknownOrSharedCompanionNumber_Gets400AndAWarning_AndRaisesNothing()
    {
        var raised = new List<BitfocusCompanionEvent>();
        _service.ReceivedBitfocusCommand += (_, e) => raised.Add(e);

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf("?SelectCamera=3"));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf("?SelectCamera=abc"));

        // A camera added later with a number another camera has: an arbitrary pick is never made.
        _cameras.Cameras.Add(new ViscaIpDevice("Cam 2 copy") { CompanionNumber = 2 });
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf("?SetCameraPreview=2"));

        Assert.Empty(raised);
        Assert.Contains(_trace.Messages, m => m.Contains("SelectCamera=3") && m.Contains("No camera has Companion number 3"));
        Assert.Contains(_trace.Messages, m => m.Contains("SetCameraPreview=2") && m.Contains("Cam 2, Cam 2 copy"));
    }

    [Theory]
    [InlineData("faster", 3, "Full")]
    [InlineData("finer", 1, "Fine")]
    public async Task SpeedProfileStep_IsAppliedSavedAndAnnounced_BeforeIts200WithAnEmptyBody(string step, int activeAfter, string activeName)
    {
        var raised = new List<BitfocusCompanionEvent>();
        _service.ReceivedBitfocusCommand += (_, e) => raised.Add(e);

        using var response = await _client.GetAsync($"?SpeedProfile={step}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(activeAfter, _speedProfiles.Profiles.ActivePosition);
        Assert.Equal(activeName, _speedProfiles.Profiles.Active.Name);
        Assert.Same(_speedProfiles.Profiles, Assert.Single(_speedProfileStore.Saves));
        Assert.Equal(1, Volatile.Read(ref _speedProfileChanges));
        // A step is a settings change, not a Companion event: nothing is queued on the input loop.
        Assert.Empty(raised);
    }

    [Theory]
    [InlineData("faster", 3)]
    [InlineData("finer", 1)]
    public async Task SpeedProfileStep_PastEitherEnd_Gets200_AndChangesSavesAndLogsNothing(string step, int end)
    {
        Assert.Equal(HttpStatusCode.OK, await StatusOf($"?SpeedProfile={step}"));
        Assert.Equal(end, _speedProfiles.Profiles.ActivePosition);
        using var trace = new TraceRecorder();

        Assert.Equal(HttpStatusCode.OK, await StatusOf($"?SpeedProfile={step}"));
        Assert.Equal(HttpStatusCode.OK, await StatusOf($"?SpeedProfile={step}"));

        // Clamped, never wrapped: only the first step, onto the end, was saved and announced.
        Assert.Equal(end, _speedProfiles.Profiles.ActivePosition);
        Assert.Single(_speedProfileStore.Saves);
        Assert.Equal(1, Volatile.Read(ref _speedProfileChanges));
        // Only the listener's lines: Trace is global, and other test classes log speed-profile refusals in
        // parallel. The service has nothing to log here: at either end a step just leaves the list as it is.
        Assert.DoesNotContain(trace.Events, e =>
            e.Type is TraceEventType.Warning or TraceEventType.Error && e.Message.Contains("Companion listener"));
    }

    [Theory]
    [InlineData("?SelectCamera=2&SpeedProfile=faster", 3)]
    [InlineData("?SpeedProfile=finer&SelectCamera=2", 1)]
    public async Task CameraSelectionWithAStep_IsRaisedBeforeTheStepIsApplied(string query, int activeAfter)
    {
        var seen = new List<(ICamera Camera, int ActivePosition)>();
        _service.ReceivedBitfocusCommand += (_, e) => seen.Add((e.Camera, _speedProfiles.Profiles.ActivePosition));

        Assert.Equal(HttpStatusCode.OK, await StatusOf(query));

        // Whatever the parameter order, the selection is queued first, so a slow save never delays it.
        Assert.Equal(new[] { (Cam2, 2) }, seen);
        Assert.Equal(activeAfter, _speedProfiles.Profiles.ActivePosition);
        Assert.Single(_speedProfileStore.Saves);
    }

    [Fact]
    public async Task Current_ReturnsTheActiveSpeedProfileAsJson_AndChangesNothing()
    {
        Assert.Equal((2, 3, "Medium"), await CurrentSpeedProfile());
        Assert.Empty(_speedProfileStore.Saves);
        Assert.Equal(0, Volatile.Read(ref _speedProfileChanges));

        // Sent after a step's 200, it reports the speed profile that step made active.
        Assert.Equal(HttpStatusCode.OK, await StatusOf("?SpeedProfile=finer"));
        Assert.Equal((1, 3, "Fine"), await CurrentSpeedProfile());
    }

    [Fact]
    public async Task Current_JsonEscapesTheName()
    {
        const string name = "Wide \"A\" \\ café – 2";
        _speedProfiles.Edit(2, new SpeedProfile(name, 40, 40, 50, 60));

        Assert.Equal((2, 3, name), await CurrentSpeedProfile());
    }

    [Theory]
    [InlineData("?SpeedProfile=Faster")]
    [InlineData("?SpeedProfile=up")]
    [InlineData("?SpeedProfile=")]
    [InlineData("?SpeedProfile")]
    [InlineData("?SpeedProfile=faster&SpeedProfile=finer")]
    [InlineData("?SpeedProfile=current&SelectCamera=1")]
    [InlineData("?SpeedProfile=faster&SelectCamera=9")]
    public async Task InvalidSpeedProfileRequest_Gets400AndAWarning_AndRunsNothing(string query)
    {
        var raised = new List<BitfocusCompanionEvent>();
        _service.ReceivedBitfocusCommand += (_, e) => raised.Add(e);
        using var trace = new TraceRecorder();

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(query));

        Assert.Equal(2, _speedProfiles.Profiles.ActivePosition);
        Assert.Empty(_speedProfileStore.Saves);
        Assert.Equal(0, Volatile.Read(ref _speedProfileChanges));
        Assert.Empty(raised);
        Assert.Contains(trace.Events, e =>
            e.Type == TraceEventType.Warning && e.Message.Contains($"Companion listener: rejected request '{query}'"));
    }

    [Fact]
    public async Task CameraSelection_StillGets200WithAnEmptyBody_AndLeavesTheSpeedProfileAlone()
    {
        using var response = await _client.GetAsync("?SelectCamera=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, _speedProfiles.Profiles.ActivePosition);
        Assert.Empty(_speedProfileStore.Saves);
    }

    [Fact]
    public async Task WhileAStepIsSaved_TheActiveSpeedProfileIsReadWithoutWaiting_AndThe200WaitsForTheSave()
    {
        var store = new HeldSaveStore(new SpeedProfileList(new[]
        {
            new SpeedProfile("Fine", 10, 10, 20, 30),
            new SpeedProfile("Full", 100, 100, 100, 100),
        }, activePosition: 1));
        var speedProfiles = new SpeedProfilesService(store);
        var port = GetFreePort();
        var service = new BitfocusCompanionService(Settings(port), _cameras, speedProfiles);
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/"), Timeout = Patience };
        try
        {
            var step = client.GetAsync("?SpeedProfile=faster");
            Assert.True(store.SaveStarted.Wait(Patience), "the step's save never started");

            // Read off the listener thread, as the movement commands read it on the input loop: the read
            // does not wait for the save (a wait would time out here), and it already shows the step.
            var active = await Task.Run(() => speedProfiles.Profiles.Active.Name).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal("Full", active);
            Assert.False(step.IsCompleted, "the step was answered before its save finished");

            store.ReleaseSave.Set();
            using var response = await step;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            store.ReleaseSave.Set();
            service.StopListening();
        }
    }

    [Fact]
    public async Task AStepThatThrows_Gets500AndAnError_AndTheNextRequestIsServed()
    {
        var port = GetFreePort();
        var service = new BitfocusCompanionService(Settings(port), _cameras, new ThrowingSpeedProfiles());
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/"), Timeout = TimeSpan.FromSeconds(3) };
        using var trace = new TraceRecorder();
        try
        {
            using (var failed = await client.GetAsync("?SpeedProfile=faster"))
                Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Contains(trace.Events, e =>
                e.Type == TraceEventType.Error && e.Message.Contains("'?SpeedProfile=faster'") && e.Message.Contains("step failed"));

            using var next = await client.GetAsync("?SelectCamera=1");
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        }
        finally
        {
            service.StopListening();
        }
    }

    [Fact]
    public async Task StopListening_WhileAccepting_LogsNoWarning_AndRaisesNoError()
    {
        var changes = 0;
        _service.InboundListenerErrorChanged += (_, _) => Interlocked.Increment(ref changes);

        _service.StopListening();

        // The pending accept completes with an error once the listener closes; a deliberate stop must end it silently.
        await Task.Delay(1000);

        Assert.DoesNotContain(_trace.Messages, m => m.Contains("Companion listener: failed to accept request"));
        Assert.DoesNotContain(_trace.Messages, m => m.Contains("Companion listener: failed to listen for the next request"));
        Assert.DoesNotContain(_trace.Messages, m => m.Contains("Companion listener: unexpected failure"));
        Assert.Null(_service.InboundListenerError);
        Assert.Equal(0, Volatile.Read(ref changes));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => StatusOf("?SetCameraPreview=1"));
    }

    [Fact]
    public void Listener_IsReportedAsListening_WhenThePortIsFree()
    {
        Assert.Null(_service.InboundListenerError);
    }

    [Fact]
    public void PortInUse_DoesNotThrow_IsLoggedAndReported_AndTheServiceStaysUsable()
    {
        var blockedPort = GetFreePort();
        var (blocker, blockerV6) = HoldPort(blockedPort);
        try
        {
            BitfocusCompanionService? service = null;
            var constructing = Record.Exception(() => service = new BitfocusCompanionService(Settings(blockedPort), _cameras, _speedProfiles));

            Assert.Null(constructing);
            Assert.NotNull(service!.InboundListenerError);
            Assert.Contains($"http://localhost:{blockedPort}/", service.InboundListenerError);
            Assert.Contains(_trace.Messages, m => m.Contains("Companion listener: failed to start") && m.Contains($"http://localhost:{blockedPort}/"));

            var afterFailure = Record.Exception(() => service.StopListening());
            Assert.Null(afterFailure);
        }
        finally
        {
            blocker.Stop();
            blockerV6.Stop();
        }
    }

    [Fact]
    public void RetryDelay_DoublesFromTheInitialDelay_AndIsCappedAtTheMaximum()
    {
        var initial = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(30);

        var delays = Enumerable.Range(1, 9).Select(attempt => BitfocusCompanionService.RetryDelay(attempt, initial, max));

        Assert.Equal(new[] { 1.0, 2, 4, 8, 16, 30, 30, 30, 30 }, delays.Select(d => d.TotalSeconds).ToArray());
        Assert.Equal(max, BitfocusCompanionService.RetryDelay(int.MaxValue, initial, max));
    }

    [Fact]
    public void RetryFailures_AreLoggedOnlyOccasionally()
    {
        var logged = Enumerable.Range(1, 200).Where(BitfocusCompanionService.ShouldLogRetryFailure).ToArray();

        Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 64, 128 }, logged);
    }

    [Fact]
    public async Task PortInUse_IsRetried_UntilTheListenerStarts_AndTheErrorClears()
    {
        var blockedPort = GetFreePort();
        var (blocker, blockerV6) = HoldPort(blockedPort);
        var service = new BitfocusCompanionService(Settings(blockedPort), _cameras, _speedProfiles, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
        var changes = 0;
        service.InboundListenerErrorChanged += (_, _) => Interlocked.Increment(ref changes);
        try
        {
            Assert.NotNull(service.InboundListenerError);
            await Task.Delay(300);
            Assert.NotNull(service.InboundListenerError);

            blocker.Stop();
            blockerV6.Stop();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && service.InboundListenerError != null)
                await Task.Delay(20);

            Assert.Null(service.InboundListenerError);
            Assert.True(Volatile.Read(ref changes) >= 1);
            Assert.Contains(_trace.Messages, m => m.Contains("Companion listener: listening on") && m.Contains($"http://localhost:{blockedPort}/"));
            Assert.Single(_trace.Messages, m => m.Contains("Companion listener: failed to start") && m.Contains($"http://localhost:{blockedPort}/"));

            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{blockedPort}/"), Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync("?SetCameraPreview=1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            blocker.Stop();
            blockerV6.Stop();
            service.StopListening();
        }
    }

    [Fact]
    public async Task StopListening_CancelsThePendingRetry()
    {
        var blockedPort = GetFreePort();
        var (blocker, blockerV6) = HoldPort(blockedPort);
        var service = new BitfocusCompanionService(Settings(blockedPort), _cameras, _speedProfiles, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100));
        try
        {
            Assert.Null(Record.Exception(service.StopListening));
        }
        finally
        {
            blocker.Stop();
            blockerV6.Stop();
        }

        await Task.Delay(500);

        Assert.NotNull(service.InboundListenerError);
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{blockedPort}/"), Timeout = TimeSpan.FromSeconds(1) };
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("?SetCameraPreview=1"));
    }

    // Hold the port with plain sockets, as another process would. Both loopback addresses are
    // held: which one the managed HttpListener binds "localhost" to differs by OS.
    private static (TcpListener V4, TcpListener V6) HoldPort(int port)
    {
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        var blockerV6 = new TcpListener(IPAddress.IPv6Loopback, port);
        blockerV6.Start();
        return (blocker, blockerV6);
    }

    private async Task<HttpStatusCode> StatusOf(string pathAndQuery)
    {
        using var response = await _client.GetAsync(pathAndQuery);
        return response.StatusCode;
    }

    // Asks for the active speed profile; the response must be exactly the documented JSON body.
    private async Task<(int Position, int Count, string Name)> CurrentSpeedProfile()
    {
        using var response = await _client.GetAsync("?SpeedProfile=current");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var root = body.RootElement;
        Assert.Equal(new[] { "position", "count", "name" }, root.EnumerateObject().Select(p => p.Name));
        return (root.GetProperty("position").GetInt32(), root.GetProperty("count").GetInt32(), root.GetProperty("name").GetString()!);
    }

    private static CompanionSettings Settings(int listenerPort) =>
        new() { ListenerPort = listenerPort };

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeCameras : ICamerasService
    {
        public FakeCameras(params ICamera[] cameras) => Cameras = new ObservableCollection<ICamera>(cameras);

        public ObservableCollection<ICamera> Cameras { get; }

        public void AddCamera(ICamera camera) => Cameras.Add(camera);

        public void RemoveCamera(ICamera camera) => Cameras.Remove(camera);
    }

    // Holds each save until the test releases it (at most Patience), as a slow disk would.
    private sealed class HeldSaveStore : ISpeedProfileSettingsStore
    {
        private readonly SpeedProfileList _stored;

        public HeldSaveStore(SpeedProfileList stored) => _stored = stored;

        public ManualResetEventSlim SaveStarted { get; } = new();

        public ManualResetEventSlim ReleaseSave { get; } = new();

        public SpeedProfileList GetSpeedProfiles() => _stored;

        public bool SaveSpeedProfiles(SpeedProfileList speedProfiles)
        {
            SaveStarted.Set();
            ReleaseSave.Wait(Patience);
            return true;
        }
    }

    // A speed-profiles service whose steps throw.
    private sealed class ThrowingSpeedProfiles : ISpeedProfilesService
    {
        public SpeedProfileList Profiles { get; } =
            new(new[] { new SpeedProfile("Full", 100, 100, 100, 100) }, activePosition: 1);

        public event EventHandler? ProfilesChanged { add { } remove { } }

        public void StepFaster() => throw new InvalidOperationException("step failed");
        public void StepFiner() => throw new InvalidOperationException("step failed");
        public void SetActive(int position) { }
        public void Add() { }
        public void Edit(int position, SpeedProfile edited) { }
        public void MoveUp(int position) { }
        public void MoveDown(int position) { }
        public void Remove(int position) { }
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public override void Write(string? message) => _messages.Enqueue(message ?? string.Empty);

        public override void WriteLine(string? message) => _messages.Enqueue(message ?? string.Empty);
    }
}
