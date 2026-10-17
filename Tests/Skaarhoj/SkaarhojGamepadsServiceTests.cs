using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Commands;
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

    private static void Encoder(SkaarhojGamepadsService service, int hwc, int steps) =>
        service.OnMessage(new RawPanelMessage.Encoder(hwc, steps));

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
    public void FocusKnobTurn_IsAppliedOnlyByTheInputLoop_OncePerStep()
    {
        var service = NewService();
        service.ActivateGamepad(Info(service));
        service.OnLinkUp();
        _queue.RunPending();
        var gamepad = Active(service);
        FocusKnob.MapToFocusStep(gamepad);
        gamepad.SelectedCamera = _camA;

        Encoder(service, 59, 2);
        Encoder(service, 59, -3);
        Assert.Empty(_camA.Calls);

        _queue.RunPending();
        Assert.Equal(
            new[] { FocusKnob.Far, FocusKnob.Far, FocusKnob.Near, FocusKnob.Near, FocusKnob.Near },
            _camA.Calls);
    }

    [Fact]
    public void FocusKnobSteps_KeepTheirOrder_WithTheJoysticksLines()
    {
        var service = NewService();
        var gamepad = ActivePanning(service);
        FocusKnob.MapToFocusStep(gamepad);
        var before = _camA.Calls.Count;

        Encoder(service, 59, 1);
        Speed(service, 60, 0);
        Encoder(service, 59, -1);
        Speed(service, 60, -500);
        _queue.RunPending();

        Assert.Equal(new[] { FocusKnob.Far, "Pan 0 Stop", FocusKnob.Near, "Pan 1 Left" }, _camA.Calls.Skip(before));
    }

    [Fact]
    public void FocusKnobTurn_AfterLinkDown_StepsNothing()
    {
        var service = NewService();
        FocusKnob.MapToFocusStep(ActivePanning(service));
        var calls = _camA.Calls.Count;

        service.OnLinkDown("no ack");
        Encoder(service, 59, 1);
        _queue.RunPending();

        Assert.Equal(calls, _camA.Calls.Count);
    }

    [Fact]
    public void FocusKnobTurn_WhileAsleep_StepsNothing()
    {
        var service = NewService();
        FocusKnob.MapToFocusStep(ActivePanning(service));
        var calls = _camA.Calls.Count;

        Sleeping(service, true);
        Encoder(service, 59, 1);
        _queue.RunPending();

        Assert.Equal(calls, _camA.Calls.Count);
    }

    [Fact]
    public void FocusKnobTurnQueuedBeforeDeactivate_StepsNothing()
    {
        var service = NewService();
        FocusKnob.MapToFocusStep(ActivePanning(service));
        var calls = _camA.Calls.Count;

        Encoder(service, 59, 1);
        service.DeactivateGamepad(Info(service));
        _queue.RunPending();

        Assert.Equal(calls, _camA.Calls.Count);
    }

    [Fact]
    public void Wake_FocusKnobKeepsItsMapping_AndStepsAgain()
    {
        var service = NewService();
        var before = ActivePanning(service);
        FocusKnob.MapToFocusStep(before);
        Sleeping(service, true);
        _queue.RunPending();

        Sleeping(service, false);
        _queue.RunPending();
        var after = Active(service);
        after.SelectedCamera = _camA;
        var calls = _camA.Calls.Count;
        Encoder(service, 59, -1);
        _queue.RunPending();

        Assert.NotSame(before, after);
        Assert.Equal(new[] { FocusKnob.Near }, _camA.Calls.Skip(calls));
    }

    [Fact]
    public void IrisWheelScroll_IsAppliedOnlyByTheInputLoop_OncePerStep()
    {
        var service = NewService();
        service.ActivateGamepad(Info(service));
        service.OnLinkUp();
        _queue.RunPending();
        var gamepad = Active(service);
        IrisWheel.MapToFocusStep(gamepad);
        gamepad.SelectedCamera = _camA;

        Encoder(service, 57, 2);
        Encoder(service, 57, -3);
        Assert.Empty(_camA.Calls);

        _queue.RunPending();
        Assert.Equal(
            new[] { IrisWheel.Far, IrisWheel.Far, IrisWheel.Near, IrisWheel.Near, IrisWheel.Near },
            _camA.Calls);
    }

    [Fact]
    public void IrisWheelSteps_KeepTheirOrder_WithTheFocusWheelsAndTheJoysticksLines()
    {
        var service = NewService();
        var gamepad = ActivePanning(service);
        FocusKnob.MapToFocusStep(gamepad);
        IrisWheel.MapToFocusStep(gamepad);
        var before = _camA.Calls.Count;

        Encoder(service, 57, 1);
        Encoder(service, 59, 1);
        Speed(service, 60, 0);
        Encoder(service, 57, -1);
        Speed(service, 60, -500);
        _queue.RunPending();

        Assert.Equal(
            new[] { IrisWheel.Far, FocusKnob.Far, "Pan 0 Stop", IrisWheel.Near, "Pan 1 Left" },
            _camA.Calls.Skip(before));
    }

    [Fact]
    public void IrisWheelScroll_AfterLinkDown_StepsNothing()
    {
        var service = NewService();
        IrisWheel.MapToFocusStep(ActivePanning(service));
        var calls = _camA.Calls.Count;

        service.OnLinkDown("no ack");
        Encoder(service, 57, 1);
        _queue.RunPending();

        Assert.Equal(calls, _camA.Calls.Count);
    }

    [Fact]
    public void IrisWheelScroll_WhileAsleep_StepsNothing()
    {
        var service = NewService();
        IrisWheel.MapToFocusStep(ActivePanning(service));
        var calls = _camA.Calls.Count;

        Sleeping(service, true);
        Encoder(service, 57, 1);
        _queue.RunPending();

        Assert.Equal(calls, _camA.Calls.Count);
    }

    [Fact]
    public void IrisWheelScrollQueuedBeforeDeactivate_StepsNothing()
    {
        var service = NewService();
        IrisWheel.MapToFocusStep(ActivePanning(service));
        var calls = _camA.Calls.Count;

        Encoder(service, 57, 1);
        service.DeactivateGamepad(Info(service));
        _queue.RunPending();

        Assert.Equal(calls, _camA.Calls.Count);
    }

    [Fact]
    public void Wake_IrisWheelKeepsItsMapping_AndStepsAgain()
    {
        var service = NewService();
        var before = ActivePanning(service);
        IrisWheel.MapToFocusStep(before);
        Sleeping(service, true);
        _queue.RunPending();

        Sleeping(service, false);
        _queue.RunPending();
        var after = Active(service);
        after.SelectedCamera = _camA;
        var calls = _camA.Calls.Count;
        Encoder(service, 57, -1);
        _queue.RunPending();

        Assert.NotSame(before, after);
        Assert.Equal(new[] { IrisWheel.Near }, _camA.Calls.Skip(calls));
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
        FocusKnob.MapToFocusStep(gamepad);
        IrisWheel.MapToFocusStep(gamepad);
        var calls = _camA.Calls.Count;
        var stops = _camA.StopCalls;
        var lines = _trace.Messages.Count(m => m.Contains(Address));

        service.OnMessage(new RawPanelMessage.Down(63, null));
        // The Focus knob's press, and anything but a turn on 59: the press stays Companion's.
        service.OnMessage(new RawPanelMessage.Press(59));
        service.OnMessage(new RawPanelMessage.Down(59, null));
        service.OnMessage(new RawPanelMessage.Up(59, null));
        service.OnMessage(new RawPanelMessage.Speed(59, 500));
        // The same for the Iris wheel on 57.
        service.OnMessage(new RawPanelMessage.Press(57));
        service.OnMessage(new RawPanelMessage.Down(57, null));
        service.OnMessage(new RawPanelMessage.Up(57, null));
        service.OnMessage(new RawPanelMessage.Speed(57, 500));
        // Other encoders: Knob A, Knob H, and the joystick's own HWC.
        service.OnMessage(new RawPanelMessage.Encoder(46, 1));
        service.OnMessage(new RawPanelMessage.Encoder(53, -1));
        service.OnMessage(new RawPanelMessage.Encoder(60, 1));
        service.OnMessage(new RawPanelMessage.Reply("isSleeping", "maybe"));
        service.OnMessage(new RawPanelMessage.Unknown("HWC#60=Speed:x"));
        _queue.RunPending();

        Assert.Same(gamepad, Active(service));
        Assert.Equal(calls, _camA.Calls.Count);
        Assert.Equal(stops, _camA.StopCalls);
        Assert.Equal(lines, _trace.Messages.Count(m => m.Contains(Address)));
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

    // The second command is mapped last, as in the controller page: it must be saved on its own.
    [Fact]
    public void FocusKnobMapping_SavesAndReloadsById_WithBothCommandsAndTheDirection()
    {
        var first = NewService();
        first.ActivateGamepad(Info(first));
        first.OnLinkUp();
        _queue.RunPending();
        FocusKnob.MapToFocusStep(Active(first));
        // Low is not the default, so a direction lost on the way gives Far for a clockwise step.
        var focus = Active(first).Inputs.Single(i => i.Name == "Focus");
        focus.CommandDirection = focus.Directions!.Single(d => d.Direction == Direction.Low);

        var saved = Assert.Single(new GamepadSettingsStore(SettingsPath).GetAllGamepadSettings());
        Assert.Equal(Id, saved.Id);
        Assert.Single(saved.Inputs!, i => i.Id == "HWC59");

        var second = NewService();
        second.OnLinkUp();
        _queue.RunPending();
        Active(second).SelectedCamera = _camA;
        Encoder(second, 59, 1);
        Encoder(second, 59, -1);
        _queue.RunPending();

        Assert.Equal(new[] { FocusKnob.Near, FocusKnob.Far }, _camA.Calls);
    }

    [Fact]
    public void SavedMappingsWithoutTheFocusKnob_LoadItUnmapped_AndTheOthersAsSaved()
    {
        // A file saved before the panel had a Focus input.
        var first = NewService();
        first.ActivateGamepad(Info(first));
        first.OnLinkUp();
        _queue.RunPending();
        MapLrToPan(Active(first));
        var store = new GamepadSettingsStore(SettingsPath);
        var saved = Assert.Single(store.GetAllGamepadSettings());
        Assert.Equal(1, saved.Inputs!.RemoveAll(i => i.Id == "HWC59"));
        Assert.True(store.SaveGamepadSettings(saved));

        var second = NewService();
        second.OnLinkUp();
        _queue.RunPending();

        var gamepad = Active(second);
        Assert.IsType<PanCommand>(gamepad.Inputs.Single(i => i.Name == "LR").SelectedCommand);
        var focus = gamepad.Inputs.Single(i => i.Name == "Focus");
        Assert.Null(focus.SelectedCommand);
        Assert.Null(focus.SecondInput!.SelectedCommand);
    }

    // As for the Focus knob: the second command is mapped last, so it must be saved on its own.
    [Fact]
    public void IrisWheelMapping_SavesAndReloadsById_WithBothCommandsAndTheDirection()
    {
        var first = NewService();
        first.ActivateGamepad(Info(first));
        first.OnLinkUp();
        _queue.RunPending();
        IrisWheel.MapToFocusStep(Active(first));
        // Low is not the default, so a direction lost on the way gives Far for a step scrolled up.
        var iris = Active(first).Inputs.Single(i => i.Name == "Iris");
        iris.CommandDirection = iris.Directions!.Single(d => d.Direction == Direction.Low);

        var saved = Assert.Single(new GamepadSettingsStore(SettingsPath).GetAllGamepadSettings());
        Assert.Equal(Id, saved.Id);
        Assert.Single(saved.Inputs!, i => i.Id == "HWC57");

        var second = NewService();
        second.OnLinkUp();
        _queue.RunPending();
        Active(second).SelectedCamera = _camA;
        Encoder(second, 57, 1);
        Encoder(second, 57, -1);
        _queue.RunPending();

        Assert.Equal(new[] { IrisWheel.Near, IrisWheel.Far }, _camA.Calls);
    }

    [Fact]
    public void SavedMappingsWithoutTheIrisWheel_LoadItUnmapped_AndTheOthersAsSaved()
    {
        // A file saved before the panel had an Iris input, with the Focus knob mapped.
        var first = NewService();
        first.ActivateGamepad(Info(first));
        first.OnLinkUp();
        _queue.RunPending();
        MapLrToPan(Active(first));
        FocusKnob.MapToFocusStep(Active(first));
        var store = new GamepadSettingsStore(SettingsPath);
        var saved = Assert.Single(store.GetAllGamepadSettings());
        Assert.Equal(1, saved.Inputs!.RemoveAll(i => i.Id == "HWC57"));
        Assert.True(store.SaveGamepadSettings(saved));

        var second = NewService();
        second.OnLinkUp();
        _queue.RunPending();

        var gamepad = Active(second);
        Assert.IsType<PanCommand>(gamepad.Inputs.Single(i => i.Name == "LR").SelectedCommand);
        var focus = gamepad.Inputs.Single(i => i.Name == "Focus");
        Assert.IsType<FocusStepCommand>(focus.SelectedCommand);
        Assert.IsType<FocusStepCommand>(focus.SecondInput!.SelectedCommand);
        var iris = gamepad.Inputs.Single(i => i.Name == "Iris");
        Assert.Null(iris.SelectedCommand);
        Assert.Null(iris.SecondInput!.SelectedCommand);
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
