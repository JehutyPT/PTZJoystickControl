using System.Collections.Concurrent;
using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// A relative input (an encoder) sends signed step counts. Mapped to a static command, it runs that
// command once per step in its direction, and a second command once per step in the other one.
public sealed class RelativeInputTests : IDisposable
{
    private readonly FakeGamepad _gamepad = new();
    private readonly RecordingStaticCommand _first;
    private readonly OtherRecordingStaticCommand _second;
    private readonly RecordingDynamicCommand _dynamic;
    private readonly RecordingTraceListener _trace = new();

    public RelativeInputTests()
    {
        _first = new RecordingStaticCommand(_gamepad);
        _second = new OtherRecordingStaticCommand(_gamepad);
        _dynamic = new RecordingDynamicCommand(_gamepad);
        _gamepad.CommandList.AddRange(new ICommand[] { _first, _second, _dynamic });
        Trace.Listeners.Add(_trace);
    }

    public void Dispose() => Trace.Listeners.Remove(_trace);

    private Input RelativeInput()
    {
        var input = new Input("enc-1", "Encoder 1", InputType.Relative, _gamepad.Commands);
        _gamepad.InputList.Add(input);
        return input;
    }

    private Input MappedInput(Direction direction)
    {
        var input = RelativeInput();
        input.SelectedCommand = _first;
        input.CommandDirection = input.Directions!.Single(d => d.Direction == direction);
        input.CommandValue = input.Values!.Single(v => v.Value == 2);
        input.SecondInput!.SelectedCommand = _second;
        input.SecondInput.CommandValue = input.SecondInput.Values!.Single(v => v.Value == 1);
        return input;
    }

    [Fact]
    public void ThreeClockwiseSteps_RunTheHighCommandThreeTimes()
    {
        var input = MappedInput(Direction.High);

        input.Step(3);

        Assert.Equal(new[] { 2, 2, 2 }, _first.Runs);
        Assert.Empty(_second.Runs);
    }

    [Fact]
    public void TwoAnticlockwiseSteps_RunTheSecondCommandTwice_AndTheFirstNever()
    {
        var input = MappedInput(Direction.High);

        input.Step(-2);

        Assert.Empty(_first.Runs);
        Assert.Equal(new[] { 1, 1 }, _second.Runs);
    }

    [Fact]
    public void WithDirectionLow_AnticlockwiseStepsRunTheFirstCommand_AndClockwiseStepsTheSecond()
    {
        var input = MappedInput(Direction.Low);

        input.Step(-2);
        input.Step(1);

        Assert.Equal(new[] { 2, 2 }, _first.Runs);
        Assert.Equal(new[] { 1 }, _second.Runs);
    }

    [Fact]
    public void ZeroSteps_RunNothing()
    {
        var input = MappedInput(Direction.High);

        input.Step(0);

        Assert.Empty(_first.Runs);
        Assert.Empty(_second.Runs);
    }

