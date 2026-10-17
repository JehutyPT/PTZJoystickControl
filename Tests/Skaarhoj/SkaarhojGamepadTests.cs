using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.SkaarhojPanel;
using PtzJoystickControl.Tests.Devices;
using PtzJoystickControl.Tests.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Skaarhoj;

public class SkaarhojGamepadTests
{
    private readonly MotionRecordingCamera _camera = new("Cam 1");

    private SkaarhojGamepad NewGamepad()
    {
        var cameras = new FakeCamerasService(_camera);
        var gamepad = new SkaarhojGamepad("skaarhoj:192.0.2.1", "SKAARHOJ panel (192.0.2.1)",
            new CommandsService(new NullCompanionService(), TestSpeedProfiles.Full()), cameras.Cameras);
        gamepad.SelectedCamera = _camera;
        return gamepad;
    }

    private static IInput Input(SkaarhojGamepad gamepad, string name) => gamepad.Inputs.Single(i => i.Name == name);

    private static void Map<T>(SkaarhojGamepad gamepad, string inputName) where T : ICommand =>
        Input(gamepad, inputName).SelectedCommand = gamepad.Commands.OfType<T>().Single();

    // Every axis to pan and the Focus knob to Focus step, so any input that fires shows in the camera's calls.
    private static void MapEveryInput(SkaarhojGamepad gamepad)
    {
        foreach (var axis in gamepad.Inputs.Where(i => i.InputType == InputType.Axis))
            axis.SelectedCommand = gamepad.Commands.OfType<PanCommand>().Single();
        FocusKnob.MapToFocusStep(gamepad);
    }

    [Fact]
    public void ExposesTheFourAxesThenTheFocusKnob_WithStableIdsAndReadableNames()
    {
        var gamepad = NewGamepad();

        Assert.Equal(
            new[]
            {
                ("HWC60", "LR", InputType.Axis),
                ("HWC61", "UD", InputType.Axis),
                ("HWC62", "Rotate", InputType.Axis),
                ("HWC58", "Zoom", InputType.Axis),
                ("HWC59", "Focus", InputType.Relative),
            },
            gamepad.Inputs.Select(i => (i.Id, i.Name, i.InputType)).ToArray());
    }

    [Fact]
    public void FocusKnob_StartsUnmapped()
    {
        var focus = Input(NewGamepad(), "Focus");

        Assert.Null(focus.SelectedCommand);
        Assert.Null(focus.SecondInput!.SelectedCommand);
    }

    [Fact]
    public void HasTheDefaultCommandSet()
    {
        var gamepad = NewGamepad();
        var expected = new CommandsService(new NullCompanionService(), TestSpeedProfiles.Full()).GetCommandsForGamepad(gamepad)
            .Select(c => c.GetType()).ToArray();

        Assert.Equal(expected, gamepad.Commands.Select(c => c.GetType()).ToArray());
    }

    [Fact]
    public void IdIsTheAddressWithAPrefix()
    {
        Assert.Equal("skaarhoj:192.168.1.163", SkaarhojGamepad.IdFor("192.168.1.163"));
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(250, 0.5f)]
    [InlineData(-250, -0.5f)]
    [InlineData(500, 1f)]
    [InlineData(-500, -1f)]
    [InlineData(501, 1f)]
    [InlineData(-501, -1f)]
    [InlineData(int.MaxValue, 1f)]
    [InlineData(int.MinValue, -1f)]
    public void Speed_IsScaledBy500_AndClamped(int speed, float expected)
    {
        Assert.Equal(expected, SkaarhojGamepad.Scale(speed));
    }

