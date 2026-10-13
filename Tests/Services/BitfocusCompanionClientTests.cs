using System.Diagnostics;
using System.Net;
using System.Text;
using PtzJoystickControl.Application.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Services;

public class BitfocusCompanionClientTests
{
    private static readonly Uri BaseAddress = new("http://companion.test:8000/");

    [Fact]
    public async Task PressButtonAsync_SendsGetToLegacyPressBankPath()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new BitfocusCompanionClient(handler, BaseAddress, TimeSpan.FromSeconds(2));

        await client.PressButtonAsync(3, 7);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("http://companion.test:8000/press/bank/3/7"), request.RequestUri);
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData(3, null)]
    [InlineData(null, null)]
    public async Task PressButtonAsync_WithMissingPageOrButton_SendsNothing(int? page, int? button)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new BitfocusCompanionClient(handler, BaseAddress, TimeSpan.FromSeconds(2));

        await client.PressButtonAsync(page, button);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PressButtonAsync_CompletesWhenTransportFaults()
    {
        var handler = new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused")));
        var client = new BitfocusCompanionClient(handler, BaseAddress, TimeSpan.FromSeconds(2));

        await client.PressButtonAsync(3, 7);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PressButtonAsync_CompletesWhenTransportThrowsSynchronously()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("socket exploded"));
        var client = new BitfocusCompanionClient(handler, BaseAddress, TimeSpan.FromSeconds(2));

        await client.PressButtonAsync(3, 7);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PressButtonAsync_CompletesWhenCompanionReturnsServerError()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = new BitfocusCompanionClient(handler, BaseAddress, TimeSpan.FromSeconds(2));

        await client.PressButtonAsync(3, 7);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PressButtonAsync_CompletesWhenRequestTimesOut()
    {
        var handler = new StubHandler(NeverResponds);
        var client = new BitfocusCompanionClient(handler, BaseAddress, TimeSpan.FromMilliseconds(100));

        var press = client.PressButtonAsync(3, 7);
        var finished = await Task.WhenAny(press, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(press, finished);
        await press;
    }

    public static IEnumerable<object[]> FailingTransports() => new[]
    {
        new object[] { new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused"))), "Connection refused" },
        new object[] { new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>((_, _) => throw new InvalidOperationException("socket exploded")), "socket exploded" },
        new object[] { new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))), "500" },
        new object[] { new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>(NeverResponds), "timed out" },
    };

    [Theory]
    [MemberData(nameof(FailingTransports))]
    public async Task PressButtonAsync_TracesFailureWithPathAndReason(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> transport, string expectedReason)
    {
        var client = new BitfocusCompanionClient(new StubHandler(transport), BaseAddress, TimeSpan.FromMilliseconds(100));

        var log = await CaptureTrace(() => client.PressButtonAsync(3, 7));

        Assert.Contains("press/bank/3/7", log);
        Assert.Contains(expectedReason, log);
    }

    [Fact]
    public async Task PressButtonAsync_DoesNotTraceOnSuccess()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new BitfocusCompanionClient(handler, BaseAddress, TimeSpan.FromSeconds(2));

        var log = await CaptureTrace(() => client.PressButtonAsync(3, 7));

        Assert.DoesNotContain("press/bank/3/7", log);
    }

    [Fact]
    public void DefaultTimeout_IsAtMostTwoSeconds()
    {
        var client = new BitfocusCompanionClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), BaseAddress);

        Assert.InRange(client.Timeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SetCustomVariableAsync_SendsGetToCustomVariablePath()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new BitfocusCompanionClient(handler, BaseAddress, TimeSpan.FromSeconds(2));

        await client.SetCustomVariableAsync("ptz_selected_camera", "2");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("http://companion.test:8000/set/custom-variable/ptz_selected_camera?value=2"), request.RequestUri);
    }

    [Theory]
    [MemberData(nameof(FailingTransports))]
    public async Task SetCustomVariableAsync_CompletesAndTracesFailureWithPathAndReason(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> transport, string expectedReason)
    {
        var client = new BitfocusCompanionClient(new StubHandler(transport), BaseAddress, TimeSpan.FromMilliseconds(100));

        var log = await CaptureTrace(() => client.SetCustomVariableAsync("ptz_selected_camera", "2"));

        Assert.Contains("set/custom-variable/ptz_selected_camera", log);
        Assert.Contains(expectedReason, log);
    }

    public static IEnumerable<object[]> FireAndForgetCalls() => new[]
    {
        new object[] { new Action<BitfocusCompanionClient>(c => c.PressButton(3, 7)) },
        new object[] { new Action<BitfocusCompanionClient>(c => c.SetCustomVariable("ptz_selected_camera", "2")) },
    };

    [Theory]
    [MemberData(nameof(FireAndForgetCalls))]
    public void FireAndForget_ReturnsImmediatelyWhenTransportBlocksSynchronously(Action<BitfocusCompanionClient> call)
    {
        using var release = new ManualResetEventSlim();
        var handler = new StubHandler((_, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var client = new BitfocusCompanionClient(handler, BaseAddress, TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            call(client);
            stopwatch.Stop();
        }
        finally
        {
            release.Set();
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), $"Call blocked for {stopwatch.Elapsed}");
    }

    [Theory]
    [MemberData(nameof(FireAndForgetCalls))]
    public void FireAndForget_DoesNotThrowWhenTransportThrowsSynchronously(Action<BitfocusCompanionClient> call)
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("socket exploded"));
        var client = new BitfocusCompanionClient(handler, BaseAddress, TimeSpan.FromSeconds(2));

        var exception = Record.Exception(() => call(client));

        Assert.Null(exception);
    }

    [Theory]
    [MemberData(nameof(FireAndForgetCalls))]
    public async Task FireAndForget_SendsTheRequestInTheBackground(Action<BitfocusCompanionClient> call)
    {
        var sent = new TaskCompletionSource<HttpRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler((request, _) =>
        {
            sent.TrySetResult(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var client = new BitfocusCompanionClient(handler, BaseAddress, TimeSpan.FromSeconds(2));

        call(client);

        Assert.Same(sent.Task, await Task.WhenAny(sent.Task, Task.Delay(TimeSpan.FromSeconds(5))));
    }

    private static async Task<string> CaptureTrace(Func<Task> action)
    {
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            await action();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
        return listener.Text;
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly StringBuilder _text = new();

        public string Text { get { lock (_text) return _text.ToString(); } }

        public override void Write(string? message) { lock (_text) _text.Append(message); }

        public override void WriteLine(string? message) { lock (_text) _text.AppendLine(message); }
    }

    private static Task<HttpResponseMessage> NeverResponds(HttpRequestMessage _, CancellationToken cancellationToken)
    {
        var never = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => never.TrySetCanceled(cancellationToken));
        return never.Task;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request);
            return _send(request, cancellationToken);
        }
    }
}
