using System.ComponentModel;
using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Tests.Devices;
using PtzJoystickControl.Tests.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Commands;

// Focus step (#62) nudges the selected camera's focus position toward far or near, by the share of
// its focus position range set in the mapping: once per detent on a relative input, once per press
// on a button. Each option's value is the signed share in hundredths of a percent, Far positive.
public sealed class FocusStepCommandTests : IDisposable
{
    private readonly FocusStepRecordingCamera _camera = new();
    private readonly string _dir;

    public FocusStepCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PtzFocusStepCommandTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // The real command set, built the way the app builds it for every controller.
    private FakeGamepad Gamepad(SpeedProfilesService? speedProfiles = null)
    {
        var gamepad = new FakeGamepad { SelectedCamera = _camera };
        gamepad.CommandList.AddRange(new CommandsService(new NullCompanionService(), speedProfiles ?? TestSpeedProfiles.Full())
            .GetCommandsForGamepad(gamepad));
        return gamepad;
    }

    private static ICommand FocusStep(FakeGamepad gamepad) => gamepad.Commands.Single(c => c.CommandName == "Focus step");

    private static Input Button(FakeGamepad gamepad, int value)
    {
        var button = new Input("button-1", "Button 1", InputType.Button, gamepad.Commands);
        gamepad.InputList.Add(button);
        button.SelectedCommand = FocusStep(gamepad);
        button.CommandValue = button.Values!.Single(v => v.Value == value);
        return button;
    }

    // The first command on the encoder's detents in `firstDirection`, the second on the other way.
    private static Input Encoder(FakeGamepad gamepad, Direction firstDirection, int firstValue, int secondValue)
    {
        var encoder = new Input("enc-1", "Encoder 1", InputType.Relative, gamepad.Commands);
        gamepad.InputList.Add(encoder);
        encoder.SelectedCommand = FocusStep(gamepad);
        encoder.CommandDirection = encoder.Directions!.Single(d => d.Direction == firstDirection);
        encoder.CommandValue = encoder.Values!.Single(v => v.Value == firstValue);
        encoder.SecondInput!.SelectedCommand = FocusStep(gamepad);
        encoder.SecondInput.CommandValue = encoder.SecondInput.Values!.Single(v => v.Value == secondValue);
        return encoder;
    }

    private static void Press(Input button)
    {
        button.InputValue = 1;
        button.InputValue = 0;
    }

    [Fact]
    public void IsInTheCommandList_RightAfterManualFocus()
    {
        var commands = Gamepad().Commands.ToList();

        var manualFocus = commands.FindIndex(c => c is FocusMoveCommand);
        Assert.IsType<FocusStepCommand>(commands[manualFocus + 1]);
        Assert.Single(commands, c => c is FocusStepCommand);
        Assert.Equal("Focus step", commands[manualFocus + 1].CommandName);
    }

    [Fact]
    public void ItsParameterIsStep_AndItsOptionsGoFromFinestToCoarsest_FarThenNear()
    {
        var command = FocusStep(Gamepad());

        Assert.Equal("Step", command.AxisParameterName);
        Assert.Equal("Step", command.ButtonParameterName);
        Assert.Equal(new[]
        {
            ("Far 0.05 %", 5), ("Near 0.05 %", -5),
            ("Far 0.1 %", 10), ("Near 0.1 %", -10),
            ("Far 0.25 %", 25), ("Near 0.25 %", -25),
            ("Far 0.5 %", 50), ("Near 0.5 %", -50),
            ("Far 1 %", 100), ("Near 1 %", -100),
            ("Far 2 %", 200), ("Near 2 %", -200),
            ("Far 5 %", 500), ("Near 5 %", -500),
        }, command.Options.Select(o => (o.Name, o.Value)).ToArray());
    }

    [Fact]
    public void NoInputHasItUntilMapped_AndPickingItSelectsTheFirstOption()
    {
        var gamepad = Gamepad();
        var button = new Input("button-1", "Button 1", InputType.Button, gamepad.Commands);
        Assert.Null(button.SelectedCommand);

        button.SelectedCommand = FocusStep(gamepad);

        Assert.Equal(5, button.CommandValue!.Value);
    }

