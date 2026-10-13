using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Services;

public class CompositeGamepadsServiceTests
{
    private readonly FakeGamepadsService _usb = new("usb");
    private readonly FakeGamepadsService _panel = new("panel");

    [Fact]
    public void Gamepads_ListsEveryServicesControllers_InServiceOrder()
    {
        var joystick = _usb.AddInfo("joystick");
        var panel = _panel.AddInfo("skaarhoj:1");

        var composite = new CompositeGamepadsService(_usb, _panel);

        Assert.Equal(new IGamepadInfo[] { joystick, panel }, composite.Gamepads);
    }

    [Fact]
    public void Gamepads_FollowsAddsAndRemoves_WithoutResetting()
    {
        var panel = _panel.AddInfo("skaarhoj:1");
        var composite = new CompositeGamepadsService(_usb, _panel);
        var actions = new List<NotifyCollectionChangedAction>();
        composite.Gamepads.CollectionChanged += (_, e) => actions.Add(e.Action);

        var joystick = _usb.AddInfo("joystick");
        Assert.Equal(new IGamepadInfo[] { joystick, panel }, composite.Gamepads);

        _usb.Gamepads.Remove(joystick);
        Assert.Equal(new IGamepadInfo[] { panel }, composite.Gamepads);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void Activate_GoesToTheOwningService_AndTheGamepadShowsInActiveGamepads()
    {
        var joystick = _usb.AddInfo("joystick");
        var panel = _panel.AddInfo("skaarhoj:1");
        var composite = new CompositeGamepadsService(_usb, _panel);

        composite.ActivateGamepad(panel);

        Assert.Equal(new[] { "activate skaarhoj:1" }, _panel.Log);
        Assert.Empty(_usb.Log);
        Assert.Equal("skaarhoj:1", Assert.Single(composite.ActiveGamepads).Id);

        composite.DeactivateGamepad(panel);
        Assert.Equal(new[] { "activate skaarhoj:1", "deactivate skaarhoj:1" }, _panel.Log);
        Assert.Empty(composite.ActiveGamepads);
        Assert.False(joystick.IsActivated);
    }

    [Fact]
    public void Activate_DeactivatesTheOtherServicesController_First()
    {
        var joystick = _usb.AddInfo("joystick");
        var panel = _panel.AddInfo("skaarhoj:1");
        var composite = new CompositeGamepadsService(_usb, _panel);
        composite.ActivateGamepad(joystick);

        composite.ActivateGamepad(panel);

        Assert.Equal(new[] { "activate joystick", "deactivate joystick" }, _usb.Log);
        Assert.Equal(new[] { "activate skaarhoj:1" }, _panel.Log);
        Assert.False(joystick.IsActivated);
        Assert.Equal("skaarhoj:1", Assert.Single(composite.ActiveGamepads).Id);
    }

    [Fact]
    public void AtStartup_TwoActivatedControllers_KeepOnlyTheFirst()
    {
        // e.g. the USB joystick was deactivated while unplugged by an older build, so Gamepads.json still says activated.
        var joystick = _usb.AddInfo("joystick", activated: true);
        var panel = _panel.AddInfo("skaarhoj:1", activated: true);

        _ = new CompositeGamepadsService(_usb, _panel);

        Assert.True(joystick.IsActivated);
        Assert.False(panel.IsActivated);
        Assert.Equal(new[] { "deactivate skaarhoj:1" }, _panel.Log);
    }

    [Fact]
    public void AnUnknownController_IsIgnored()
    {
        _usb.AddInfo("joystick");
        var composite = new CompositeGamepadsService(_usb, _panel);

        composite.ActivateGamepad(new FakeGamepadsService.Info("stray"));

        Assert.Empty(_usb.Log);
        Assert.Empty(composite.ActiveGamepads);
    }

    /// <summary>A controller source that records Activate/Deactivate and keeps its own typed collections.</summary>
    internal sealed class FakeGamepadsService : IGamepadsService
    {
        private readonly string _name;

        public FakeGamepadsService(string name) => _name = name;

        public ObservableCollection<IGamepadInfo> Gamepads { get; } = new();
        public ObservableCollection<IGamepad> ActiveGamepads { get; } = new();
        public List<string> Log { get; } = new();

        public Info AddInfo(string id, bool activated = false)
        {
            var info = new Info(id) { IsActivated = activated };
            Gamepads.Add(info);
            return info;
        }

        public void ActivateGamepad(IGamepadInfo gamepad)
        {
            if (!Gamepads.Contains(gamepad))
                throw new InvalidOperationException($"{_name} does not own {gamepad.Id}");
            Log.Add($"activate {gamepad.Id}");
            gamepad.IsActivated = true;
            ActiveGamepads.Add(new NamedGamepad(gamepad.Id));
        }

        public void DeactivateGamepad(IGamepadInfo gamepad)
        {
            if (!Gamepads.Contains(gamepad))
                throw new InvalidOperationException($"{_name} does not own {gamepad.Id}");
            Log.Add($"deactivate {gamepad.Id}");
            gamepad.IsActivated = false;
            foreach (var active in ActiveGamepads.Where(g => g.Id == gamepad.Id).ToList())
                ActiveGamepads.Remove(active);
        }

        internal sealed class Info : IGamepadInfo
        {
            public Info(string id) => Id = id;
            public string Id { get; }
            public string Name => Id;
            public bool IsActivated { get; set; }
            public bool IsConnected { get; set; } = true;
            public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        }

        private sealed class NamedGamepad : IGamepad
        {
            public NamedGamepad(string id) => Id = id;
            public string Id { get; }
            public string Name => Id;
            public bool IsActivated { get; set; } = true;
            public bool IsConnected { get; set; } = true;
            public ObservableCollection<ViscaDeviceBase>? Cameras { get; set; }
            public IReadOnlyList<Core.Commands.ICommand> Commands => Array.Empty<Core.Commands.ICommand>();
            public bool DetectInput { get; set; }
            public IEnumerable<IInput> Inputs => Array.Empty<IInput>();
            public ViscaDeviceBase? SelectedCamera { get; set; }
            public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
            public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }
            public void Acquire() { }
            public void Unacquire() { }
            public void Update() { }
        }
    }
}
