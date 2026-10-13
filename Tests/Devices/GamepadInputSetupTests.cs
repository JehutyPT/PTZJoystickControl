using System.Collections.ObjectModel;
using System.ComponentModel;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

public class GamepadInputSetupTests
{
    [Fact]
    public void WithoutSavedSettings_AddsNoInputs()
    {
        var gamepad = new TestGamepad(physicalButtons: 3);

        GamepadInputSetup.Configure(gamepad, settings: null);

        // No Companion virtual buttons any more: Companion selects cameras directly.
        Assert.Equal(new[] { "Button 1", "Button 2", "Button 3" }, gamepad.Inputs.Select(i => i.Id));
    }

    [Fact]
    public void StoredMappingsOfTheOldCompanionVirtualButtons_AreIgnored_AndPhysicalMappingsStillApply()
    {
        var gamepad = new TestGamepad(physicalButtons: 2);
        var settings = new GamepadSettings
        {
            Id = gamepad.Id,
            Name = gamepad.Name,
            Inputs = new List<InputSettings>
            {
                // The ids the virtual buttons had on a 2-button gamepad: numbered after the physical buttons.
                new() { Id = "Button 3", CommandType = typeof(SelectCommand).ToString(), CommandValue = new CommandValueOption("Cam 1", 0) },
                new() { Id = "Button 4", CommandType = typeof(SelectCommand).ToString(), CommandValue = new CommandValueOption("Cam 2", 1) },
                new() { Id = "Button 1", CommandType = typeof(OtherCommand).ToString() },
            },
        };

        var thrown = Record.Exception(() => GamepadInputSetup.Configure(gamepad, settings));

        Assert.Null(thrown);
        Assert.Equal(new[] { "Button 1", "Button 2" }, gamepad.Inputs.Select(i => i.Id));
        Assert.IsType<OtherCommand>(gamepad.Inputs.Single(i => i.Id == "Button 1").SelectedCommand);
        Assert.Null(gamepad.Inputs.Single(i => i.Id == "Button 2").SelectedCommand);
        // The next save only holds the inputs the gamepad has.
        Assert.Equal(new[] { "Button 1", "Button 2" }, new GamepadSettings(gamepad).Inputs!.Select(i => i.Id));
    }

    [Fact]
    public void StoredMappingForAnUnknownCommandType_LeavesTheInputUnmapped()
    {
        var gamepad = new TestGamepad(physicalButtons: 1);
        var settings = new GamepadSettings
        {
            Id = gamepad.Id,
            Name = gamepad.Name,
            Inputs = new List<InputSettings> { new() { Id = gamepad.Inputs.First().Id, CommandType = "No.Such.Command" } },
        };

        GamepadInputSetup.Configure(gamepad, settings);

        Assert.Null(gamepad.Inputs.First().SelectedCommand);
    }

    private sealed class SelectCommand : FakeCommand { }

    private sealed class OtherCommand : FakeCommand { }

    private abstract class FakeCommand : ICommand
    {
        public string CommandName => GetType().Name;
        public string AxisParameterName => string.Empty;
        public string ButtonParameterName => string.Empty;
        public IEnumerable<CommandValueOption> Options => Array.Empty<CommandValueOption>();
        public void Execute(int value) { }
        public void Execute(CommandValueOption value) { }
    }

    private sealed class TestGamepad : IGamepad
    {
        private readonly List<IInput> _inputs = new();
        private readonly List<ICommand> _commands = new() { new SelectCommand(), new OtherCommand() };

        public TestGamepad(int physicalButtons)
        {
            for (var i = 0; i < physicalButtons; i++)
                _inputs.Add(new Input($"Button {i + 1}", $"Button {i}", InputType.Button, _commands));
        }

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public string Id => "test-gamepad";
        public string Name => "Test gamepad";
        public bool IsActivated { get; set; } = true;
        public bool IsConnected { get; set; } = true;
        public ObservableCollection<ViscaDeviceBase>? Cameras { get; set; }
        public IReadOnlyList<ICommand> Commands => _commands;
        public bool DetectInput { get; set; }
        public IEnumerable<IInput> Inputs => _inputs;
        public ViscaDeviceBase? SelectedCamera { get; set; }

        public void Acquire() { }
        public void Unacquire() { }
        public void Update() { }
    }
}
