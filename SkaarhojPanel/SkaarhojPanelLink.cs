using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// Owns the one TCP connection to a SKAARHOJ panel's Raw Panel server (docs/skaarhoj-integration.md).
/// Reports the link up as soon as TCP connects (without waiting for <c>RDY</c>). Any line from the panel
/// counts as hearing from it; the first <c>ping</c> goes out with the startup queries, and each later one
/// only after a ping interval without a line or a ping, and the link reports down after the timeout without a line, counted only after a grace period from
/// connect and again from <c>_isSleeping=0</c> (the panel stalls for ~0.5 s after every connect and for
/// more than the timeout when it wakes). A freeze of the app itself restarts the count (see
/// <see cref="PanelDeadMan"/>). On down it closes the socket and reconnects with capped backoff. Every line
/// received is parsed and raised as a message.
/// <para>
/// Everything runs on one background task; events are raised there, and a subscriber that throws is
/// logged. Nothing escapes onto a thread-pool thread (live-safety §2). Moves no camera.
/// </para>
/// </summary>
public sealed class SkaarhojPanelLink : IDisposable
{
    public static readonly TimeSpan DefaultPingInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultSilenceTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultConnectGrace = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultReconnectInitialDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultReconnectMaxDelay = TimeSpan.FromSeconds(30);
    // Without a bound, a connect with the cable pulled waits out the OS's SYN retries (tens of seconds).
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);
    private static readonly byte[] PingBytes = Encoding.ASCII.GetBytes("ping\n");
    private static readonly byte[] GreetingBytes = Encoding.ASCII.GetBytes("list\nConnections?\n");
    // Replies worth a log line; everything else (input events above all) is never logged per message.
    private static readonly HashSet<string> LoggedReplies = new() { "model", "version", "connections" };

    // Set while this link raises an event, so Dispose from a subscriber does not wait on its own task.
    [ThreadStatic] private static bool t_raisingEvent;

    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _pingInterval;
    private readonly TimeSpan _silenceTimeout;
    private readonly TimeSpan _connectGrace;
    private readonly TimeSpan _reconnectInitialDelay;
    private readonly TimeSpan _reconnectMaxDelay;
    private readonly TimeSpan _connectTimeout;

    private readonly CancellationTokenSource _stopping = new();
    // Guards _started, _disposed and _client so Dispose can never miss a socket being opened.
    private readonly object _lock = new();
    private bool _started;
    private bool _disposed;
    private TcpClient? _client;
    private Task? _loop;

    private volatile bool _isUp;
    // Whether the current connection has received an ack: only then does the reconnect backoff start over.
    private volatile bool _acked;
    private int _unknownLines;

    public SkaarhojPanelLink(string host, int port)
        : this(host, port, DefaultPingInterval, DefaultSilenceTimeout, DefaultConnectGrace,
            DefaultReconnectInitialDelay, DefaultReconnectMaxDelay, DefaultConnectTimeout)
    {
    }

    internal SkaarhojPanelLink(
        string host,
        int port,
        TimeSpan pingInterval,
        TimeSpan silenceTimeout,
        TimeSpan connectGrace,
        TimeSpan reconnectInitialDelay,
        TimeSpan reconnectMaxDelay,
        TimeSpan connectTimeout)
    {
        _host = host;
        _port = port;
        _pingInterval = pingInterval;
        _silenceTimeout = silenceTimeout;
        _connectGrace = connectGrace;
        _reconnectInitialDelay = reconnectInitialDelay;
        _reconnectMaxDelay = reconnectMaxDelay;
        _connectTimeout = connectTimeout;
    }

    /// <summary>Raised on the link's task when the TCP connection opens.</summary>
    public event EventHandler? LinkUp;

    /// <summary>Raised on the link's task when a connection that was up is closed, with the reason.</summary>
    public event EventHandler<string>? LinkDown;

    /// <summary>Raised on the link's task for every line received, including <see cref="RawPanelMessage.Unknown"/>.</summary>
    public event EventHandler<RawPanelMessage>? MessageReceived;

    public bool IsUp => _isUp;

    /// <summary>The panel's address, as configured.</summary>
    public string Host => _host;

    public int Port => _port;

    private string Endpoint => $"{_host}:{_port}";

    /// <summary>
    /// Reads <paramref name="settingsFilePath"/> and returns a link, not yet started, when it names a
    /// panel; returns null, and logs nothing, when no address is set. Subscribe, then call
    /// <see cref="Start"/>. Never throws.
    /// </summary>
    public static SkaarhojPanelLink? CreateIfConfigured(string settingsFilePath)
    {
        try
        {
            var settings = new SkaarhojPanelSettingsStore(settingsFilePath).Load();
            if (!settings.IsConfigured)
                return null;

            if (settings.Port is < 1 or > 65535)
            {
                Trace.TraceError($"SKAARHOJ panel: port {settings.Port} in {settingsFilePath} is not a valid TCP port; the panel stays off.");
                return null;
            }

            var address = settings.Address!.Trim();
            Trace.TraceInformation($"SKAARHOJ panel: connecting to {address}:{settings.Port}");
            return new SkaarhojPanelLink(address, settings.Port);
        }
        catch (Exception e)
        {
            Trace.TraceError($"SKAARHOJ panel: failed to start the link from {settingsFilePath}; the panel stays off: {e}");
            return null;
        }
    }

    /// <summary>Starts connecting in the background. Does nothing if already started or disposed.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed)
                return;
            _started = true;
            _loop = Task.Run(() => RunAsync(_stopping.Token));
        }
    }

    /// <summary>
    /// Cancels pings and reconnects, closes the socket and waits briefly for the link's task to end
    /// (raising <see cref="LinkDown"/> if it was up). Never throws.
    /// </summary>
    public void Dispose()
    {
        TcpClient? client;
        Task? loop;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            client = _client;
            loop = _loop;
        }

        try
        {
            _stopping.Cancel();
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"SKAARHOJ panel: failed to cancel the link to {Endpoint}: {e}");
        }

        CloseQuietly(client);

        if (loop == null || t_raisingEvent)
            return;
        try
        {
            if (!loop.Wait(DisposeWait))
                Trace.TraceWarning($"SKAARHOJ panel: the link to {Endpoint} did not stop within {DisposeWait.TotalSeconds:0.#} s");
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"SKAARHOJ panel: the link to {Endpoint} stopped with an error: {e}");
        }
    }

    // The link's only task. Runs until disposed; nothing may escape it.
    private async Task RunAsync(CancellationToken stopping)
    {
        // Consecutive connections that ended without a single ack, and consecutive connect failures.
        var attempt = 0;
        var connectFailures = 0;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var (outcome, connectError) = await RunConnectionAsync(stopping).ConfigureAwait(false);
                if (outcome == ConnectionOutcome.ConnectFailed)
                    connectFailures++;
                else
                    connectFailures = 0;

                // A connection the panel answered was healthy: start the backoff over. One that never got
                // an ack keeps growing it, so a panel that accepts and then stalls is not hammered.
                if (outcome == ConnectionOutcome.Acked)
                    attempt = 0;
                attempt++;

                if (stopping.IsCancellationRequested)
                    break;

                var delay = ReconnectBackoff.Delay(attempt, _reconnectInitialDelay, _reconnectMaxDelay);
                if (outcome == ConnectionOutcome.ConnectFailed && ReconnectBackoff.ShouldLog(connectFailures))
                    Trace.TraceWarning($"SKAARHOJ panel: cannot connect to {Endpoint} (attempt {connectFailures}), retrying in {delay.TotalSeconds:0.##} s: {connectError}");

                await Task.Delay(delay, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (stopping.IsCancellationRequested)
            {
                // Dispose closed the socket mid-connect or mid-session: a clean stop, not an error.
                break;
            }
            catch (Exception e)
            {
                // Should not happen; keep the link alive rather than lose the panel for the rest of the show.
                Trace.TraceError($"SKAARHOJ panel: unexpected error in the link to {Endpoint}, reconnecting: {e}");
                try
                {
                    await Task.Delay(_reconnectInitialDelay, stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private enum ConnectionOutcome { ConnectFailed, NoAck, Acked }

    // One connection: connect, run until it goes down, close. Throws only OperationCanceledException on stopping.
    private async Task<(ConnectionOutcome Outcome, string? ConnectError)> RunConnectionAsync(CancellationToken stopping)
    {
        var client = new TcpClient { NoDelay = true };
        lock (_lock)
        {
            if (_disposed)
            {
                // Dispose is cancelling the loop; its wait below ends at once.
                client.Dispose();
                return (ConnectionOutcome.NoAck, null);
            }
            _client = client;
        }

        try
        {
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                connectTimeout.CancelAfter(_connectTimeout);
                await client.ConnectAsync(_host, _port, connectTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (!stopping.IsCancellationRequested)
            {
                var error = e is OperationCanceledException ? $"no answer within {_connectTimeout.TotalSeconds:0.##} s" : e.Message;
                return (ConnectionOutcome.ConnectFailed, error);
            }

            return (await RunSessionAsync(client, stopping).ConfigureAwait(false), null);
        }
        finally
        {
            lock (_lock)
                _client = null;
            CloseQuietly(client);
        }
    }

    private async Task<ConnectionOutcome> RunSessionAsync(TcpClient client, CancellationToken stopping)
    {
        var deadMan = new PanelDeadMan(Stopwatch.GetTimestamp(), _pingInterval, _silenceTimeout, _connectGrace);
        _acked = false;
        _isUp = true;
        Trace.TraceInformation($"SKAARHOJ panel: link up to {Endpoint}");
        Raise(LinkUp, EventArgs.Empty, nameof(LinkUp));

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        string reason;
        try
        {
            var stream = client.GetStream();
            var reading = ReadAsync(stream, deadMan, session.Token);
            var pinging = PingAsync(stream, deadMan, session.Token);
            var watching = WatchAsync(deadMan, session.Token);

            var first = await Task.WhenAny(reading, pinging, watching).ConfigureAwait(false);
            reason = stopping.IsCancellationRequested ? "stopped" : await first.ConfigureAwait(false);

            // Closing the socket unblocks a read or write still pending.
            session.Cancel();
            CloseQuietly(client);
            await Task.WhenAll(reading, pinging, watching).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            reason = stopping.IsCancellationRequested ? "stopped" : $"error: {e.Message}";
            if (!stopping.IsCancellationRequested)
                Trace.TraceError($"SKAARHOJ panel: unexpected error on the link to {Endpoint}: {e}");
        }

        _isUp = false;
        if (stopping.IsCancellationRequested)
            Trace.TraceInformation($"SKAARHOJ panel: link down from {Endpoint} (stopped)");
        else
            Trace.TraceWarning($"SKAARHOJ panel: link down from {Endpoint}: {reason}");
        Raise(LinkDown, reason, nameof(LinkDown));

        return _acked ? ConnectionOutcome.Acked : ConnectionOutcome.NoAck;
    }

    // Returns why the connection should go down. Never throws.
    private async Task<string> ReadAsync(NetworkStream stream, PanelDeadMan deadMan, CancellationToken session)
    {
        try
        {
            var framer = new LineFramer();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, session).ConfigureAwait(false);
                if (read == 0)
                    return "the panel closed the connection";

                var dropped = framer.DroppedLines;
                var droppedEnded = framer.EndedDroppedLines;
                foreach (var line in framer.Append(buffer, 0, read))
                    Handle(line, deadMan);
                // An over-long line the framer discards is still a complete line from the panel.
                if (framer.EndedDroppedLines != droppedEnded)
                    deadMan.LineHeard(Stopwatch.GetTimestamp());
                if (framer.DroppedLines != dropped)
                    Trace.TraceWarning($"SKAARHOJ panel: dropped an over-long line from {Endpoint} ({framer.DroppedLines} so far on this connection)");
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            return "stopped";
        }
        catch (Exception e)
        {
            return $"read failed: {e.Message}";
        }
    }

    private void Handle(string line, PanelDeadMan deadMan)
    {
        // Any complete line proves the panel is alive, whatever it says and even if it does not parse.
        var heardAt = Stopwatch.GetTimestamp();
        deadMan.LineHeard(heardAt);

        RawPanelMessage message;
        try
        {
            message = RawPanelParser.Parse(line);
        }
        catch (Exception e)
        {
            // The parser does not throw; this keeps a future bug in it from dropping the link.
            Trace.TraceError($"SKAARHOJ panel: failed to parse '{line}': {e}");
            return;
        }

        switch (message)
        {
            case RawPanelMessage.Ack:
                _acked = true;
                break;
            case RawPanelMessage.Reply { Key: "isSleeping", Value: "0" }:
                // Waking stalls the panel for longer than the timeout: give it the same grace as a connect.
                deadMan.Woke(heardAt);
                break;
            case RawPanelMessage.Reply reply when LoggedReplies.Contains(reply.Key):
                Trace.TraceInformation($"SKAARHOJ panel {Endpoint}: _{reply.Key}={reply.Value}");
                break;
            case RawPanelMessage.Unknown unknown:
                var count = Interlocked.Increment(ref _unknownLines);
                if (ReconnectBackoff.ShouldLog(count))
                    Trace.TraceWarning($"SKAARHOJ panel: ignored unrecognised line from {Endpoint} '{unknown.Line}' ({count} so far)");
                break;
        }

        Raise(MessageReceived, message, nameof(MessageReceived));
    }

    // Sends the startup queries and a first ping, then a ping whenever the panel has been quiet for a ping
    // interval since the last line or ping. Returns why the connection should go down. Never throws.
    private async Task<string> PingAsync(NetworkStream stream, PanelDeadMan deadMan, CancellationToken session)
    {
        try
        {
            await stream.WriteAsync(GreetingBytes, session).ConfigureAwait(false);
            while (true)
            {
                // A line heard meanwhile only moves the due time later, so waking at the old one and
                // checking again never sends a ping early.
                var now = Stopwatch.GetTimestamp();
                var due = deadMan.NextPingDue;
                if (now < due)
                {
                    // Rounded up to whole ms: Task.Delay truncates, and a 0 ms delay would spin.
                    var wait = Math.Ceiling(Stopwatch.GetElapsedTime(now, due).TotalMilliseconds);
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), session).ConfigureAwait(false);
                    continue;
                }

                deadMan.PingSent(now);
                await stream.WriteAsync(PingBytes, session).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            return "stopped";
        }
        catch (Exception e)
        {
            return $"send failed: {e.Message}";
        }
    }

    // The dead-man timer: kept apart from the pings, so a stalled send cannot delay it. Never throws.
    private async Task<string> WatchAsync(PanelDeadMan deadMan, CancellationToken session)
    {
        try
        {
            var interval = deadMan.CheckInterval;
            var intervalTicks = StopwatchTicks.From(interval);
            while (true)
            {
                var scheduledAt = Stopwatch.GetTimestamp() + intervalTicks;
                await Task.Delay(interval, session).ConfigureAwait(false);
                var verdict = deadMan.Check(Stopwatch.GetTimestamp(), scheduledAt);
                if (verdict.Stall is { } stall)
                    Trace.TraceWarning($"SKAARHOJ panel: the app stalled, not the panel at {Endpoint}: a dead-man check ran {stall.TotalMilliseconds:0} ms late");
                if (verdict.Silence is { } silence)
                    return verdict.NothingHeard
                        ? $"the panel was silent for {silence.TotalMilliseconds:0} ms since connecting (timeout {_silenceTimeout.TotalMilliseconds:0} ms after the {_connectGrace.TotalMilliseconds:0} ms connect grace)"
                        : $"the panel was silent for {silence.TotalMilliseconds:0} ms (timeout {_silenceTimeout.TotalMilliseconds:0} ms)";
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            return "stopped";
        }
        catch (Exception e)
        {
            return $"dead-man timer failed: {e.Message}";
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args, string name)
    {
        if (handler == null)
            return;
        t_raisingEvent = true;
        try
        {
            handler(this, args);
        }
        catch (Exception e)
        {
            Trace.TraceError($"SKAARHOJ panel: a {name} subscriber failed: {e}");
        }
        finally
        {
            t_raisingEvent = false;
        }
    }

    private void Raise(EventHandler? handler, EventArgs args, string name)
    {
        if (handler == null)
            return;
        t_raisingEvent = true;
        try
        {
            handler(this, args);
        }
        catch (Exception e)
        {
            Trace.TraceError($"SKAARHOJ panel: a {name} subscriber failed: {e}");
        }
        finally
        {
            t_raisingEvent = false;
        }
    }

    private void CloseQuietly(TcpClient? client)
    {
        try
        {
            client?.Dispose();
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"SKAARHOJ panel: failed to close the connection to {Endpoint}: {e.Message}");
        }
    }
}