    [Theory]
    [InlineData(-501)]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(int.MinValue)]
    public void Speed_OutsideTheDocumentedRange_NeverThrows(int speed)
    {
        var gamepad = NewGamepad();
        Map<PanCommand>(gamepad, "LR");

        var thrown = Record.Exception(() => gamepad.OnSpeed(60, speed));

        Assert.Null(thrown);
        Assert.Equal(Math.Sign(speed), Math.Sign(Input(gamepad, "LR").RawInputValue));
    }

    [Theory]
    [InlineData(60, "LR")]
    [InlineData(61, "UD")]
    [InlineData(62, "Rotate")]
    [InlineData(58, "Zoom")]
    public void Speed_SetsTheMatchingAxis(int hwc, string name)
    {
        var gamepad = NewGamepad();

        gamepad.OnSpeed(hwc, -250);

        Assert.Equal(-0.5f, Input(gamepad, name).RawInputValue);
        Assert.All(gamepad.Inputs.Where(i => i.Name != name), i => Assert.Equal(0f, i.RawInputValue));
    }

    [Theory]
    [InlineData(59)] // the Focus knob: not an axis
    [InlineData(63)]
    [InlineData(57)]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-60)]
    public void Speed_ForAnyOtherHwc_IsIgnored(int hwc)
    {
        var gamepad = NewGamepad();
        MapEveryInput(gamepad);

        gamepad.OnSpeed(hwc, 500);

        Assert.All(gamepad.Inputs, i => Assert.Equal(0f, i.RawInputValue));
        Assert.Empty(_camera.Calls);
    }

    [Theory]
    [InlineData(46)] // Knob A
    [InlineData(53)] // Knob H
    [InlineData(57)] // Iris
    [InlineData(58)] // the axes
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(0)]
    [InlineData(-59)]
    public void Encoder_ForAnyOtherHwc_IsIgnored(int hwc)
    {
        var gamepad = NewGamepad();
        MapEveryInput(gamepad);

        gamepad.OnEncoder(hwc, 1);
        gamepad.OnEncoder(hwc, -2);

        Assert.All(gamepad.Inputs, i => Assert.Equal(0f, i.RawInputValue));
        Assert.Empty(_camera.Calls);
    }

    // The panel can send several steps in one line: a capture of it has Enc:±2 and Enc:3.
    [Theory]
    [InlineData(1, 1, FocusKnob.Far)]
    [InlineData(-1, 1, FocusKnob.Near)]
    [InlineData(2, 2, FocusKnob.Far)]
    [InlineData(-2, 2, FocusKnob.Near)]
    [InlineData(3, 3, FocusKnob.Far)]
    [InlineData(0, 0, FocusKnob.Far)]
    public void Encoder_OnTheFocusKnob_RunsItsCommandOncePerStep(int steps, int expectedCount, string expectedStep)
    {
        var gamepad = NewGamepad();
        FocusKnob.MapToFocusStep(gamepad);

        gamepad.OnEncoder(59, steps);

        Assert.Equal(Enumerable.Repeat(expectedStep, expectedCount), _camera.Calls);
    }

    [Fact]
    public void MappedAxis_DrivesTheSelectedCamera_AndStopsAtZero()
    {
        var gamepad = NewGamepad();
        Map<PanCommand>(gamepad, "LR");

        gamepad.OnSpeed(60, 500);
        gamepad.OnSpeed(60, 0);

        Assert.Equal(new[] { "Pan 1 Right", "Pan 0 Stop" }, _camera.Calls);
    }

    [Fact]
    public void JitterAtFullDeflection_KeepsTheSameFullSpeed()
    {
        // At the limit the rotate axis reports 498–500 for a couple of seconds.
        var gamepad = NewGamepad();
        Map<ZoomCommand>(gamepad, "Rotate");

        foreach (var speed in new[] { 500, 498, 500, 499, 498, 500 })
        {
            gamepad.OnSpeed(62, speed);
            Assert.Equal(1f, Input(gamepad, "Rotate").InputValue);
        }

        Assert.Single(_camera.Calls.Distinct());
        Assert.StartsWith("Zoom 1 ", _camera.Calls[0]);
    }

    [Fact]
    public void FreshGamepad_HasEveryAxisAtZero_AndSendsNothing()
    {
        var gamepad = NewGamepad();
        MapEveryInput(gamepad);

        Assert.All(gamepad.Inputs, i => Assert.Equal(0f, i.InputValue));
        Assert.Empty(_camera.Calls);
    }

    [Fact]
    public void AfterUnacquire_SpeedIsIgnored()
    {
        var gamepad = NewGamepad();
        Map<PanCommand>(gamepad, "LR");

        gamepad.Unacquire();
        gamepad.OnSpeed(60, 500);

        Assert.Empty(_camera.Calls);
    }

    [Fact]
    public void AfterUnacquire_EncoderIsIgnored()
    {
        var gamepad = NewGamepad();
        FocusKnob.MapToFocusStep(gamepad);

        gamepad.Unacquire();
        gamepad.OnEncoder(59, 1);

        Assert.Empty(_camera.Calls);
    }

    [Fact]
    public void ChangingAMapping_RaisesPersistentPropertyChanged()
    {
        var gamepad = NewGamepad();
        var raised = 0;
        gamepad.PersistentPropertyChanged += (_, _) => raised++;

        Map<PanCommand>(gamepad, "LR");

        Assert.True(raised > 0);
    }
}
