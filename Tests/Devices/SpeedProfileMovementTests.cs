using System.ComponentModel;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Tests.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// The active speed profile scales every pan, tilt, zoom and focus move on every camera: the camera
// gets the shaped input × the input's Max speed (or Speed) % × the profile's % for that axis (#45).
public sealed class SpeedProfileMovementTests
{
    // Distinct percentages per axis, so a command that used another axis's percentage is caught.
    private static readonly SpeedProfileList TwoProfiles = new(new[]
    {
        new SpeedProfile("Fine", 10, 20, 30, 40),
        new SpeedProfile("Full", 100, 100, 100, 100),
    }, activePosition: 1);

    private readonly FakeGamepad _gamepad = new();
    private readonly SpeedProfilesService _speedProfiles = new(new MemorySpeedProfileStore(TwoProfiles));

    // The real command set, built the way the app builds it for every controller.
    private ICommand AnyCommand(string commandName)
    {
        var commands = new CommandsService(new NullCompanionService(), _speedProfiles).GetCommandsForGamepad(_gamepad).ToList();
        _gamepad.CommandList.AddRange(commands);
        return commands.Single(c => c.CommandName == commandName);
    }

    private IDynamicCommand Command(string commandName) => (IDynamicCommand)AnyCommand(commandName);

    private Input Axis(string commandName, int maxSpeedPercent)
    {
        var command = Command(commandName);
        var axis = new Input("axis-1", "Axis 1", InputType.Axis, _gamepad.Commands) { DeadZone = 0, Saturation = 0 };
        axis.SelectedCommand = command;
        axis.CommandValue = axis.Values!.Single(v => v.Value == maxSpeedPercent);
        return axis;
    }

    private Input Button(string commandName, int speedPercent)
    {
        var command = Command(commandName);
        var button = new Input("button-1", "Button 1", InputType.Button, _gamepad.Commands);
        button.SelectedCommand = command;
        button.CommandValue = button.Values!.Single(v => v.Value == speedPercent);
        button.CommandDirection = button.Directions!.Single(d => d.Direction == Direction.High);
        return button;
    }

    // At full deflection with Max speed 50 %, the input asks for 0.5; the active profile then scales each axis:
    // "Fine" by its percentage for that axis, "Full" not at all.
    [Theory]
    [InlineData("Pan", 0.05)]
    [InlineData("Tilt", 0.10)]
    [InlineData("Zoom", 0.15)]
    [InlineData("Manual focus", 0.20)]
    public void EveryCamera_GetsTheAxisFraction_ScaledByTheActiveProfilesPercentageForThatAxis(string command, double fine)
    {
        var axis = Axis(command, maxSpeedPercent: 50);
        var cameras = new[] { new SpeedRecordingCamera(), new SpeedRecordingCamera() };

        foreach (var (position, expected) in new[] { (1, fine), (2, 0.5) })
        {
            _speedProfiles.SetActive(position);

            foreach (var camera in cameras)
            {
                _gamepad.SelectedCamera = camera;
                axis.InputValue = 1;

                Assert.Equal(1, camera.Runs.Last().Sign);
                Assert.Equal(expected, camera.Runs.Last().Speed, 9);
            }
        }
    }

    [Theory]
    [InlineData("Pan", 0.05)]
    [InlineData("Tilt", 0.10)]
    [InlineData("Zoom", 0.15)]
    [InlineData("Manual focus", 0.20)]
    public void AButton_IsScaledByTheActiveProfile_LikeAnAxis(string command, double expected)
    {
        var camera = new SpeedRecordingCamera();
        _gamepad.SelectedCamera = camera;
        var button = Button(command, speedPercent: 50);

        button.InputValue = 1;

        Assert.Equal(1, camera.Runs.Last().Sign);
        Assert.Equal(expected, camera.Runs.Last().Speed, 9);
    }

    // Decided in #45: a held input is not re-sent when the profile changes; its next movement uses the new one.
    [Theory]
    [InlineData("Pan", 0.05)]
    [InlineData("Tilt", 0.10)]
    [InlineData("Zoom", 0.15)]
    [InlineData("Manual focus", 0.20)]
    public void AProfileChange_AppliesFromTheNextMovement_AndDoesNotResendAHeldInput(string command, double fine)
    {
        var camera = new SpeedRecordingCamera();
        _gamepad.SelectedCamera = camera;
        var axis = Axis(command, maxSpeedPercent: 50);
        axis.InputValue = 1;
        Assert.Equal(fine, camera.Runs.Single().Speed, 9);

        _speedProfiles.StepFaster();

        Assert.Single(camera.Runs);
        axis.InputValue = 0.8f;
        Assert.Equal(1, camera.Runs.Last().Sign);
        Assert.Equal(0.4, camera.Runs.Last().Speed, 6);
    }

