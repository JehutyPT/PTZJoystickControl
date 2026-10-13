using System.Collections.ObjectModel;
using System.ComponentModel;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Tests.Devices;

/// <summary>A static command that records each value it runs with.</summary>
internal class RecordingStaticCommand : IStaticCommand
{
    public RecordingStaticCommand(IGamepad gamepad) : base(gamepad, new NullCompanionService()) { }

    public List<int> Runs { get; } = new();

    public override string CommandName => GetType().Name;
    public override string AxisParameterName => "Value";
    public override string ButtonParameterName => "Value";
    public override IEnumerable<CommandValueOption> Options { get; } =
        new[] { new CommandValueOption("One", 1), new CommandValueOption("Two", 2) };

    public override void Execute(int value) => Runs.Add(value);
}

/// <summary>A second static command type, so settings can tell the two apart by type name.</summary>
internal sealed class OtherRecordingStaticCommand : RecordingStaticCommand
{
    public OtherRecordingStaticCommand(IGamepad gamepad) : base(gamepad) { }
}

/// <summary>A dynamic command that records every call; a relative input must never run it.</summary>
internal sealed class RecordingDynamicCommand : IDynamicCommand
{
    public RecordingDynamicCommand(IGamepad gamepad) : base(gamepad, new NullCompanionService()) { }

    public int Runs { get; private set; }

    public override string CommandName => GetType().Name;
    public override string AxisParameterName => "Max speed";
    public override string ButtonParameterName => "Speed";
    public override int MaxValue => 7;
    public override int MinValue => 0;
    public override IEnumerable<CommandDirectionOption> ButtonDirections { get; } = new[]
    {
        new CommandDirectionOption("In", Direction.High),
        new CommandDirectionOption("Out", Direction.Low),
    };

    public override void Execute(int value, Direction direction) => Runs++;
}

internal sealed class NullCompanionService : IBitfocusCompanionService
{
    public event EventHandler<BitfocusCompanionEvent>? ReceivedBitfocusCommand { add { } remove { } }
    public void PressButton(int? pageNumber, int? buttonNumber) { }
    public void SetCustomVariable(string variableName, string value) { }
    public string? InboundListenerError => null;
    public event EventHandler? InboundListenerErrorChanged { add { } remove { } }
}

/// <summary>A gamepad whose inputs and commands are set up by the test.</summary>
internal sealed class FakeGamepad : IGamepad
{
    public List<IInput> InputList { get; } = new();
    public List<ICommand> CommandList { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

    public string Id => "fake-gamepad";
    public string Name => "Fake gamepad";
    public bool IsActivated { get; set; } = true;
    public bool IsConnected { get; set; } = true;
    public ObservableCollection<ViscaDeviceBase>? Cameras { get; set; }
    public IReadOnlyList<ICommand> Commands => CommandList;
    public bool DetectInput { get; set; }
    public IEnumerable<IInput> Inputs => InputList;
    public ViscaDeviceBase? SelectedCamera { get; set; }

    public void Acquire() { }
    public void Unacquire() { }
    public void Update() { }
}