    [Fact]
    public void EachEventRunsItsSteps_NothingStaysPressed()
    {
        var input = MappedInput(Direction.High);

        input.Step(1);
        input.Step(1);
        input.Step(1);

        Assert.Equal(3, _first.Runs.Count);
        Assert.Equal(Direction.Stop, input.CurrentDirection);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(1_000_000)]
    [InlineData(-1_000_000)]
    public void ExtremeStepCounts_DoNotThrow_AndAreCappedPerEvent(int steps)
    {
        var input = MappedInput(Direction.High);

        var thrown = Record.Exception(() => input.Step(steps));

        Assert.Null(thrown);
        Assert.Equal(Input.MaxStepsPerEvent, _first.Runs.Count + _second.Runs.Count);
        Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains("enc-1"));
    }

    [Fact]
    public void StepsWithoutACommand_DoNothing()
    {
        var input = RelativeInput();

        var thrown = Record.Exception(() => { input.Step(5); input.Step(-5); input.Step(0); });

        Assert.Null(thrown);
        Assert.Empty(_first.Runs);
    }

    [Fact]
    public void CommandList_OffersNoDynamicCommand()
    {
        var input = RelativeInput();

        Assert.DoesNotContain(input.Commands, c => c is IDynamicCommand);
        Assert.Contains(_first, input.Commands);
        Assert.DoesNotContain(input.SecondInput!.Commands!, c => c is IDynamicCommand);
    }

    [Fact]
    public void SelectingADynamicCommand_LeavesTheInputUnmapped_AndLogs()
    {
        var input = RelativeInput();

        input.SelectedCommand = _dynamic;
        input.Step(3);

        Assert.Null(input.SelectedCommand);
        Assert.Equal(0, _dynamic.Runs);
        Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains("enc-1"));
    }

    [Fact]
    public void SavedSettingsBindingADynamicCommand_LeaveTheInputUnmapped_AndLog()
    {
        var input = RelativeInput();
        var settings = new GamepadSettings
        {
            Id = _gamepad.Id,
            Name = _gamepad.Name,
            Inputs = new List<InputSettings>
            {
                new()
                {
                    Id = "enc-1",
                    CommandType = typeof(RecordingDynamicCommand).ToString(),
                    CommandDirection = new CommandDirectionOption("In", Direction.High),
                    CommandValue = new CommandValueOption("7", 7),
                    SecondInputSettings = new() { CommandType = typeof(RecordingDynamicCommand).ToString() },
                },
            },
        };

        var thrown = Record.Exception(() => GamepadInputSetup.Configure(_gamepad, settings));
        input.Step(2);
        input.Step(-2);

        Assert.Null(thrown);
        Assert.Null(input.SelectedCommand);
        Assert.Null(input.SecondInput!.SelectedCommand);
        Assert.Equal(0, _dynamic.Runs);
        Assert.Contains(_trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains("enc-1"));
    }

    [Fact]
    public void InputValue_OnARelativeInput_RunsNothing()
    {
        var input = MappedInput(Direction.High);

        input.InputValue = 1;
        input.InputValue = -1;

        Assert.Empty(_first.Runs);
        Assert.Empty(_second.Runs);
    }

    [Fact]
    public void AxisAndButtonInputs_StillOfferDynamicCommands()
    {
        var axis = new Input("axis", "X", InputType.Axis, _gamepad.Commands);
        var button = new Input("button", "B", InputType.Button, _gamepad.Commands);

        Assert.Contains(_dynamic, axis.Commands);
        Assert.Contains(_dynamic, button.Commands);
        Assert.Equal(InputType.Axis, axis.SecondInput!.InputType);
        Assert.Null(button.SecondInput);
    }

    // The controller saves its mappings when an input raises PersistentPropertyChanged: a change to
    // the second command alone must raise it too, or it is lost on restart.
    [Theory]
    [InlineData(InputType.Relative)]
    [InlineData(InputType.Axis)]
    public void ChangingTheSecondCommand_IsAPersistentChangeOfTheInput(InputType inputType)
    {
        var input = new Input("in-1", "Input 1", inputType, _gamepad.Commands);
        input.SelectedCommand = _first;
        var changed = new List<string?>();
        input.PersistentPropertyChanged += (_, e) => changed.Add(e.PropertyName);

        input.SecondInput!.SelectedCommand = _second;
        Assert.Contains(nameof(Input.SelectedCommand), changed);

        changed.Clear();
        input.SecondInput.CommandValue = input.SecondInput.Values!.Single(v => v.Value == 1);
        Assert.Contains(nameof(Input.CommandValue), changed);
    }

    [Fact]
    public void ExistingInputTypeValues_AreUnchanged()
    {
        Assert.Equal(0, (int)InputType.Axis);
        Assert.Equal(1, (int)InputType.Button);
        Assert.Equal(2, (int)InputType.Unsupported);
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<(TraceEventType Type, string Message)> _events = new();

        public IEnumerable<(TraceEventType Type, string Message)> Events => _events;

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
            _events.Enqueue((eventType, message ?? string.Empty));

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
            _events.Enqueue((eventType, args == null ? format ?? string.Empty : string.Format(format ?? string.Empty, args)));

        public override void Write(string? message) { }

        public override void WriteLine(string? message) { }
    }
}