    // A profile never turns a move into a stop: the smallest deflection at Max speed 1 % on "Fine" still
    // moves, at the camera's slowest speed (VISCA ranges: pan 1..24, tilt 1..20, zoom and focus 0..7).
    [Theory]
    [InlineData("Pan", 1, 24)]
    [InlineData("Tilt", 1, 20)]
    [InlineData("Zoom", 0, 7)]
    [InlineData("Manual focus", 0, 7)]
    public void ATinyInput_OnTheFinestProfile_StillMovesTheCamera_AtItsSlowestSpeed(string command, int min, int max)
    {
        var camera = new SpeedRecordingCamera();
        _gamepad.SelectedCamera = camera;
        var axis = Axis(command, maxSpeedPercent: 1);

        axis.InputValue = 0.01f;

        var run = camera.Runs.Last();
        Assert.Equal(1, run.Sign);
        Assert.Equal(min, ViscaIpDevice.ToViscaSpeed(run.Speed, (byte)min, (byte)max));
    }

    // A stop is a stop, on any profile: releasing an axis or a button stops the camera.
    [Theory]
    [InlineData("Pan")]
    [InlineData("Tilt")]
    [InlineData("Zoom")]
    [InlineData("Manual focus")]
    public void Releasing_StopsTheCamera_WhicheverProfileIsActive(string command)
    {
        var camera = new SpeedRecordingCamera();
        _gamepad.SelectedCamera = camera;
        var axis = Axis(command, maxSpeedPercent: 50);
        var button = Button(command, speedPercent: 50);

        foreach (var input in new[] { axis, button })
        {
            input.InputValue = 1;
            _speedProfiles.StepFaster();
            input.InputValue = 0;
            Assert.Equal((0.0, 0), camera.Runs.Last());

            _speedProfiles.StepFiner();
            input.InputValue = 1;
            input.InputValue = 0;
            Assert.Equal((0.0, 0), camera.Runs.Last());
        }
    }

    // The controller view's axis readout stays the input's own fraction (#43), before the profile:
    // the active profile is shown in the speed profiles section instead.
    [Fact]
    public void TheAxisReadout_ShowsTheInputsFraction_NotTheProfiles()
    {
        var camera = new SpeedRecordingCamera();
        _gamepad.SelectedCamera = camera;
        var axis = Axis("Pan", maxSpeedPercent: 100);

        axis.InputValue = 1;

        Assert.Equal((Direction.High, 100), (axis.CurrentDirection, axis.CurrentValue));
        Assert.Equal(0.10, camera.Runs.Last().Speed, 9);
    }

    [Fact]
    public void PresetRecallSpeed_IsTheSame_OnEveryProfile()
    {
        var camera = new RecallSpeedRecordingCamera();
        _gamepad.SelectedCamera = camera;
        var button = new Input("button-1", "Button 1", InputType.Button, _gamepad.Commands);
        button.SelectedCommand = AnyCommand("Preset Recall Speed");
        button.CommandValue = button.Values!.Single(v => v.Value == 12);

        foreach (var position in new[] { 1, 2 })
        {
            _speedProfiles.SetActive(position);
            button.InputValue = 1;
            button.InputValue = 0;
        }

        Assert.Equal(new byte[] { 12, 12 }, camera.RecallSpeeds);
    }

    /// <summary>A camera that records the preset recall speeds it is sent.</summary>
    private sealed class RecallSpeedRecordingCamera : ICamera
    {
        public List<byte> RecallSpeeds { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public string Name => "Recall speed recorder";
        public int? CompanionNumber { get; set; }
        public bool Connected => true;
        public bool IsMoving => false;

        public void PresetRecallSpeed(byte speed) => RecallSpeeds.Add(speed);
        public void Pan(double panSpeed, PanDir panDir) { }
        public void Tilt(double tiltSpeed, TiltDir tiltDir) { }
        public void Zoom(double zoomSpeed, ZoomDir zoomDir) { }
        public void Focus(double focusSpeed, FocusDir focusDir) { }
        public void Power(Power power) { }
        public void FocusMode(FocusMode focusMode) { }
        public void FocusLock(FocusLock focusLock) { }
        public void Preset(Preset preset, byte presetNumber) { }
        public void Stop() { }
    }
}
