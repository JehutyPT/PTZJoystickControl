using System.Collections.Concurrent;
using System.Diagnostics;
using PtzJoystickControl.SkaarhojPanel;
using Xunit;

namespace PtzJoystickControl.Tests.Skaarhoj;

// Timings are scaled down from the defaults (500 ms ping, 1 s timeout, 1 s grace, 1–30 s backoff)
// so each case runs in about a second or less. Lower bounds are asserted tightly; upper bounds leave
// room for slow CI runners. Exact dead-man timing (pings, drops, app freezes) is tested with explicit
// timestamps in PanelDeadManTests; these cases check the link wires it to the socket.
[Collection(TimingBoundCollection.Name)]
public sealed class SkaarhojPanelLinkTests : IDisposable
{
    private static readonly TimeSpan Ping = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(250);
    // Long enough that no reconnect happens inside a test unless the test asks for one.
    private static readonly TimeSpan NoReconnect = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(250);

    private readonly FakeRawPanel _panel = new();
    private readonly TraceRecorder _trace = new();
    private readonly List<SkaarhojPanelLink> _links = new();
    private readonly ConcurrentQueue<(string Kind, long At, string Detail)> _events = new();
    private long _startedAt;
    // Each message the link raised, and when it reached subscribers: just after the link counted its line as heard.
    private readonly ConcurrentQueue<(long At, RawPanelMessage Message)> _heard = new();

    public void Dispose()
    {
        foreach (var link in _links)
            link.Dispose();
        _panel.Dispose();
        _trace.Dispose();
    }

    private SkaarhojPanelLink Start(
        int? port = null,
        TimeSpan? grace = null,
        TimeSpan? reconnectInitial = null,
        TimeSpan? reconnectMax = null,
        TimeSpan? ping = null,
        TimeSpan? timeout = null)
    {
        var link = new SkaarhojPanelLink(
            "127.0.0.1",
            port ?? _panel.Port,
            pingInterval: ping ?? Ping,
            silenceTimeout: timeout ?? Timeout,
            connectGrace: grace ?? Grace,
            reconnectInitialDelay: reconnectInitial ?? NoReconnect,
            reconnectMaxDelay: reconnectMax ?? NoReconnect,
            connectTimeout: TimeSpan.FromSeconds(1));
        link.LinkUp += (_, _) => _events.Enqueue(("up", Stopwatch.GetTimestamp(), ""));
        link.LinkDown += (_, reason) => _events.Enqueue(("down", Stopwatch.GetTimestamp(), reason));
        link.MessageReceived += (_, message) => _heard.Enqueue((Stopwatch.GetTimestamp(), message));
        _links.Add(link);
        if (_startedAt == 0)
            _startedAt = Stopwatch.GetTimestamp();
        link.Start();
        return link;
    }

    private int Count(string kind) => _events.Count(e => e.Kind == kind);

    private long FirstAt(string kind) => _events.First(e => e.Kind == kind).At;

    // Each event with its ms since the first link started, and the reason for a down.
    private string DescribeEvents()
    {
        var events = _events.ToArray();
        var start = _startedAt;
        return "Events: " + string.Join(" | ", events.Select(e =>
            $"{e.Kind} at {Stopwatch.GetElapsedTime(start, e.At).TotalMilliseconds:F0} ms{(e.Detail.Length > 0 ? ": " + e.Detail : "")}"));
    }

    private RawPanelMessage[] Messages => _heard.Select(h => h.Message).ToArray();

    private string DownReason => _events.First(e => e.Kind == "down").Detail;

    private int PingsReceived => _panel.Received.Count(l => l == "ping");

    // When the link last heard the panel before `at`, as seen by a MessageReceived subscriber.
    private long LastHeardBefore(long at) => _heard.Where(h => h.At <= at).Select(h => h.At).DefaultIfEmpty(0).Max();

