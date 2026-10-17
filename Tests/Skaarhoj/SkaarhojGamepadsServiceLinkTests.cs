using System.Collections.Concurrent;
using System.Diagnostics;
using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.SkaarhojPanel;
using PtzJoystickControl.Tests.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Skaarhoj;

// The panel controller over a real SkaarhojPanelLink and FakeRawPanel. The test plays the input
// loop: nothing is applied until it calls RunPending.
// The link goes down only when the test drops the connection: the ping timeout is far longer than
// any test, because a busy parallel test run can starve the link's own pings past a short one and
// drop a healthy link mid-test. The ping timeout itself is tested in SkaarhojPanelLinkTests.
public sealed class SkaarhojGamepadsServiceLinkTests : IDisposable
{
    private static readonly TimeSpan Ping = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(3);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptz-skaarhoj-link-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRawPanel _panel = new();
    private readonly MotionRecordingCamera _camA = new("A");
    private readonly MotionRecordingCamera _camB = new("B");
    private readonly InputQueue _queue = new();
    private readonly RaisingCompanionService _companion = new();
    private readonly ConcurrentQueue<string> _linkEvents = new();
    private readonly SkaarhojPanelLink _link;
    private readonly SkaarhojGamepadsService _service;

    public SkaarhojGamepadsServiceLinkTests()
    {
        Directory.CreateDirectory(_dir);
        _link = new SkaarhojPanelLink("127.0.0.1", _panel.Port, Ping, Timeout, Grace,
            reconnectInitialDelay: TimeSpan.FromMilliseconds(50),
            reconnectMaxDelay: TimeSpan.FromMilliseconds(50),
            connectTimeout: TimeSpan.FromSeconds(1));
        _service = new SkaarhojGamepadsService(_link,
            new GamepadSettingsStore(Path.Combine(_dir, SkaarhojGamepadsService.SettingsFileName)),
            new FakeCamerasService(_camA, _camB), new CommandsService(_companion, TestSpeedProfiles.Full()), _companion, _queue);
        // Subscribed after the service, so a recorded event has already been enqueued by it.
        _link.LinkUp += (_, _) => _linkEvents.Enqueue("up");
        _link.LinkDown += (_, _) => _linkEvents.Enqueue("down");
        _link.MessageReceived += (_, m) => _linkEvents.Enqueue(m.ToString());
    }

