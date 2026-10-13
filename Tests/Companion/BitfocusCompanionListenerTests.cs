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
        // _service already holds _port, as another process would.
        var exception = Record.Exception(() =>
        {
            var service = new BitfocusCompanionService(GetFreePort(), _port);

            Assert.NotNull(service.InboundListenerError);
            Assert.Contains($"http://localhost:{_port}/", service.InboundListenerError);

            service.PressButton(1, 1);
            service.SetCustomVariable("ptz_selected_camera", "1");
            service.StopListening();
        });

        Assert.Null(exception);
        Assert.Contains(_trace.Messages, m => m.Contains("Companion listener: failed to start") && m.Contains($"http://localhost:{_port}/"));
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
