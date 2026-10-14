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
    private IDynamicCommand Command(string commandName)
    {
        var commands = new CommandsService(new NullCompanionService(), _speedProfiles).GetCommandsForGamepad(_gamepad).ToList();
        _gamepad.CommandList.AddRange(commands);
        return (IDynamicCommand)commands.Single(c => c.CommandName == commandName);
    }

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

    // At full deflection with Max speed 50 %, the input asks for 0.5; "Fine" then scales each axis.
    [Theory]
    [InlineData("Pan", 0.05)]
    [InlineData("Tilt", 0.10)]
    [InlineData("Zoom", 0.15)]
    [InlineData("Manual focus", 0.20)]
    public void EveryCamera_GetsTheAxisFraction_ScaledByTheActiveProfilesPercentageForThatAxis(string command, double expected)
    {
        var axis = Axis(command, maxSpeedPercent: 50);

        foreach (var camera in new[] { new SpeedRecordingCamera(), new SpeedRecordingCamera() })
        {
            _gamepad.SelectedCamera = camera;
            axis.InputValue = 1;

            Assert.Equal(1, camera.Runs.Last().Sign);
            Assert.Equal(expected, camera.Runs.Last().Speed, 9);
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
}