    [Fact]
    public void ClockwiseDetents_RunTheFirstCommand_AndAnticlockwiseOnesTheSecond_OncePerDetent()
    {
        var encoder = Encoder(Gamepad(), Direction.High, firstValue: 50, secondValue: -10);

        encoder.Step(3);
        encoder.Step(-2);

        Assert.Equal(new[]
        {
            (0.005, FocusDir.Far), (0.005, FocusDir.Far), (0.005, FocusDir.Far),
            (0.001, FocusDir.Near), (0.001, FocusDir.Near),
        }, _camera.Steps);
        Assert.Equal(0, _camera.OtherCalls);
    }

    [Fact]
    public void AnticlockwiseDetents_RunTheFirstCommand_WhenItIsMappedAnticlockwise()
    {
        var encoder = Encoder(Gamepad(), Direction.Low, firstValue: -25, secondValue: 200);

        encoder.Step(-2);
        encoder.Step(1);

        Assert.Equal(new[] { (0.0025, FocusDir.Near), (0.0025, FocusDir.Near), (0.02, FocusDir.Far) }, _camera.Steps);
    }

    [Fact]
    public void OnAButton_EachPressRunsItOnce()
    {
        var button = Button(Gamepad(), value: -500);

        button.InputValue = 1;
        button.InputValue = 1;
        button.InputValue = 0;
        Press(button);

        Assert.Equal(new[] { (0.05, FocusDir.Near), (0.05, FocusDir.Near) }, _camera.Steps);
        Assert.Equal(0, _camera.OtherCalls);
    }

    // Unlike Manual focus, a focus step is not scaled by the active speed profile.
    [Fact]
    public void TheActiveSpeedProfile_DoesNotScaleIt()
    {
        var fine = new SpeedProfilesService(new MemorySpeedProfileStore(new SpeedProfileList(
            new[] { new SpeedProfile("Fine", 10, 20, 30, 40) }, activePosition: 1)));
        var button = Button(Gamepad(fine), value: 100);

        Press(button);

        Assert.Equal(new[] { (0.01, FocusDir.Far) }, _camera.Steps);
    }

    [Fact]
    public void WithNoSelectedCamera_NothingHappens()
    {
        var gamepad = Gamepad();
        var button = Button(gamepad, value: 50);
        gamepad.SelectedCamera = null;

        var thrown = Record.Exception(() => Press(button));

        Assert.Null(thrown);
        Assert.Empty(_camera.Steps);
    }

    // A hand-edited settings file can hold any value: beyond ±500 it is limited to 5 %.
    [Theory]
    [InlineData(501, 0.05, FocusDir.Far)]
    [InlineData(-501, 0.05, FocusDir.Near)]
    [InlineData(9999, 0.05, FocusDir.Far)]
    [InlineData(-9999, 0.05, FocusDir.Near)]
    [InlineData(int.MaxValue, 0.05, FocusDir.Far)]
    [InlineData(int.MinValue, 0.05, FocusDir.Near)]
    [InlineData(1, 0.0001, FocusDir.Far)]
    [InlineData(-7, 0.0007, FocusDir.Near)]
    public void ASavedValueOffTheList_IsLimitedTo5Percent(int saved, double share, FocusDir direction)
    {
        var button = ButtonLoadedWithSavedValue(saved);

        var thrown = Record.Exception(() => Press(button));

        Assert.Null(thrown);
        Assert.Equal(new[] { (share, direction) }, _camera.Steps);
    }

    [Fact]
    public void ASavedValueOf0_DoesNothing()
    {
        var button = ButtonLoadedWithSavedValue(0);

        var thrown = Record.Exception(() => Press(button));

        Assert.Null(thrown);
        Assert.Empty(_camera.Steps);
    }

