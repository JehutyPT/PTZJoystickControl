using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PtzJoystickControl.Tests.Devices;

/// <summary>
/// A loopback XC camera: a minimal HTTP/1.1 server that records every request (path, query, arrival
/// time, connection) and answers as <see cref="Responder"/> decides: with an HTTP status and
/// <c>livescope-status</c>, after the test releases it, never, or by closing the connection.
/// Connections are kept alive, so a client that reuses one is served on it.
/// </summary>
internal sealed class FakeXcCamera : IDisposable
{
    public const string ControlPath = "/-wvhttp-01-/control.cgi";
    public const string InfoPath = "/-wvhttp-01-/info.cgi";

    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly List<XcRequest> _requests = new();
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _accepted;
    private int _inFlight;
    private int _maxInFlight;

    public FakeXcCamera()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public int Port { get; }

    /// <summary>Decides how each request is answered. Called on the fake's own tasks. Default: HTTP 200, <c>livescope-status: 0</c>.</summary>
    public Func<XcRequest, XcReply> Responder { get; set; } = _ => XcReply.Ok;

    /// <summary>Connections accepted so far.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>
    /// The most requests received and not yet answered at one time. A held request counts until it is
    /// released, even if the client gave up on it.
    /// </summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public IReadOnlyList<XcRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public IReadOnlyList<XcRequest> ControlRequests => Requests.Where(r => r.IsControl).ToList();

    public IReadOnlyList<XcRequest> Probes => Requests.Where(r => r.IsProbe).ToList();

    /// <summary>Answers every request held by <see cref="XcReply.Held"/> so far.</summary>
    public void Release() => Interlocked.Exchange(ref _release, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Waits until <paramref name="condition"/> holds for the requests received so far, and returns them.</summary>
    public async Task<IReadOnlyList<XcRequest>> WaitUntilAsync(Func<IReadOnlyList<XcRequest>, bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)((timeout ?? DefaultWait).TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            var requests = Requests;
            if (condition(requests))
                return requests;
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"Timed out waiting for {what}. {Describe(requests)}");
            await Task.Delay(5);
        }
    }

    /// <summary>Waits for the <paramref name="count"/>th control request and returns it.</summary>
    public async Task<XcRequest> WaitForControlAsync(int count, TimeSpan? timeout = null)
    {
        var requests = await WaitUntilAsync(r => r.Count(x => x.IsControl) >= count, $"{count} control request(s)", timeout);
        return requests.Where(r => r.IsControl).ElementAt(count - 1);
    }

    /// <summary>Waits for the <paramref name="count"/>th probe and returns it.</summary>
    public async Task<XcRequest> WaitForProbeAsync(int count, TimeSpan? timeout = null)
    {
        var requests = await WaitUntilAsync(r => r.Count(x => x.IsProbe) >= count, $"{count} probe(s)", timeout);
        return requests.Where(r => r.IsProbe).ElementAt(count - 1);
    }

    public static string Describe(IEnumerable<XcRequest> requests)
    {
        var list = requests.ToList();
        if (list.Count == 0)
            return "Received: nothing";
        var first = list[0].ArrivedAt;
        return "Received: " + string.Join(" | ", list.Select(r =>
            $"+{Stopwatch.GetElapsedTime(first, r.ArrivedAt).TotalMilliseconds:0} ms {r.Path[(r.Path.LastIndexOf('/') + 1)..]}?{r.Query}"));
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        foreach (var client in _clients.Keys)
            client.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception)
            {
                return;
            }

            _clients[client] = 0;
            _ = Serve(client, Interlocked.Increment(ref _accepted));
        }
    }

    private async Task Serve(TcpClient client, int connection)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var pending = new StringBuilder();
            while (true)
            {
                // A request head can arrive in pieces; GET requests carry no body.
                int end;
                while ((end = pending.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
                {
                    var read = await stream.ReadAsync(buffer, _stopping.Token);
                    if (read == 0)
                        return;
                    pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var arrivedAt = Stopwatch.GetTimestamp();
                var head = pending.ToString(0, end);
                pending.Remove(0, end + 4);
                var target = head.Split("\r\n")[0].Split(' ')[1];
                var question = target.IndexOf('?');
                var request = new XcRequest(
                    question < 0 ? target : target[..question],
                    question < 0 ? "" : target[(question + 1)..],
                    arrivedAt,
                    connection);
                lock (_requests)
                    _requests.Add(request);

                var inFlight = Interlocked.Increment(ref _inFlight);
                try
                {
                    int max;
                    while (inFlight > (max = Volatile.Read(ref _maxInFlight))
                        && Interlocked.CompareExchange(ref _maxInFlight, inFlight, max) != max)
                    {
                    }

                    var reply = Responder(request);
                    switch (reply.Kind)
                    {
                        case XcReplyKind.Close:
                            return;
                        case XcReplyKind.Never:
                            // Until the client gives up and closes the connection.
                            while (await stream.ReadAsync(buffer, _stopping.Token) > 0)
                            {
                            }
                            return;
                        case XcReplyKind.Held:
                            await Volatile.Read(ref _release).Task.WaitAsync(_stopping.Token);
                            break;
                    }

                    var response = new StringBuilder($"HTTP/1.1 {reply.Status} {(reply.Status == 200 ? "OK" : "Error")}\r\n");
                    if (reply.Livescope != null)
                        response.Append($"livescope-status: {reply.Livescope}\r\n");
                    response.Append("Content-Length: 0\r\n\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), _stopping.Token);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }
        catch (Exception)
        {
            // The client aborted the connection (a timed-out request) or the fake is stopping.
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }
}

internal sealed record XcRequest(string Path, string Query, long ArrivedAt, int Connection)
{
    public bool IsControl => Path == FakeXcCamera.ControlPath;

    public bool IsProbe => Path == FakeXcCamera.InfoPath;

    public string? Parameter(string name) =>
        Query.Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == name) is { Length: 2 } pair ? pair[1] : null;

    public string? Pan => Parameter("c.1.pan");

    public string? Tilt => Parameter("c.1.tilt");

    public bool IsStop => IsControl && Pan == "stop" && Tilt == "stop";
}

internal enum XcReplyKind { Answer, Held, Never, Close }

internal sealed record XcReply(XcReplyKind Kind, int Status = 200, string? Livescope = "0")
{
    public static readonly XcReply Ok = new(XcReplyKind.Answer);

    /// <summary>Answered with HTTP 200 and <c>livescope-status: 0</c> once the test calls <see cref="FakeXcCamera.Release"/>.</summary>
    public static readonly XcReply Held = new(XcReplyKind.Held);

    /// <summary>Read and never answered; the connection stays open until the client closes it.</summary>
    public static readonly XcReply Never = new(XcReplyKind.Never);

    /// <summary>The connection is closed without an answer.</summary>
    public static readonly XcReply Close = new(XcReplyKind.Close);

    public static XcReply With(int status, string? livescope) => new(XcReplyKind.Answer, status, livescope);
}
