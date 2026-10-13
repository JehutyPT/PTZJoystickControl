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
    private const string ButtonIdFormat = "Button {0}";

    [Fact]
    public void WithoutSavedSettings_AddsOneVirtualButtonPerCompanionEvent()
    {
        var gamepad = new TestGamepad(physicalButtons: 3);

        GamepadInputSetup.Configure(gamepad, gamepad.AddInput, ButtonIdFormat, BitfocusCompanionEvent.SupportedEvents, settings: null);

        var virtualButtons = gamepad.Inputs.Skip(3).ToList();
        Assert.Equal(new[] { "Vmix Cam 1 Preview", "Vmix Cam 2 Preview" }, virtualButtons.Select(i => i.Name));
        Assert.All(virtualButtons, i => Assert.Equal(InputType.Button, i.InputType));
        // Numbered after the 3 physical buttons, and the same number a Companion press sends.
        Assert.Equal(new[] { "Button 4", "Button 5" }, virtualButtons.Select(i => i.Id));
        Assert.Equal(new[] { 4, 5 }, BitfocusCompanionEvent.SupportedEvents.Select(e => e.Button));
    }

    [Fact]
    public void WithSavedSettings_AddsVirtualButtonsAndAppliesStoredMappingsToThem()
    {
        var discovery = new TestGamepad(physicalButtons: 2);
        GamepadInputSetup.Configure(discovery, discovery.AddInput, ButtonIdFormat, BitfocusCompanionEvent.SupportedEvents, settings: null);
        var virtualId = discovery.Inputs.Single(i => i.Name == "Vmix Cam 2 Preview").Id;
        var physicalId = discovery.Inputs.First().Id;

        var gamepad = new TestGamepad(physicalButtons: 2);
        var settings = new GamepadSettings
        {
            Id = gamepad.Id,
            Name = gamepad.Name,
            Inputs = new List<InputSettings>
            {
                new() { Id = virtualId, CommandType = typeof(SelectCommand).ToString(), Inverted = true },
                new() { Id = physicalId, CommandType = typeof(OtherCommand).ToString() },
            },
        };

        GamepadInputSetup.Configure(gamepad, gamepad.AddInput, ButtonIdFormat, BitfocusCompanionEvent.SupportedEvents, settings);

        var virtualButton = gamepad.Inputs.Single(i => i.Id == virtualId);
        Assert.Equal("Vmix Cam 2 Preview", virtualButton.Name);
        Assert.IsType<SelectCommand>(virtualButton.SelectedCommand);
        Assert.True(virtualButton.Inverted);
        Assert.IsType<OtherCommand>(gamepad.Inputs.Single(i => i.Id == physicalId).SelectedCommand);
        Assert.Null(gamepad.Inputs.Single(i => i.Name == "Vmix Cam 1 Preview").SelectedCommand);
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

        GamepadInputSetup.Configure(gamepad, gamepad.AddInput, ButtonIdFormat, BitfocusCompanionEvent.SupportedEvents, settings);

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

        public void AddInput(string id, IInput input) => _inputs.Add(input);

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