    private Input ButtonLoadedWithSavedValue(int value)
    {
        var path = Path.Combine(_dir, GamepadSettingsStore.FileName);
        var gamepad = Gamepad();
        var settings = new GamepadSettings
        {
            Id = gamepad.Id,
            Name = gamepad.Name,
            IsActivated = true,
            Inputs = new List<InputSettings>
            {
                new()
                {
                    Id = "button-1",
                    CommandType = typeof(FocusStepCommand).ToString(),
                    CommandValue = new CommandValueOption("Hand-edited", value),
                    SpeedIsPercentage = true,
                },
            },
        };
        Assert.True(new GamepadSettingsStore(path).SaveGamepadSettings(settings));

        var button = new Input("button-1", "Button 1", InputType.Button, gamepad.Commands);
        gamepad.InputList.Add(button);
        GamepadInputSetup.Configure(gamepad, new GamepadSettingsStore(path).GetGamepadSettingsById(gamepad.Id));
        Assert.IsType<FocusStepCommand>(button.SelectedCommand);
        Assert.Equal(value, button.CommandValue!.Value);
        return button;
    }

    // The mapping (command and value) saves and loads with the controller's settings, on a relative
    // input (both commands) and on a button, and the loaded mapping runs.
    [Fact]
    public void TheMapping_SavesAndLoads_OnARelativeInputAndOnAButton()
    {
        var path = Path.Combine(_dir, GamepadSettingsStore.FileName);
        var saved = Gamepad();
        Encoder(saved, Direction.Low, firstValue: -50, secondValue: 10);
        Button(saved, value: 200);
        Assert.True(new GamepadSettingsStore(path).SaveGamepadSettings(new GamepadSettings(saved)));

        var loaded = Gamepad();
        var encoder = new Input("enc-1", "Encoder 1", InputType.Relative, loaded.Commands);
        var button = new Input("button-1", "Button 1", InputType.Button, loaded.Commands);
        loaded.InputList.AddRange(new IInput[] { encoder, button });
        GamepadInputSetup.Configure(loaded, new GamepadSettingsStore(path).GetGamepadSettingsById(loaded.Id));

        Assert.IsType<FocusStepCommand>(encoder.SelectedCommand);
        Assert.Equal(Direction.Low, encoder.CommandDirection!.Direction);
        Assert.Equal(-50, encoder.CommandValue!.Value);
        Assert.IsType<FocusStepCommand>(encoder.SecondInput!.SelectedCommand);
        Assert.Equal(10, encoder.SecondInput.CommandValue!.Value);
        Assert.IsType<FocusStepCommand>(button.SelectedCommand);
        Assert.Equal(200, button.CommandValue!.Value);

        encoder.Step(-1);
        encoder.Step(1);
        Press(button);
        Assert.Equal(new[] { (0.005, FocusDir.Near), (0.001, FocusDir.Far), (0.02, FocusDir.Far) }, _camera.Steps);
    }

    /// <summary>A camera that records each focus step's share and direction, and counts every other call.</summary>
    private sealed class FocusStepRecordingCamera : ICamera
    {
        public List<(double Share, FocusDir Direction)> Steps { get; } = new();
        public int OtherCalls { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public string Name => "Focus step recorder";
        public int? CompanionNumber { get; set; }
        public bool Connected => true;
        public bool IsMoving => false;

        public void FocusStep(double focusShare, FocusDir focusDir) => Steps.Add((focusShare, focusDir));
        public void Pan(double panSpeed, PanDir panDir) => OtherCalls++;
        public void Tilt(double tiltSpeed, TiltDir tiltDir) => OtherCalls++;
        public void Zoom(double zoomSpeed, ZoomDir zoomDir) => OtherCalls++;
        public void Focus(double focusSpeed, FocusDir focusDir) => OtherCalls++;
        public void Stop() => OtherCalls++;
        public void FocusMode(FocusMode focusMode) => OtherCalls++;
        public void FocusLock(FocusLock focusLock) => OtherCalls++;
        public void Power(Power power) => OtherCalls++;
        public void Preset(Preset preset, byte presetNumber) => OtherCalls++;
        public void PresetRecallSpeed(byte speed) => OtherCalls++;
    }
}
