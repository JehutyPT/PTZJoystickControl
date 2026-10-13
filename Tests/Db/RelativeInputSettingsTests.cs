using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Tests.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Db;

// A relative input's mapping (both commands, direction and value) is saved and loaded like any other input's.
public sealed class RelativeInputSettingsTests : IDisposable
{
    private readonly string _dir;

    public RelativeInputSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PtzRelativeInputSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static (FakeGamepad Gamepad, Input Input) GamepadWithEncoder()
    {
        var gamepad = new FakeGamepad();
        gamepad.CommandList.AddRange(new ICommand[]
        {
            new RecordingStaticCommand(gamepad),
            new OtherRecordingStaticCommand(gamepad),
            new RecordingDynamicCommand(gamepad),
        });
        var input = new Input("enc-1", "Encoder 1", InputType.Relative, gamepad.Commands);
        gamepad.InputList.Add(input);
        return (gamepad, input);
    }

    [Fact]
    public void RelativeInputMapping_SavesAndLoads()
    {
        var path = Path.Combine(_dir, "Gamepads.json");
        var (saved, savedInput) = GamepadWithEncoder();
        savedInput.SelectedCommand = saved.Commands.OfType<OtherRecordingStaticCommand>().Single();
        savedInput.CommandDirection = savedInput.Directions!.Single(d => d.Direction == Direction.Low);
        savedInput.CommandValue = savedInput.Values!.Single(v => v.Value == 2);
        savedInput.SecondInput!.SelectedCommand = saved.Commands.Single(c => c.GetType() == typeof(RecordingStaticCommand));
        savedInput.SecondInput.CommandValue = savedInput.SecondInput.Values!.Single(v => v.Value == 1);

        Assert.True(new GamepadSettingsStore(path).SaveGamepadSettings(new GamepadSettings(saved)));

        var (loaded, loadedInput) = GamepadWithEncoder();
        GamepadInputSetup.Configure(loaded, new GamepadSettingsStore(path).GetGamepadSettingsById(loaded.Id));

        Assert.IsType<OtherRecordingStaticCommand>(loadedInput.SelectedCommand);
        Assert.Equal(Direction.Low, loadedInput.CommandDirection!.Direction);
        Assert.Equal(2, loadedInput.CommandValue!.Value);
        Assert.IsType<RecordingStaticCommand>(loadedInput.SecondInput!.SelectedCommand);
        Assert.Equal(Direction.High, loadedInput.SecondInput.CommandDirection!.Direction);
        Assert.Equal(1, loadedInput.SecondInput.CommandValue!.Value);

        // The loaded mapping runs: anticlockwise steps run the first command, clockwise the second.
        loadedInput.Step(-1);
        loadedInput.Step(2);
        Assert.Equal(new[] { 2 }, loaded.Commands.OfType<OtherRecordingStaticCommand>().Single().Runs);
        Assert.Equal(new[] { 1, 1 }, ((RecordingStaticCommand)loaded.Commands.Single(c => c.GetType() == typeof(RecordingStaticCommand))).Runs);
    }
}
