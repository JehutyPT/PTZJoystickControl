using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.SkaarhojPanel;
using PtzJoystickControl.Tests.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Skaarhoj;

// The panel controller's lifecycle, driven through the service's link-thread entry points
// (OnLinkUp, OnLinkDown, OnMessage) and the input loop (RunPending), without a socket.
// SkaarhojGamepadsServiceLinkTests covers the same paths through a real link and FakeRawPanel.
public sealed class SkaarhojGamepadsServiceTests : IDisposable
{
    private const string Address = "192.0.2.10";
    private const string Id = "skaarhoj:192.0.2.10";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptz-skaarhoj-" + Guid.NewGuid().ToString("N"));
    private readonly MotionRecordingCamera _camA = new("A");
    private readonly MotionRecordingCamera _camB = new("B");
    private readonly FakeCamerasService _cameras;
    private readonly RaisingCompanionService _companion = new();
    private readonly InputQueue _queue = new();
    private readonly TraceRecorder _trace = new();

    public SkaarhojGamepadsServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _cameras = new FakeCamerasService(_camA, _camB);
    }

    public void Dispose()
    {
        _trace.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string SettingsPath => Path.Combine(_dir, "SkaarhojGamepads.json");

    private SkaarhojGamepadsService NewService(Action<Action>? notifyOnUiThread = null) => new(Address,
        new GamepadSettingsStore(SettingsPath), _cameras, new CommandsService(_companion, TestSpeedProfiles.Full()), _companion, _queue,
        notifyOnUiThread);

    private static IGamepadInfo Info(SkaarhojGamepadsService service) => Assert.Single(service.Gamepads);

    private static SkaarhojGamepad Active(SkaarhojGamepadsService service) =>
        Assert.IsType<SkaarhojGamepad>(Assert.Single(service.ActiveGamepads));

    private static void MapLrToPan(SkaarhojGamepad gamepad) =>
        gamepad.Inputs.Single(i => i.Name == "LR").SelectedCommand = gamepad.Commands.OfType<PanCommand>().Single();

    private static void Speed(SkaarhojGamepadsService service, int hwc, int value) =>
        service.OnMessage(new RawPanelMessage.Speed(hwc, value));

    private static void Sleeping(SkaarhojGamepadsService service, bool asleep) =>
        service.OnMessage(new RawPanelMessage.Reply("isSleeping", asleep ? "1" : "0"));

    // Activated, link up, LR mapped to pan, camera A selected and panning.
    private SkaarhojGamepad ActivePanning(SkaarhojGamepadsService service)
    {
        service.ActivateGamepad(Info(service));
        service.OnLinkUp();
        _queue.RunPending();
        var gamepad = Active(service);
        MapLrToPan(gamepad);
        gamepad.SelectedCamera = _camA;
        Speed(service, 60, 500);
        _queue.RunPending();
        Assert.True(_camA.IsMoving);
        return gamepad;
    }

    [Fact]
    public void ListsOnePanel_ByAddress_Disconnected_UntilTheLinkIsUp()
    {
        var service = NewService();

        var info = Info(service);
        Assert.Equal(Id, info.Id);
        Assert.Contains(Address, info.Name);
        Assert.False(info.IsConnected);
        Assert.False(info.IsActivated);
        Assert.Empty(service.ActiveGamepads);
    }

    [Fact]
    public void LinkUp_IsAppliedOnlyByTheInputLoop()
    {
        var service = NewService();
        service.ActivateGamepad(Info(service));

        service.OnLinkUp();
        Assert.False(Info(service).IsConnected);
        Assert.Empty(service.ActiveGamepads);

        _queue.RunPending();
        Assert.True(Info(service).IsConnected);
        Assert.True(Active(service).IsConnected);
    }

    [Fact]
    public void LinkUp_WhenNotActivated_ShowsConnected_ButActivatesNothing()
    {
        var service = NewService();

        service.OnLinkUp();
        _queue.RunPending();

        Assert.True(Info(service).IsConnected);
        Assert.Empty(service.ActiveGamepads);
    }

    [Fact]
    public void Activate_WhileConnected_AddsAGamepadWithEveryAxisAtZero_AndMovesNothing()
    {
        var service = NewService();
        service.OnLinkUp();
        _queue.RunPending();

        service.ActivateGamepad(Info(service));

        var gamepad = Active(service);
        Assert.Equal(Id, gamepad.Id);
        Assert.True(Info(service).IsActivated);
        Assert.All(gamepad.Inputs, i => Assert.Equal(0f, i.InputValue));
        Assert.Empty(_camA.Calls);
        Assert.Empty(_camB.Calls);
    }

    [Fact]
    public void Speed_IsAppliedOnlyByTheInputLoop()
    {
        var service = NewService();
        service.ActivateGamepad(Info(service));
        service.OnLinkUp();
        _queue.RunPending();
        var gamepad = Active(service);
        MapLrToPan(gamepad);
        gamepad.SelectedCamera = _camA;

        Speed(service, 60, 500);
        Assert.Empty(_camA.Calls);

        _queue.RunPending();
        Assert.Equal(new[] { "Pan 1 Right" }, _camA.Calls);
    }

    [Fact]
    public void LinkDown_StopsEveryCameraTheGamepadDrove_RemovesIt_AndShowsDisconnected()
    {
        var service = NewService();
        var gamepad = ActivePanning(service);
        gamepad.SelectedCamera = _camB;
        Speed(service, 60, -500);
        _queue.RunPending();
        var stopsA = _camA.StopCalls;

        service.OnLinkDown("no ack");
        Assert.Equal(0, _camB.StopCalls);
        _queue.RunPending();

        Assert.Equal(1, _camA.StopCalls - stopsA);
        Assert.Equal(1, _camB.StopCalls);
        Assert.Empty(service.ActiveGamepads);
        Assert.False(gamepad.IsConnected);
        Assert.False(Info(service).IsConnected);
        Assert.True(Info(service).IsActivated);
    }

    [Fact]
    public void Reconnect_ActivatesAFreshGamepad_WithEveryAxisAtZero_AndNoMotionUntilSpeedArrives()
    {
        var service = NewService();
        var before = ActivePanning(service);
        service.OnLinkDown("no ack");
        _queue.RunPending();
        var callsBefore = _camA.Calls.Count;

        service.OnLinkUp();
        _queue.RunPending();

        var after = Active(service);
        Assert.NotSame(before, after);
        Assert.All(after.Inputs, i => Assert.Equal(0f, i.InputValue));
        Assert.IsType<PanCommand>(after.Inputs.Single(i => i.Name == "LR").SelectedCommand);
        Assert.Equal(callsBefore, _camA.Calls.Count);

        after.SelectedCamera = _camA;
        Speed(service, 60, 250);
        _queue.RunPending();
        Assert.Equal(callsBefore + 1, _camA.Calls.Count);
    }

    [Fact]
    public void Sleep_StopsEveryCameraTheGamepadDrove_AndMakesItUnavailable()
    {
        var service = NewService();
        var gamepad = ActivePanning(service);
        var stops = _camA.StopCalls;

        Sleeping(service, true);
        _queue.RunPending();

        Assert.Equal(1, _camA.StopCalls - stops);
        Assert.Empty(service.ActiveGamepads);
        Assert.False(gamepad.IsConnected);
        // Shown asleep, not disconnected: the link is still up.
        Assert.True(Info(service).IsAsleep);
        Assert.True(Info(service).IsConnected);
        Assert.Contains(_trace.Messages, m => m.Contains(Address) && m.Contains("asleep"));
    }

    [Fact]
    public void Speed_WhileAsleep_MovesNothing()
    {
        var service = NewService();
        ActivePanning(service);
        Sleeping(service, true);
        _queue.RunPending();
        var calls = _camA.Calls.Count;

        Speed(service, 60, -500);
        _queue.RunPending();

        Assert.Equal(calls, _camA.Calls.Count);
    }

    [Fact]
    public void Wake_MakesItAvailableAgain_WithEveryAxisAtZero()
    {
        var service = NewService();
        var before = ActivePanning(service);
        Sleeping(service, true);
        _queue.RunPending();
        var calls = _camA.Calls.Count;

        Sleeping(service, false);
        _queue.RunPending();

        var after = Active(service);
        Assert.NotSame(before, after);
        Assert.True(Info(service).IsConnected);
        Assert.False(Info(service).IsAsleep);
        Assert.All(after.Inputs, i => Assert.Equal(0f, i.InputValue));
        Assert.Equal(calls, _camA.Calls.Count);
    }

    [Fact]
    public void RepeatedSleepReports_StopOnlyOnce()
    {
        var service = NewService();
        ActivePanning(service);
        var stops = _camA.StopCalls;

        Sleeping(service, true);
        Sleeping(service, true);
        _queue.RunPending();

        Assert.Equal(1, _camA.StopCalls - stops);
    }

    [Fact]
    public void LinkUpAfterSleep_TreatsThePanelAsAwake()
    {
        // The panel does not report its sleep state on connect; asleep, its joystick sends nothing anyway.
        var service = NewService();
        ActivePanning(service);
        Sleeping(service, true);
        service.OnLinkDown("no ack");
        service.OnLinkUp();
        _queue.RunPending();

        Assert.True(Info(service).IsConnected);
        Assert.False(Info(service).IsAsleep);
        Assert.Single(service.ActiveGamepads);
    }

    [Fact]
    public void SleepAndWake_AreLoggedOnceEach_AsInformation()
    {
        var service = NewService();
        ActivePanning(service);

        Sleeping(service, true);
        Sleeping(service, true);
        _queue.RunPending();
        Sleeping(service, false);
        Sleeping(service, false);
        _queue.RunPending();

        var lines = _trace.Events.Where(e => e.Message.Contains(Address)).ToList();
        Assert.Single(lines, e => e.Message.Contains("asleep"));
        Assert.Single(lines, e => e.Message.Contains("awake"));
        Assert.All(lines.Where(e => e.Message.Contains("asleep") || e.Message.Contains("awake")),
            e => Assert.Equal(System.Diagnostics.TraceEventType.Information, e.Type));
    }

    [Fact]
    public void Asleep_WhenNotActivated_IsShownToo()
    {
        var service = NewService();
        service.OnLinkUp();
        Sleeping(service, true);
        _queue.RunPending();

        Assert.True(Info(service).IsAsleep);
        Assert.True(Info(service).IsConnected);
        Assert.Empty(service.ActiveGamepads);
    }

    [Fact]
    public void LinkDownWhileAsleep_ShowsDisconnected_NotAsleep()
    {
        var service = NewService();
        ActivePanning(service);
        Sleeping(service, true);
        _queue.RunPending();

        service.OnLinkDown("no ack");
        _queue.RunPending();

        Assert.False(Info(service).IsAsleep);
        Assert.False(Info(service).IsConnected);
    }

    [Fact]
    public void ActivateWhileAsleep_IsSaved_AndConnectsOnWake()
    {
        var service = NewService();
        service.OnLinkUp();
        Sleeping(service, true);
        _queue.RunPending();

        service.ActivateGamepad(Info(service));
        Assert.True(Info(service).IsActivated);
        Assert.Empty(service.ActiveGamepads);

        Sleeping(service, false);
        _queue.RunPending();
        Assert.Single(service.ActiveGamepads);
    }

    [Fact]
    public void ConnectedAndAsleepChanges_AreRaisedThroughTheUiThreadMarshaller()
    {
        var posted = new List<Action>();
        var raisedInside = new List<string>();
        var raisedOutside = new List<string>();
        var inMarshaller = false;
        var service = NewService(a => posted.Add(() => { inMarshaller = true; try { a(); } finally { inMarshaller = false; } }));
        Info(service).PropertyChanged += (_, e) => (inMarshaller ? raisedInside : raisedOutside).Add(e.PropertyName!);

        service.OnLinkUp();
        Sleeping(service, true);
        _queue.RunPending();

        // The input loop has applied the state; the UI hears of it only when the posted work runs.
        Assert.True(Info(service).IsAsleep);
        Assert.DoesNotContain(nameof(IGamepadInfo.IsConnected), raisedOutside);
        Assert.DoesNotContain(nameof(IGamepadInfo.IsAsleep), raisedOutside);
        Assert.Empty(raisedInside);

        foreach (var action in posted)
            action();

        Assert.Contains(nameof(IGamepadInfo.IsConnected), raisedInside);
        Assert.Contains(nameof(IGamepadInfo.IsAsleep), raisedInside);
    }

    [Fact]
    public void Deactivate_StopsEveryCameraTheGamepadDrove()
    {
        var service = NewService();
        var gamepad = ActivePanning(service);
        var stops = _camA.StopCalls;

        service.DeactivateGamepad(Info(service));

        Assert.Equal(1, _camA.StopCalls - stops);
        Assert.Empty(service.ActiveGamepads);
        Assert.False(Info(service).IsActivated);
        Assert.False(gamepad.IsActivated);
        Assert.True(Info(service).IsConnected);
    }

    [Fact]
    public void SpeedQueuedBeforeDeactivate_MovesNothingAfterTheStop()
    {
        var service = NewService();
        ActivePanning(service);
        Speed(service, 60, 0);
        _queue.RunPending();
        var calls = _camA.Calls.Count;

        Speed(service, 60, 500);
        service.DeactivateGamepad(Info(service));
        _queue.RunPending();

        Assert.Equal(calls, _camA.Calls.Count);
        Assert.False(_camA.IsMoving);
    }

    [Fact]
    public void CompanionSelection_ReachesThePanelGamepad_OnTheInputLoop()
    {
        var service = NewService();
        service.ActivateGamepad(Info(service));
        service.OnLinkUp();
        _queue.RunPending();
        var gamepad = Active(service);

        _companion.Select(_camB);
        Assert.Null(gamepad.SelectedCamera);

        _queue.RunPending();
        Assert.Same(_camB, gamepad.SelectedCamera);
    }

    [Fact]
    public void OtherMessages_AreIgnored()
    {
        var service = NewService();
        var gamepad = ActivePanning(service);
        var calls = _camA.Calls.Count;
        var stops = _camA.StopCalls;

        service.OnMessage(new RawPanelMessage.Down(63, null));
        service.OnMessage(new RawPanelMessage.Encoder(57, 1));
        service.OnMessage(new RawPanelMessage.Reply("isSleeping", "maybe"));
        service.OnMessage(new RawPanelMessage.Unknown("HWC#60=Speed:x"));
        _queue.RunPending();

        Assert.Same(gamepad, Active(service));
        Assert.Equal(calls, _camA.Calls.Count);
        Assert.Equal(stops, _camA.StopCalls);
    }

    [Fact]
    public void Mappings_SaveAndReloadById_InTheirOwnFile()
    {
        var first = NewService();
        first.ActivateGamepad(Info(first));
        first.OnLinkUp();
        _queue.RunPending();
        MapLrToPan(Active(first));

        var second = NewService();
        Assert.True(Info(second).IsActivated);
        second.OnLinkUp();
        _queue.RunPending();

        Assert.IsType<PanCommand>(Active(second).Inputs.Single(i => i.Name == "LR").SelectedCommand);
        var saved = new GamepadSettingsStore(SettingsPath).GetAllGamepadSettings();
        Assert.Equal(Id, Assert.Single(saved).Id);
        Assert.Equal(new[] { "SkaarhojGamepads.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Expo_SavesAndReloads_InTheirOwnFile()
    {
        var first = NewService();
        first.ActivateGamepad(Info(first));
        first.OnLinkUp();
        _queue.RunPending();
        MapLrToPan(Active(first));
        Active(first).Inputs.Single(i => i.Name == "LR").Expo = 0.6f;

        var saved = Assert.Single(new GamepadSettingsStore(SettingsPath).GetAllGamepadSettings());
        Assert.Equal(0.6f, Assert.Single(saved.Inputs!, i => i.Id == "HWC60").Expo);

        var second = NewService();
        second.OnLinkUp();
        _queue.RunPending();
        Assert.Equal(0.6f, Active(second).Inputs.Single(i => i.Name == "LR").Expo);
    }

    [Fact]
    public void ActivationChosenWhileDisconnected_IsSaved()
    {
        var first = NewService();
        first.ActivateGamepad(Info(first));
        Assert.True(Info(first).IsActivated);
        Assert.True(Info(NewService()).IsActivated);

        first.DeactivateGamepad(Info(first));
        Assert.False(Info(NewService()).IsActivated);
    }

    [Fact]
    public void InTheControllerList_SelectingTheJoystickStopsThePanel_AndViceVersa_NeverBothActive()
    {
        var usb = new CompositeGamepadsServiceTests.FakeGamepadsService("usb", _queue);
        var joystick = usb.AddInfo("joystick");
        var panel = NewService();
        var composite = new CompositeGamepadsService(_queue, usb, panel);
        Assert.Equal(new[] { "joystick", Id }, composite.Gamepads.Select(g => g.Id));

        composite.ActivateGamepad(Info(panel));
        panel.OnLinkUp();
        _queue.RunPending();
        var gamepad = Active(panel);
        MapLrToPan(gamepad);
        gamepad.SelectedCamera = _camA;
        Speed(panel, 60, 500);
        _queue.RunPending();
        Assert.Equal(Id, Assert.Single(composite.ActiveGamepads).Id);
        var stops = _camA.StopCalls;

        composite.ActivateGamepad(joystick);

        Assert.Equal(1, _camA.StopCalls - stops);
        Assert.False(Info(panel).IsActivated);
        Assert.Equal("joystick", Assert.Single(composite.ActiveGamepads).Id);

        composite.ActivateGamepad(Info(panel));

        Assert.False(joystick.IsActivated);
        Assert.Equal(Id, Assert.Single(composite.ActiveGamepads).Id);
    }

    [Fact]
    public void SavedActive_ComingUpWhileAnotherControllerIsActive_StaysOff_AndSavesIt()
    {
        var saved = NewService();
        saved.ActivateGamepad(Info(saved));
        var usb = new CompositeGamepadsServiceTests.FakeGamepadsService("usb", _queue);
        var joystick = usb.AddInfo("joystick");
        var panel = NewService();
        var composite = new CompositeGamepadsService(_queue, usb, panel);
        // The USB source activates a joystick on its own, as it does when one saved as active is plugged in.
        usb.ActivateGamepad(joystick);

        panel.OnLinkUp();
        _queue.RunPending();

        Assert.Empty(panel.ActiveGamepads);
        Assert.False(Info(panel).IsActivated);
        Assert.True(Info(panel).IsConnected);
        Assert.Equal("joystick", Assert.Single(composite.ActiveGamepads).Id);
        Assert.False(Info(NewService()).IsActivated);
        Assert.Contains(_trace.Events, e => e.Type == System.Diagnostics.TraceEventType.Warning && e.Message.Contains(Address));
    }

    [Fact]
    public void ForeignGamepadInfo_IsIgnored()
    {
        var service = NewService();
        service.OnLinkUp();
        _queue.RunPending();
        var foreign = new ForeignInfo();

        service.ActivateGamepad(foreign);
        service.DeactivateGamepad(foreign);

        Assert.Empty(service.ActiveGamepads);
        Assert.False(Info(service).IsActivated);
    }

    private sealed class ForeignInfo : IGamepadInfo
    {
        public string Id => "usb-joystick#0";
        public string Name => "USB joystick";
        public bool IsActivated { get; set; }
        public bool IsConnected { get; set; } = true;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }
}
