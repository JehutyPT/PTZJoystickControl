using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Model;
using Xunit;

namespace PtzJoystickControl.Tests.Companion;

public sealed class BitfocusCompanionListenerTests : IDisposable
{
    private readonly int _port = GetFreePort();
    private readonly BitfocusCompanionService _service;
    // A short timeout makes a response that is never closed fail the test instead of hanging it.
    private readonly HttpClient _client;
    private readonly RecordingTraceListener _trace = new();

    public BitfocusCompanionListenerTests()
    {
        Trace.Listeners.Add(_trace);
        _service = new BitfocusCompanionService(GetFreePort(), _port);
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
        var companionEvent = Assert.Single(raised);
        Assert.Same(BitfocusCompanionEvent.SupportedEvents[1], companionEvent);

        Assert.Contains(_trace.Messages, m => m.Contains("foo=1") && m.Contains("Unknown command 'foo'"));
        Assert.Contains(_trace.Messages, m => m.Contains("subscriber failed"));
    }

    [Fact]
    public async Task StoppingTheListener_IsLoggedAndNotThrownFromTheCallback()
    {
        _service.StopListening();

        // The pending accept completes with an error once the listener stops; it must be logged, not thrown.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !_trace.Messages.Any(m => m.Contains("Companion listener: failed to accept request")))
            await Task.Delay(20);

        Assert.Contains(_trace.Messages, m => m.Contains("Companion listener: failed to accept request"));
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
            var constructing = Record.Exception(() => service = new BitfocusCompanionService(GetFreePort(), blockedPort));

            Assert.Null(constructing);
            Assert.NotNull(service!.InboundListenerError);
            Assert.Contains($"http://localhost:{blockedPort}/", service.InboundListenerError);
            Assert.Contains(_trace.Messages, m => m.Contains("Companion listener: failed to start") && m.Contains($"http://localhost:{blockedPort}/"));

            var afterFailure = Record.Exception(() =>
            {
                service.PressButton(1, 1);
                service.SetCustomVariable("ptz_selected_camera", "1");
                service.StopListening();
            });
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
        var service = new BitfocusCompanionService(GetFreePort(), blockedPort, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
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
        var service = new BitfocusCompanionService(GetFreePort(), blockedPort, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100));
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

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public override void Write(string? message) => _messages.Enqueue(message ?? string.Empty);

        public override void WriteLine(string? message) => _messages.Enqueue(message ?? string.Empty);
    }
}