    public void Dispose()
    {
        _link.Dispose();
        _service.Dispose();
        _panel.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
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

    private int Count(string kind) => _linkEvents.Count(e => e == kind);

    private bool Received(string messageText) => _linkEvents.Any(e => e.Contains(messageText));

    // Up, and the fake panel holds exactly the connection numbered `connection`, so Send reaches it.
    private async Task WaitForConnection(int connection)
    {
        Assert.True(await WaitUntil(() => Count("up") == connection && _panel.Accepted == connection && _panel.OpenClients == 1, Within));
    }

    private SkaarhojGamepad ActiveGamepad() => Assert.IsType<SkaarhojGamepad>(Assert.Single(_service.ActiveGamepads));

    // Activated, link up, LR → pan and Rotate → zoom on camera A.
    private async Task<SkaarhojGamepad> ConnectAndMap()
    {
        _service.ActivateGamepad(_service.Gamepads.Single());
        _link.Start();
        await WaitForConnection(1);
        _queue.RunPending();
        var gamepad = ActiveGamepad();
        gamepad.Inputs.Single(i => i.Name == "LR").SelectedCommand = gamepad.Commands.OfType<PanCommand>().Single();
        gamepad.Inputs.Single(i => i.Name == "Rotate").SelectedCommand = gamepad.Commands.OfType<ZoomCommand>().Single();
        gamepad.SelectedCamera = _camA;
        return gamepad;
    }

    [Fact]
    public async Task InputFromTheLink_IsNotAppliedUntilTheInputLoopRuns()
    {
        await ConnectAndMap();

        _panel.Send("HWC#60=Speed:500");
        Assert.True(await WaitUntil(() => Received("Speed { Hwc = 60, Value = 500 }"), Within));
        await Task.Delay(50);
        Assert.Empty(_camA.Calls);

        _queue.RunPending();
        Assert.Equal(new[] { "Pan 1 Right" }, _camA.Calls);
    }

    [Fact]
    public async Task FocusKnobTurnFromTheLink_StepsOncePerStep_WhenTheInputLoopRuns()
    {
        FocusKnob.MapToFocusStep(await ConnectAndMap());

        _panel.Send("HWC#59=Enc:2");
        // The service enqueues before the test's own handler records the line.
        Assert.True(await WaitUntil(() => Received("Encoder { Hwc = 59, Steps = 2 }"), Within));
        Assert.Empty(_camA.Calls);

        _queue.RunPending();
        Assert.Equal(new[] { FocusKnob.Far, FocusKnob.Far }, _camA.Calls);
    }

    [Fact]
    public async Task LinkDown_StopsEveryDrivenCamera_AndShowsDisconnected()
    {
        var gamepad = await ConnectAndMap();
        _panel.Send("HWC#60=Speed:500");
        Assert.True(await WaitUntil(() => Received("Value = 500"), Within));
        _queue.RunPending();
        gamepad.SelectedCamera = _camB;
        _panel.Send("HWC#62=Speed:-500");
        Assert.True(await WaitUntil(() => Received("Value = -500"), Within));
        _queue.RunPending();
        Assert.True(_camB.IsMoving);
        var stopsA = _camA.StopCalls;

        // Off rather than dropped: a reconnect 50 ms later would race RunPending and bring a fresh
        // gamepad back up before the asserts.
        _panel.TurnOff();
        Assert.True(await WaitUntil(() => Count("down") == 1, Within));
        _queue.RunPending();

        Assert.Equal(1, _camA.StopCalls - stopsA);
        Assert.Equal(1, _camB.StopCalls);
        Assert.Empty(_service.ActiveGamepads);
        Assert.False(gamepad.IsConnected);
        Assert.False(_service.Gamepads.Single().IsConnected);
        Assert.Equal(1, Count("up"));
    }

    [Fact]
    public async Task AfterReconnect_AFreshGamepadHasEveryAxisAtZero_AndSendsNoMotionUntilSpeedArrives()
    {
        var before = await ConnectAndMap();
        _panel.Send("HWC#60=Speed:500");
        Assert.True(await WaitUntil(() => Received("Value = 500"), Within));
        _queue.RunPending();

        _panel.DropConnections();
        await WaitForConnection(2);
        _queue.RunPending();
        var calls = _camA.Calls.Count;

        var after = ActiveGamepad();
        Assert.NotSame(before, after);
        Assert.True(after.IsConnected);
        Assert.All(after.Inputs, i => Assert.Equal(0f, i.InputValue));
        after.SelectedCamera = _camA;
        await Task.Delay(Ping * 3);
        _queue.RunPending();
        Assert.Equal(calls, _camA.Calls.Count);

        _panel.Send("HWC#60=Speed:-250");
        Assert.True(await WaitUntil(() => Received("Value = -250"), Within));
        _queue.RunPending();
        Assert.Equal(calls + 1, _camA.Calls.Count);
    }

    [Fact]
    public async Task SleepAndWake_FromThePanel_StopThenRestoreWithAxesAtZero()
    {
        var before = await ConnectAndMap();
        _panel.Send("HWC#60=Speed:500");
        Assert.True(await WaitUntil(() => Received("Value = 500"), Within));
        _queue.RunPending();
        var stops = _camA.StopCalls;

        _panel.Send("_isSleeping=1");
        Assert.True(await WaitUntil(() => Received("Value = 1"), Within));
        _queue.RunPending();
        Assert.Equal(1, _camA.StopCalls - stops);
        Assert.Empty(_service.ActiveGamepads);
        Assert.True(_service.Gamepads.Single().IsAsleep);

        _panel.Send("_isSleeping=0");
        Assert.True(await WaitUntil(() => Received("Value = 0"), Within));
        _queue.RunPending();
        var after = ActiveGamepad();
        Assert.NotSame(before, after);
        Assert.True(_service.Gamepads.Single().IsConnected);
        Assert.False(_service.Gamepads.Single().IsAsleep);
        Assert.All(after.Inputs, i => Assert.Equal(0f, i.InputValue));
    }

    [Fact]
    public async Task TheAppSendsThePanelNothingBeyondTheLinksOwnLines()
    {
        // Every path this issue adds: input, Companion selection, sleep, wake, deactivate,
        // reactivate, link loss and reconnect. Then the Focus knob: its turn and its press.
        FocusKnob.MapToFocusStep(await ConnectAndMap());
        _panel.Send("HWC#60=Speed:500", "HWC#58=Speed:-500", "HWC#63=Down", "HWC#63=Up", "HWC#57=Enc:1",
            "HWC#59=Enc:2", "HWC#59=Press", "HWC#1.2=Down");
        Assert.True(await WaitUntil(() => Received("Down { Hwc = 1"), Within));
        _queue.RunPending();
        _companion.Select(_camB);
        _queue.RunPending();
        _panel.Send("_isSleeping=1", "_isSleeping=0");
        Assert.True(await WaitUntil(() => Received("Value = 0"), Within));
        _queue.RunPending();
        _service.DeactivateGamepad(_service.Gamepads.Single());
        _service.ActivateGamepad(_service.Gamepads.Single());
        _panel.DropConnections();
        await WaitForConnection(2);
        _queue.RunPending();
        await Task.Delay(Ping * 3);

        Assert.Equal(new[] { "Connections?", "list", "ping" }, _panel.Received.Distinct().OrderBy(l => l, StringComparer.Ordinal).ToArray());
    }
}