    // A forgiven app freeze restarts the silence count, so a drop may come up to how late the check ran
    // plus one more timeout later. Zero when this test's link logged no freeze.
    private TimeSpan ForgivenFreezeAllowance(TimeSpan timeout)
    {
        var late = _trace.Messages
            .Where(m => m.Contains($"the app stalled, not the panel at 127.0.0.1:{_panel.Port}:"))
            .Select(m => System.Text.RegularExpressions.Regex.Match(m, @"ran (\d+) ms late"))
            .Where(m => m.Success)
            .Sum(m => double.Parse(m.Groups[1].Value));
        return late == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(late) + timeout;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan within)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < within)
        {
            if (condition())
                return true;
            await Task.Delay(5);
        }
        return condition();
    }

    [Fact]
    public async Task ReportsUpOnConnect_WithoutRdyOrAnyAck_AndAcceptsMapLinesAtAnyTime()
    {
        _panel.Greeting = new[] { "map=1:1", "map=2:2" };
        _panel.AnswerPings = false;
        var link = Start(grace: TimeSpan.FromSeconds(10));

        Assert.True(await WaitUntil(() => Count("up") == 1, TimeSpan.FromSeconds(2)));
        Assert.True(link.IsUp);

        _panel.Send("HWC#60=Speed:-500", "map=3:3", "garbage line", "HWC#1.2=Down");
        Assert.True(await WaitUntil(() => Messages.OfType<RawPanelMessage.Down>().Any(), TimeSpan.FromSeconds(2)));

        Assert.Equal(new RawPanelMessage[]
        {
            new RawPanelMessage.Map(1, 1),
            new RawPanelMessage.Map(2, 2),
            new RawPanelMessage.Speed(60, -500),
            new RawPanelMessage.Map(3, 3),
            new RawPanelMessage.Unknown("garbage line"),
            new RawPanelMessage.Down(1, 2),
        }, Messages);
        Assert.DoesNotContain(Messages, m => m is RawPanelMessage.Ready);
        Assert.Equal(0, Count("down"));
        Assert.True(link.IsUp);
        Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains("garbage line"));
    }

    [Fact]
    public async Task AfterConnect_SendsListAndConnectionsQuery_ThenAPingAtOnce()
    {
        // A ping interval far longer than the wait: only the first ping, sent with the queries, can arrive.
        Start(ping: TimeSpan.FromSeconds(5), timeout: TimeSpan.FromSeconds(10));

        Assert.True(await WaitUntil(() => PingsReceived == 1, TimeSpan.FromSeconds(2)));

        Assert.Equal(new[] { "list", "Connections?", "ping" }, _panel.Received.Take(3));
    }

    [Fact]
    public async Task StaysUp_WhileThePanelAnswersPings()
    {
        Start();

        await Task.Delay(Grace + Timeout * 4);

        Assert.Equal(1, Count("up"));
        Assert.Equal(0, Count("down"));
        Assert.Equal(1, _panel.Accepted);
    }

    [Fact]
    public async Task ReportsDown_WhenThePanelFallsSilent_WithinTimeoutPlusOnePingInterval_AndClosesTheSocket()
    {
        // A longer timeout than the class default, so a read-loop stall in a busy suite cannot drop the link early.
        var ping = TimeSpan.FromMilliseconds(50);
        var timeout = TimeSpan.FromMilliseconds(300);
        var link = Start(ping: ping, timeout: timeout);
        await Task.Delay(Grace + timeout);
        Assert.Equal(0, Count("down"));

        _panel.AnswerPings = false;

        Assert.True(await WaitUntil(() => Count("down") == 1, TimeSpan.FromSeconds(3)));
        // Counted from the last line the link heard, which a stalled read loop can hear well after it was sent.
        var downAt = FirstAt("down");
        var sinceLastHeard = Stopwatch.GetElapsedTime(LastHeardBefore(downAt), downAt);
        Assert.InRange(sinceLastHeard, timeout - TimeSpan.FromMilliseconds(5), timeout + ping + Slack + ForgivenFreezeAllowance(timeout));
        Assert.False(link.IsUp);
        Assert.StartsWith("the panel was silent for", DownReason);
        Assert.EndsWith(" ms (timeout 300 ms)", DownReason);
        Assert.True(await WaitUntil(() => _panel.ClosedByLink == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, _panel.OpenClients);
        Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains("link down"));
    }

    [Fact]
    public async Task InputLines_KeepTheLinkUp_AndNoPingIsSentWhileTheyArrive()
    {
        // The panel never answers a ping: only its input lines can keep the link up.
        _panel.AnswerPings = false;
        var ping = TimeSpan.FromMilliseconds(300);
        var timeout = TimeSpan.FromMilliseconds(600);
        var grace = TimeSpan.FromMilliseconds(50);
        var link = Start(grace: grace, ping: ping, timeout: timeout);
        Assert.True(await WaitUntil(() => Count("up") == 1 && _panel.OpenClients == 1, TimeSpan.FromSeconds(2)));

        // A moving stick: a line every 20 ms, from its own thread so a busy thread pool cannot open a gap.
        using var stop = new CancellationTokenSource();
        var stick = Task.Factory.StartNew(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                _panel.Send($"HWC#60=Speed:{i % 500}");
                Thread.Sleep(20);
            }
        }, TaskCreationOptions.LongRunning);
        Assert.True(await WaitUntil(() => Messages.OfType<RawPanelMessage.Speed>().Any(), TimeSpan.FromSeconds(2)));
        await Task.Delay(50);
        var pingsBefore = PingsReceived;

        await Task.Delay(grace + timeout * 2);
        var pingsAfter = PingsReceived;
        stop.Cancel();
        await stick;

        Assert.Equal(pingsBefore, pingsAfter);
        Assert.Equal(0, Count("down"));
        Assert.True(link.IsUp);
    }

    [Fact]
    public async Task OverLongLinesTheLinkDiscards_StillCountAsHearingFromThePanel()
    {
        // The panel never answers a ping: only the over-long lines can keep the link up.
        _panel.AnswerPings = false;
        _panel.Greeting = Array.Empty<string>();
        var timeout = TimeSpan.FromMilliseconds(600);
        var grace = TimeSpan.FromMilliseconds(50);
        var link = Start(grace: grace, ping: TimeSpan.FromMilliseconds(300), timeout: timeout);
        Assert.True(await WaitUntil(() => Count("up") == 1 && _panel.OpenClients == 1, TimeSpan.FromSeconds(2)));

        var tooLong = new string('x', LineFramer.DefaultMaxLineLength + 16);
        using var stop = new CancellationTokenSource();
        var sender = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _panel.Send(tooLong);
                Thread.Sleep(150);
            }
        }, TaskCreationOptions.LongRunning);

        await Task.Delay(grace + timeout * 2);
        stop.Cancel();
        await sender;

        Assert.Equal(0, Count("down"));
        Assert.True(link.IsUp);
        Assert.Contains(_trace.Messages, m => m.Contains($":{_panel.Port}") && m.Contains("dropped an over-long line"));
    }

    [Fact]
    public async Task AQuietPanel_GetsAPingEveryPingInterval_CountedFromTheLastLineHeard()
    {
        // The panel answers each ping with an ack and sends nothing else.
        var ping = TimeSpan.FromMilliseconds(100);
        Start(grace: TimeSpan.FromMilliseconds(50), ping: ping, timeout: TimeSpan.FromSeconds(2));

        Assert.True(await WaitUntil(() => PingsReceived >= 5, TimeSpan.FromSeconds(3)));
        var pings = _panel.TimesReceived("ping");
        for (var i = 1; i < pings.Length; i++)
            Assert.InRange(Stopwatch.GetElapsedTime(pings[i - 1], pings[i]), ping, ping + Slack);

        // A line heard partway through the interval puts the next ping a whole interval after it.
        var seen = PingsReceived;
        Assert.True(await WaitUntil(() => PingsReceived > seen, TimeSpan.FromSeconds(2)));
        await Task.Delay(40);
        var lineAt = Stopwatch.GetTimestamp();
        _panel.Send("HWC#61=Speed:10");
        Assert.True(await WaitUntil(() => _heard.Any(h => h.Message is RawPanelMessage.Speed), TimeSpan.FromSeconds(2)));
        var heardAt = _heard.First(h => h.Message is RawPanelMessage.Speed).At;
        // A ping already on its way before the link heard the line is not the next one; the next comes a
        // whole interval after the line was heard.
        var after = heardAt + StopwatchTicks.From(ping / 2);
        Assert.True(await WaitUntil(() => _panel.TimesReceived("ping").Any(t => t > after), TimeSpan.FromSeconds(2)));

        var next = _panel.TimesReceived("ping").First(t => t > after);
        Assert.True(Stopwatch.GetElapsedTime(lineAt, next) >= ping);
        Assert.True(Stopwatch.GetElapsedTime(heardAt, next) <= ping + Slack);
    }

    [Fact]
    public async Task Silence_DropsTheLink_AboutOneTimeoutAfterTheLastLine_OfAnyKind()
    {
        var ping = TimeSpan.FromMilliseconds(100);
        var timeout = TimeSpan.FromMilliseconds(500);
        var grace = TimeSpan.FromMilliseconds(50);
        Start(grace: grace, ping: ping, timeout: timeout);
        await Task.Delay(grace + timeout);
        Assert.Equal(0, Count("down"));

        // The acks stop; a moment later one input line arrives, then nothing.
        _panel.AnswerPings = false;
        await Task.Delay(ping);
        var lineAt = Stopwatch.GetTimestamp();
        _panel.Send("HWC#60=Speed:0");

        Assert.True(await WaitUntil(() => Count("down") == 1, TimeSpan.FromSeconds(3)));
        // Counted from the input line, not from the last ack before it.
        var downAt = FirstAt("down");
        Assert.True(Stopwatch.GetElapsedTime(lineAt, downAt) >= timeout);
        // The upper bound runs from when the link heard the line, which a stalled read loop can delay.
        var heardAt = _heard.Last(h => h.Message is RawPanelMessage.Speed).At;
        Assert.True(Stopwatch.GetElapsedTime(heardAt, downAt) <= timeout + ping + Slack + ForgivenFreezeAllowance(timeout));
        Assert.StartsWith("the panel was silent for", DownReason);
    }

    [Fact]
    public async Task StaysUp_ThroughAPostConnectStallShorterThanTheGrace_ThenTimesOutAfterIt()
    {
        // The panel answers the first ping at once, then stalls longer than the timeout; neither may trip it.
        _panel.SilentAfterAccept = TimeSpan.FromMilliseconds(180);
        _panel.AnswerFirstPingAtOnce = true;
        Start();

        await Task.Delay(Grace + Timeout * 2);
        Assert.True(Count("up") == 1 && Count("down") == 0, DescribeEvents());

        _panel.AnswerPings = false;

        Assert.True(await WaitUntil(() => Count("down") == 1, TimeSpan.FromSeconds(2)));
        Assert.InRange(Stopwatch.GetElapsedTime(_panel.LastAckSentAt, FirstAt("down")), Timeout, Timeout + Ping + Slack);
    }

    [Fact]
    public async Task StaysUp_ThroughAWakeStallShorterThanTheGrace_ThenTimesOutAfterIt()
    {
        Start();
        await Task.Delay(Grace + Timeout);
        Assert.Equal(0, Count("down"));

        // The panel wakes and stalls longer than the timeout, but within the grace re-armed by the wake.
        _panel.SilentUntil = Stopwatch.GetTimestamp() + StopwatchTicks.From(TimeSpan.FromMilliseconds(180));
        _panel.Send("_isSleeping=0");

        await Task.Delay(Grace + Timeout * 2);
        Assert.Equal(1, Count("up"));
        Assert.Equal(0, Count("down"));

        _panel.AnswerPings = false;

        Assert.True(await WaitUntil(() => Count("down") == 1, TimeSpan.FromSeconds(2)));
        Assert.InRange(Stopwatch.GetElapsedTime(_panel.LastAckSentAt, FirstAt("down")), Timeout, Timeout + Ping + Slack);
    }

    [Fact]
    public async Task AfterWake_ASilenceLongerThanTheGrace_StillReportsDown_OnlyAfterGracePlusTimeout()
    {
        Start();
        await Task.Delay(Grace + Timeout);
        Assert.Equal(0, Count("down"));

        _panel.AnswerPings = false;
        _panel.Send("_isSleeping=0");
        Assert.True(await WaitUntil(() => Messages.OfType<RawPanelMessage.Reply>().Any(), TimeSpan.FromSeconds(2)));
        var wokeAt = Stopwatch.GetTimestamp();

        Assert.True(await WaitUntil(() => Count("down") == 1, TimeSpan.FromSeconds(2)));

        // Seen by the test just after the link re-armed its grace, so allow a few ms below.
        Assert.InRange(Stopwatch.GetElapsedTime(wokeAt, FirstAt("down")), Grace + Timeout - TimeSpan.FromMilliseconds(10), Grace + Timeout + Ping + Slack);
    }

    [Fact]
    public async Task SleepReport_DoesNotRearmTheGrace()
    {
        Start();
        await Task.Delay(Grace + Timeout);

        _panel.AnswerPings = false;
        // The sleep report is itself a line heard: the count runs from it, with no new grace.
        var sleptAt = Stopwatch.GetTimestamp();
        _panel.Send("_isSleeping=1");

        Assert.True(await WaitUntil(() => Count("down") == 1, TimeSpan.FromSeconds(2)));
        Assert.InRange(Stopwatch.GetElapsedTime(sleptAt, FirstAt("down")), Timeout, Timeout + Ping + Slack);
    }

    [Fact]
    public async Task WhenNothingIsHeardAfterConnect_ReportsDownOnlyAfterGracePlusTimeout_SilentSinceConnecting()
    {
        _panel.Greeting = Array.Empty<string>();
        _panel.AnswerPings = false;
        Start();
        Assert.True(await WaitUntil(() => Count("up") == 1, TimeSpan.FromSeconds(2)));
        var upAt = FirstAt("up");

        Assert.True(await WaitUntil(() => Count("down") == 1, TimeSpan.FromSeconds(2)));

        // The link takes its connect time just before raising LinkUp, so allow a few ms below.
        Assert.InRange(Stopwatch.GetElapsedTime(upAt, FirstAt("down")), Grace + Timeout - TimeSpan.FromMilliseconds(10), Grace + Timeout + Ping + Slack);
        Assert.EndsWith(" ms since connecting (timeout 100 ms after the 250 ms connect grace)", DownReason);
    }

    [Fact]
    public async Task ReportsDownAtOnce_WhenThePanelClosesTheConnection()
    {
        Start();
        Assert.True(await WaitUntil(() => Count("up") == 1, TimeSpan.FromSeconds(2)));

        _panel.Dispose();

        // Well before the dead-man could fire (grace + timeout after connect).
        Assert.True(await WaitUntil(() => Count("down") == 1, Grace));
        Assert.DoesNotContain("silent", DownReason);
    }

    [Fact]
    public async Task Reconnects_WithCappedExponentialDelays_AndNeverHoldsTwoConnections()
    {
        var initial = TimeSpan.FromMilliseconds(30);
        var max = TimeSpan.FromMilliseconds(120);
        _panel.CloseOnAccept = true;
        var link = Start(reconnectInitial: initial, reconnectMax: max);

        // Each accepted-then-closed connection never got an ack, so the delay keeps growing.
        Assert.True(await WaitUntil(() => _panel.Accepted >= 5, TimeSpan.FromSeconds(3)));
        _panel.CloseOnAccept = false;

        var accepted = _panel.AcceptedAt;
        var gaps = Enumerable.Range(1, 4).Select(i => Stopwatch.GetElapsedTime(accepted[i - 1], accepted[i])).ToArray();
        var expected = new[] { 30, 60, 120, 120 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray();
        for (var i = 0; i < expected.Length; i++)
            Assert.InRange(gaps[i], expected[i] - TimeSpan.FromMilliseconds(5), expected[i] + Slack);

        // The panel comes back: the link reconnects on its own and stays up.
        Assert.True(await WaitUntil(() => link.IsUp && _panel.OpenClients == 1, TimeSpan.FromSeconds(2)));
        var acceptedWhenBack = _panel.Accepted;
        await Task.Delay(Grace + Timeout);
        Assert.True(link.IsUp);
        Assert.Equal(acceptedWhenBack, _panel.Accepted);
        Assert.Equal(1, _panel.MaxConcurrentClients);
    }

    [Fact]
    public async Task AConnectionThatHearsLinesButNoAck_StillGrowsTheReconnectDelay()
    {
        // The panel greets every connection with map= lines but never answers a ping: only an ack
        // shows a healthy connection, so the backoff is not started over.
        _panel.AnswerPings = false;
        Start(grace: TimeSpan.FromMilliseconds(20), timeout: TimeSpan.FromMilliseconds(40),
            reconnectInitial: TimeSpan.FromMilliseconds(30), reconnectMax: TimeSpan.FromMilliseconds(120));

        Assert.True(await WaitUntil(() => _panel.Accepted >= 4 && Count("down") >= 3, TimeSpan.FromSeconds(3)));
        Assert.Contains(Messages, m => m is RawPanelMessage.Map);

        // Each wait runs from a link down to the next connect.
        var downs = _events.Where(e => e.Kind == "down").Select(e => e.At).ToArray();
        var accepted = _panel.AcceptedAt;
        var expected = new[] { 30, 60, 120 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray();
        for (var i = 0; i < expected.Length; i++)
            Assert.InRange(Stopwatch.GetElapsedTime(downs[i], accepted[i + 1]), expected[i] - TimeSpan.FromMilliseconds(5), expected[i] + Slack);
    }

    [Fact]
    public async Task AfterASilenceTimeout_ReconnectsToTheSamePanel_AndNeverHoldsTwoConnections()
    {
        var link = Start(reconnectInitial: TimeSpan.FromMilliseconds(50), reconnectMax: TimeSpan.FromMilliseconds(200));
        await Task.Delay(Grace + Timeout);

        // The link drops a silent connection the panel still holds open, then reconnects.
        _panel.AnswerPings = false;
        Assert.True(await WaitUntil(() => Count("down") >= 1, TimeSpan.FromSeconds(2)));
        _panel.AnswerPings = true;

        Assert.True(await WaitUntil(() => Count("up") >= 2 && link.IsUp, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, _panel.MaxConcurrentClients);
        Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Information && e.Message.Contains("link up"));
    }

    [Fact]
    public async Task ConnectFailures_AreRetried_UntilThePanelAppears()
    {
        var port = FakeRawPanel.GetFreePort();
        var link = Start(port: port, reconnectInitial: TimeSpan.FromMilliseconds(20), reconnectMax: TimeSpan.FromMilliseconds(80));

        await Task.Delay(300);
        Assert.False(link.IsUp);
        Assert.Equal(0, Count("up"));
        Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains($":{port}"));
        // Failures are logged at attempts 1, 2, 4, 8, ...: not one line per attempt.
        Assert.InRange(_trace.Messages.Count(m => m.Contains($":{port}") && m.Contains("cannot connect")), 1, 4);

        using var panel = new FakeRawPanel(port);

        Assert.True(await WaitUntil(() => link.IsUp, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, panel.MaxConcurrentClients);
        link.Dispose();
    }

    [Fact]
    public async Task Dispose_WhileUp_ClosesTheSocket_ReportsDown_StopsPinging_AndNeverThrows()
    {
        var link = Start(reconnectInitial: TimeSpan.FromMilliseconds(20), reconnectMax: TimeSpan.FromMilliseconds(20));
        Assert.True(await WaitUntil(() => _panel.Received.Contains("ping"), TimeSpan.FromSeconds(2)));

        Assert.Null(Record.Exception(link.Dispose));

        Assert.False(link.IsUp);
        Assert.Equal(1, Count("down"));
        Assert.True(await WaitUntil(() => _panel.ClosedByLink == 1, TimeSpan.FromSeconds(1)));
        var pings = _panel.Received.Count(l => l == "ping");
        await Task.Delay(200);
        Assert.Equal(pings, _panel.Received.Count(l => l == "ping"));
        Assert.Equal(1, _panel.Accepted);
        Assert.Null(Record.Exception(link.Dispose));
    }

    [Fact]
    public async Task Dispose_WhileWaitingToReconnect_CancelsTheReconnect()
    {
        _panel.CloseOnAccept = true;
        var link = Start(reconnectInitial: TimeSpan.FromMilliseconds(150), reconnectMax: TimeSpan.FromMilliseconds(150));
        Assert.True(await WaitUntil(() => _panel.Accepted == 1 && Count("down") == 1, TimeSpan.FromSeconds(2)));

        Assert.Null(Record.Exception(link.Dispose));

        await Task.Delay(400);
        Assert.Equal(1, _panel.Accepted);
    }

    [Fact]
    public void Dispose_BeforeStart_NeverThrows_AndStartAfterDisposeDoesNothing()
    {
        var link = new SkaarhojPanelLink("127.0.0.1", _panel.Port);

        Assert.Null(Record.Exception(link.Dispose));
        Assert.Null(Record.Exception(link.Start));
        Assert.Equal(0, _panel.Accepted);
    }

    [Fact]
    public async Task ThrowingSubscribers_AreLogged_AndTheLinkKeepsRunning()
    {
        var link = new SkaarhojPanelLink("127.0.0.1", _panel.Port, Ping, Timeout, Grace, NoReconnect, NoReconnect, TimeSpan.FromSeconds(1));
        _links.Add(link);
        link.LinkUp += (_, _) => throw new InvalidOperationException("up subscriber failed");
        link.MessageReceived += (_, _) => throw new InvalidOperationException("message subscriber failed");
        link.Start();

        Assert.True(await WaitUntil(() => _trace.Messages.Any(m => m.Contains("message subscriber failed")), TimeSpan.FromSeconds(2)));
        await Task.Delay(Grace + Timeout * 2);

        Assert.True(link.IsUp);
        Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Error && e.Message.Contains("up subscriber failed"));
        Assert.Equal(1, _panel.Accepted);
    }

    [Fact]
    public async Task ModelVersionAndConnectionsReplies_AreLogged()
    {
        // The dead-man stays unarmed: a busy test run can starve the pings past the short timeout,
        // and with no reconnect the replies would then go nowhere.
        Start(grace: NoReconnect);
        Assert.True(await WaitUntil(() => _panel.Received.Contains("Connections?"), TimeSpan.FromSeconds(2)));

        _panel.Send("_model=SK_PTZEXTREMEV2", "_serial=123", "_version=v2.5.27", $"_connections=127.0.0.1;");

        Assert.True(await WaitUntil(() => _trace.Messages.Any(m => m.Contains("_connections=127.0.0.1;")), TimeSpan.FromSeconds(2)));
        Assert.Contains(_trace.Messages, m => m.Contains("_model=SK_PTZEXTREMEV2"));
        Assert.Contains(_trace.Messages, m => m.Contains("_version=v2.5.27"));
    }

    [Fact]
    public void CreateIfConfigured_WithNoAddress_ReturnsNull_AndLogsNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PtzSkaarhojLinkTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, SkaarhojPanelSettingsStore.FileName);

            Assert.Null(SkaarhojPanelLink.CreateIfConfigured(path));
            File.WriteAllText(path, "{ \"Address\": \"\", \"Port\": 9923 }");
            Assert.Null(SkaarhojPanelLink.CreateIfConfigured(path));

            // Other test classes log SKAARHOJ lines in parallel: look only for this file and the connect line.
            Assert.DoesNotContain(_trace.Messages, m => m.Contains(dir) || m.Contains("SKAARHOJ panel: connecting to"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void CreateIfConfigured_WithAnInvalidPort_ReturnsNull_AndLogsAnError(int port)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PtzSkaarhojLinkTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, SkaarhojPanelSettingsStore.FileName);
            File.WriteAllText(path, $"{{ \"Address\": \"127.0.0.1\", \"Port\": {port} }}");

            Assert.Null(SkaarhojPanelLink.CreateIfConfigured(path));

            Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Error && e.Message.Contains(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateIfConfigured_WithAnAddress_ReturnsALinkThatConnectsOnStart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PtzSkaarhojLinkTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, SkaarhojPanelSettingsStore.FileName);
            File.WriteAllText(path, $"{{ \"Address\": \" 127.0.0.1 \", \"Port\": {_panel.Port} }}");
            var link = SkaarhojPanelLink.CreateIfConfigured(path);

            Assert.NotNull(link);
            _links.Add(link!);
            Assert.Equal("127.0.0.1", link!.Host);
            Assert.Equal(_panel.Port, link.Port);
            // Not started: a subscriber attached now cannot miss the first LinkUp.
            await Task.Delay(100);
            Assert.Equal(0, _panel.Accepted);

            link.Start();
            Assert.True(await WaitUntil(() => link.IsUp, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
