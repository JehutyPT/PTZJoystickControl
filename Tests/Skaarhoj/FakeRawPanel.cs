using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PtzJoystickControl.Tests.Skaarhoj;

/// <summary>
/// A loopback Raw Panel server: answers <c>ping</c> with <c>ack</c>, sends greeting lines on accept,
/// and records what the link sends and how many connections it holds at once.
/// </summary>
internal sealed class FakeRawPanel : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentQueue<string> _received = new();
    private readonly ConcurrentQueue<long> _acceptedAt = new();
    private readonly object _countLock = new();
    private int _open;
    private int _maxOpen;
    private int _closedByLink;
    private long _lastAckSentAt;
    private long _silentUntil;

    public FakeRawPanel(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public int Port { get; }

    /// <summary>Lines sent to each new client right after accept, e.g. <c>map=</c> lines and optionally <c>RDY</c>.</summary>
    public string[] Greeting { get; set; } = { "map=1:1", "map=2:2" };

    /// <summary>When false, pings are read and ignored, as with the cable pulled.</summary>
    public volatile bool AnswerPings = true;

    /// <summary>Pings received this long after accept go unanswered (the panel's post-connect stall).</summary>
    public TimeSpan SilentAfterAccept { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Pings received before this <see cref="Stopwatch.GetTimestamp"/> go unanswered, on every
    /// connection (a stall at any time, such as the panel waking). 0 for none.
    /// </summary>
    public long SilentUntil
    {
        get => Interlocked.Read(ref _silentUntil);
        set => Interlocked.Exchange(ref _silentUntil, value);
    }

    /// <summary>The first ping is answered at once even during the silence, as seen on the panel.</summary>
    public bool AnswerFirstPingAtOnce { get; set; }

    /// <summary>Close each connection right after accepting it, without answering anything.</summary>
    public volatile bool CloseOnAccept;

    public int MaxConcurrentClients { get { lock (_countLock) return _maxOpen; } }

    public int OpenClients { get { lock (_countLock) return _open; } }

    public int Accepted => _acceptedAt.Count;

    /// <summary>Connections the link closed (the fake read end-of-stream).</summary>
    public int ClosedByLink => Volatile.Read(ref _closedByLink);

    public IReadOnlyList<long> AcceptedAt => _acceptedAt.ToArray();

    public IEnumerable<string> Received => _received;

    /// <summary><see cref="Stopwatch.GetTimestamp"/> of the last <c>ack</c> sent, or 0.</summary>
    public long LastAckSentAt => Interlocked.Read(ref _lastAckSentAt);

    /// <summary>Sends <paramref name="lines"/> to every connected client.</summary>
    public void Send(params string[] lines)
    {
        var bytes = Encoding.ASCII.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        foreach (var client in _clients.Keys)
        {
            try { client.GetStream().Write(bytes); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Closes every connection but keeps listening, as when the panel restarts: the link sees the
    /// close at once, without waiting for its ping timeout, and reconnects.
    /// </summary>
    public void DropConnections()
    {
        foreach (var client in _clients.Keys)
            client.Dispose();
    }

    /// <summary>
    /// Stops listening, then closes every connection, as when the panel is switched off: the link
    /// sees the close at once and its reconnects are refused, so it stays down.
    /// </summary>
    public void TurnOff()
    {
        _listener.Stop();
        DropConnections();
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

            // Registered for Send before it is counted, so a test that waits on OpenClients can send.
            _clients[client] = 0;
            _acceptedAt.Enqueue(Stopwatch.GetTimestamp());
            lock (_countLock)
            {
                _open++;
                _maxOpen = Math.Max(_maxOpen, _open);
            }
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        var acceptedAt = Stopwatch.GetTimestamp();
        var closedByLink = false;
        try
        {
            if (CloseOnAccept)
                return;

            var stream = client.GetStream();
            if (Greeting.Length > 0)
                await stream.WriteAsync(Encoding.ASCII.GetBytes(string.Concat(Greeting.Select(l => l + "\n"))), _stopping.Token);

            var pending = new StringBuilder();
            var buffer = new byte[1024];
            var pings = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, _stopping.Token);
                if (read == 0)
                {
                    closedByLink = true;
                    return;
                }

                pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                string text;
                int newline;
                while ((newline = (text = pending.ToString()).IndexOf('\n')) >= 0)
                {
                    var line = text[..newline];
                    pending.Remove(0, newline + 1);
                    _received.Enqueue(line);
                    if (line != "ping")
                        continue;

                    pings++;
                    var silent = Stopwatch.GetElapsedTime(acceptedAt) < SilentAfterAccept
                        && !(AnswerFirstPingAtOnce && pings == 1)
                        || Stopwatch.GetTimestamp() < SilentUntil;
                    if (AnswerPings && !silent)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("ack\n"), _stopping.Token);
                        Interlocked.Exchange(ref _lastAckSentAt, Stopwatch.GetTimestamp());
                    }
                }
            }
        }
        catch (Exception)
        {
            // A reset from the link closing is also a close by the link.
            closedByLink = !_stopping.IsCancellationRequested;
        }
        finally
        {
            _clients.TryRemove(client, out _);
            lock (_countLock)
                _open--;
            if (closedByLink)
                Interlocked.Increment(ref _closedByLink);
            client.Dispose();
        }
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
