using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Services;

public class CompositeGamepadsServiceTests
{
    private readonly InputQueue _queue = new();
    private readonly FakeGamepadsService _usb;
    private readonly FakeGamepadsService _panel;

    public CompositeGamepadsServiceTests()
    {
        _usb = new("usb", _queue);
        _panel = new("panel", _queue);
    }

    [Fact]
    public void Gamepads_ListsEveryServicesControllers_InServiceOrder()
    {
        var joystick = _usb.AddInfo("joystick");
        var panel = _panel.AddInfo("skaarhoj:1");

        var composite = new CompositeGamepadsService(_queue, _usb, _panel);

        Assert.Equal(new IGamepadInfo[] { joystick, panel }, composite.Gamepads);
    }

    [Fact]
    public void Gamepads_FollowsAddsAndRemoves_WithoutResetting()
    {
        var panel = _panel.AddInfo("skaarhoj:1");
        var composite = new CompositeGamepadsService(_queue, _usb, _panel);
        var actions = new List<NotifyCollectionChangedAction>();
        composite.Gamepads.CollectionChanged += (_, e) => actions.Add(e.Action);

        var joystick = _usb.AddInfo("joystick");
        Assert.Equal(new IGamepadInfo[] { joystick, panel }, composite.Gamepads);

        _usb.RemoveInfo(joystick);
        Assert.Equal(new IGamepadInfo[] { panel }, composite.Gamepads);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void Activate_GoesToTheOwningService_AndTheGamepadShowsInActiveGamepads()
    {
        var joystick = _usb.AddInfo("joystick");
        var panel = _panel.AddInfo("skaarhoj:1");
        var composite = new CompositeGamepadsService(_queue, _usb, _panel);

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
        var composite = new CompositeGamepadsService(_queue, _usb, _panel);
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

        _ = new CompositeGamepadsService(_queue, _usb, _panel);

        Assert.True(joystick.IsActivated);
        Assert.False(panel.IsActivated);
        Assert.Equal(new[] { "deactivate skaarhoj:1" }, _panel.Log);
    }

    [Fact]
    public void ASavedActiveController_PluggedInWhileAnotherIsActive_StaysOff()
    {
        var panel = _panel.AddInfo("skaarhoj:1");
        var composite = new CompositeGamepadsService(_queue, _usb, _panel);
        composite.ActivateGamepad(panel);
        var joystick = _usb.AddInfo("joystick", activated: true);

        _usb.PlugInSavedActive(joystick);

        Assert.False(joystick.IsActivated);
        Assert.Empty(_usb.Log);
        Assert.True(panel.IsActivated);
        Assert.Equal("skaarhoj:1", Assert.Single(composite.ActiveGamepads).Id);
    }

    [Fact]
    public void ASavedActiveController_PluggedInWhileNoOtherIsActive_IsActivated()
    {
        _panel.AddInfo("skaarhoj:1");
        var composite = new CompositeGamepadsService(_queue, _usb, _panel);
        var joystick = _usb.AddInfo("joystick", activated: true);

        _usb.PlugInSavedActive(joystick);

        Assert.True(joystick.IsActivated);
        Assert.Equal("joystick", Assert.Single(composite.ActiveGamepads).Id);
    }

    [Fact]
    public void AnUnknownController_IsIgnored()
    {
        _usb.AddInfo("joystick");
        var composite = new CompositeGamepadsService(_queue, _usb, _panel);

        composite.ActivateGamepad(new FakeGamepadsService.Info("stray"));

        Assert.Empty(_usb.Log);
        Assert.Empty(composite.ActiveGamepads);
    }

    /// <summary>
    /// A controller source that records Activate/Deactivate and keeps its own typed collections,
    /// changing them under the shared controllers lock as the real sources do.
    /// </summary>
    internal sealed class FakeGamepadsService : IGamepadsService
    {
        private readonly string _name;
        private readonly InputQueue _inputQueue;
        private readonly object _sync;

        public FakeGamepadsService(string name, InputQueue inputQueue)
        {
            _name = name;
            _inputQueue = inputQueue;
            _sync = inputQueue.ControllersLock;
        }

        public ObservableCollection<IGamepadInfo> Gamepads { get; } = new();
        public ObservableCollection<IGamepad> ActiveGamepads { get; } = new();
        public List<string> Log { get; } = new();

        public Info AddInfo(string id, bool activated = false)
        {
            var info = new Info(id) { IsActivated = activated };
            lock (_sync)
                Gamepads.Add(info);
            return info;
        }

        // As the real sources do when a controller saved as active is plugged in or comes back.
        public void PlugInSavedActive(Info info)
        {
            lock (_sync)
            {
                if (_inputQueue.MayActivate(info))
                    ActivateGamepad(info);
                else
                    info.IsActivated = false;
            }
        }

        public void RemoveInfo(IGamepadInfo info)
        {
            lock (_sync)
                Gamepads.Remove(info);
        }

        public void ActivateGamepad(IGamepadInfo gamepad)
        {
            lock (_sync)
            {
                if (!Gamepads.Contains(gamepad))
                    throw new InvalidOperationException($"{_name} does not own {gamepad.Id}");
                Log.Add($"activate {gamepad.Id}");
                gamepad.IsActivated = true;
                ActiveGamepads.Add(new NamedGamepad(gamepad.Id));
            }
        }

        public void DeactivateGamepad(IGamepadInfo gamepad)
        {
            lock (_sync)
            {
                if (!Gamepads.Contains(gamepad))
                    throw new InvalidOperationException($"{_name} does not own {gamepad.Id}");
                Log.Add($"deactivate {gamepad.Id}");
                gamepad.IsActivated = false;
                foreach (var active in ActiveGamepads.Where(g => g.Id == gamepad.Id).ToList())
                    ActiveGamepads.Remove(active);
            }
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
            public ObservableCollection<ICamera>? Cameras { get; set; }
            public IReadOnlyList<Core.Commands.ICommand> Commands => Array.Empty<Core.Commands.ICommand>();
            public bool DetectInput { get; set; }
            public IEnumerable<IInput> Inputs => Array.Empty<IInput>();
            public ICamera? SelectedCamera { get; set; }
            public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
            public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }
            public void Acquire() { }
            public void Unacquire() { }
            public void Update() { }
        }
    }
}
