using System.Diagnostics;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// Expo bends an axis's response between the deadzone and the saturation zone:
// out = (1 - e)·x + e·x³ of today's linear value x, for dynamic commands only.
public sealed class InputExpoTests
{
    private readonly FakeGamepad _gamepad = new();
    private readonly RecordingSpeedCommand _speed;
    private readonly RecordingStaticCommand _static;

    public InputExpoTests()
    {
        _speed = new RecordingSpeedCommand(_gamepad);
        _static = new RecordingStaticCommand(_gamepad);
        _gamepad.CommandList.AddRange(new ICommand[] { _speed, _static });
    }

    private Input Axis(float expo = 0, float deadZone = 0.05F, float saturation = 0.05F, string? id = null)
    {
        var input = new Input(id ?? "axis-1", "Axis 1", InputType.Axis, _gamepad.Commands)
        {
            DeadZone = deadZone,
            Saturation = saturation,
        };
        input.SelectedCommand = _speed;
        input.Expo = expo;
        return input;
    }

    // Today's shaping (deadzone, saturation, linear map between them, sign and inversion), copied
    // from before expo existed, so "expo 0 is unchanged" is checked against the old behaviour.
    private static float Linear(float raw, float deadZone, float saturation, bool inverted, bool defaultCenter)
    {
        float value = raw;
        if (!defaultCenter) value = Util.Map(value, -1, 1, 0, 1);
        var isNegative = value < 0;
        var absVal = Math.Abs(value);

        if (absVal <= deadZone) value = 0;
        else if (absVal >= 1 - saturation) value = 1;
        else value = Util.Map(absVal, deadZone, 1 - saturation, 0, 1);

        if (isNegative ^ inverted) value = -value;
        return value;
    }

    private static IEnumerable<float> Sweep()
    {
        for (int i = -1000; i <= 1000; i++)
            yield return i / 1000F;
    }

    [Fact]
    public void NewInput_HasExpoZero()
    {
        var input = new Input("axis-1", "Axis 1", InputType.Axis, _gamepad.Commands);

        Assert.Equal(0F, input.Expo);
        Assert.Equal(0F, input.SecondInput!.Expo);
    }

    [Fact]
    public void ExpoZero_ShapesExactlyAsBefore()
    {
        foreach (var deadZone in new[] { 0F, 0.05F, 0.2F })
            foreach (var saturation in new[] { 0F, 0.05F, 0.25F })
                foreach (var inverted in new[] { false, true })
                    foreach (var defaultCenter in new[] { true, false })
                    {
                        var input = Axis(expo: 0, deadZone: deadZone, saturation: saturation);
                        input.Inverted = inverted;
                        input.DefaultCenter = defaultCenter;

                        foreach (var raw in Sweep())
                        {
                            input.InputValue = raw;
                            Assert.Equal(Linear(raw, deadZone, saturation, inverted, defaultCenter), input.InputValue);
                        }
                    }
    }

    [Theory]
    [InlineData(0F)]
    [InlineData(0.5F)]
    [InlineData(1F)]
    public void InsideTheDeadzone_TheCameraIsStopped_ForEveryExpo(float expo)
    {
        var input = Axis(expo: expo, deadZone: 0.1F);

        foreach (var raw in new[] { 0.6F, 0.1F, -0.6F, -0.05F, 0.6F, 0F })
        {
            input.InputValue = raw;
            if (Math.Abs(raw) <= 0.1F)
            {
                Assert.Equal(0F, input.InputValue);
                Assert.Equal(Direction.Stop, _speed.Runs.Last().Direction);
            }
        }
    }

    [Theory]
    [InlineData(0F, 0F)]
    [InlineData(0.3F, 0F)]
    [InlineData(1F, 0F)]
    [InlineData(0.3F, 0.05F)]
    [InlineData(1F, 0.05F)]
    public void FullDeflection_ReachesMaxSpeed_ForEveryExpo(float expo, float saturation)
    {
        var input = Axis(expo: expo, saturation: saturation);

        foreach (var raw in new[] { 1F, 1F - saturation })
        {
            input.InputValue = raw;
            Assert.Equal(1F, input.InputValue);
            Assert.Equal((RecordingSpeedCommand.Max, Direction.High), _speed.Runs.Last());

            input.InputValue = -raw;
            Assert.Equal(-1F, input.InputValue);
            Assert.Equal((RecordingSpeedCommand.Max, Direction.Low), _speed.Runs.Last());
        }
    }

    [Theory]
    [InlineData(0.25F, false)]
    [InlineData(0.5F, false)]
    [InlineData(1F, false)]
    [InlineData(0.5F, true)]
    public void BetweenDeadzoneAndSaturation_FollowsTheCurve_AndStaysMonotonicAndInRange(float expo, bool inverted)
    {
        const float deadZone = 0.05F, saturation = 0.05F;
        var input = Axis(expo: expo, deadZone: deadZone, saturation: saturation);
        input.Inverted = inverted;

        float previous = float.NegativeInfinity;
        foreach (var raw in Sweep())
        {
            input.InputValue = raw;
            var linear = Linear(raw, deadZone, saturation, inverted, defaultCenter: true);
            var x = Math.Abs(linear);
            var expected = (1 - expo) * x + expo * x * x * x;
            var shaped = input.InputValue;

            Assert.False(float.IsNaN(shaped));
            Assert.InRange(shaped, -1F, 1F);
            Assert.True(Math.Abs(expected - Math.Abs(shaped)) < 1e-6, $"raw {raw}: |{shaped}| != {expected}");
            Assert.True(Math.Abs(shaped) <= x, $"raw {raw}: |{shaped}| > linear {x}");
            Assert.Equal(Math.Sign(linear), Math.Sign(shaped));
            if (Math.Abs(raw) > deadZone)
            {
                Assert.NotEqual(0F, shaped);
                Assert.NotEqual(Direction.Stop, _speed.Runs.Last().Direction);
            }

            // Monotonic in the raw deflection (decreasing when inverted).
            var oriented = inverted ? -shaped : shaped;
            Assert.True(oriented >= previous, $"raw {raw}: {oriented} < previous {previous}");
            previous = oriented;
        }
    }

    [Fact]
    public void Expo_GivesASlowerSpeedForASmallDeflection_ButNeverStopsOutsideTheDeadzone()
    {
        var linear = Axis(expo: 0);
        var expo = Axis(expo: 1);

        linear.InputValue = 0.3F;
        expo.InputValue = 0.3F;

        var linearRun = _speed.Runs[^2];
        var expoRun = _speed.Runs[^1];
        Assert.Equal(Direction.High, linearRun.Direction);
        Assert.Equal(Direction.High, expoRun.Direction);
        Assert.True(expoRun.Speed < linearRun.Speed, $"expo speed {expoRun.Speed} not below linear {linearRun.Speed}");

        // Just outside the deadzone, the tiniest curve value still moves the camera.
        expo.InputValue = 0.0501F;
        Assert.Equal(Direction.High, _speed.Runs.Last().Direction);
        Assert.True(_speed.Runs.Last().Speed >= RecordingSpeedCommand.Min);
    }

    [Fact]
    public void AnAxisMappedToAStaticCommand_TriggersAtTheSameDeflection_RegardlessOfExpo()
    {
        var input = new Input("axis-1", "Axis 1", InputType.Axis, _gamepad.Commands);
        input.SelectedCommand = _static;
        input.CommandValue = input.Values!.Single(v => v.Value == 2);
        input.Expo = 1;

        // Linear 0.278 is past the 0.25 trigger; with the curve it would be 0.02 and never trigger.
        input.InputValue = 0.3F;
        Assert.Equal(Linear(0.3F, 0.05F, 0.05F, false, true), input.InputValue);
        Assert.Equal(new[] { 2 }, _static.Runs);

        // The second input is fed the same (unbent) value as before.
        input.InputValue = -0.3F;
        Assert.Equal(Linear(-0.3F, 0.05F, 0.05F, false, true), input.SecondInput!.InputValue);
        Assert.Equal(0F, input.SecondInput.Expo);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-0.01F)]
    [InlineData(1.01F)]
    [InlineData(5F)]
    public void AnInvalidExpo_IsStoredAsZero_WithAWarning(float bad)
    {
        var id = $"axis-{Guid.NewGuid():N}";
        using var trace = new TraceRecorder();
        var input = Axis(id: id);

        var thrown = Record.Exception(() => input.Expo = bad);

        Assert.Null(thrown);
        Assert.Equal(0F, input.Expo);
        var warning = Assert.Single(trace.Events, e => e.Message.Contains(id));
        Assert.Equal(TraceEventType.Warning, warning.Type);
        Assert.Contains("expo", warning.Message, StringComparison.OrdinalIgnoreCase);

        // Shaping still works, linearly.
        input.InputValue = 0.5F;
        Assert.Equal(Linear(0.5F, 0.05F, 0.05F, false, true), input.InputValue);
    }

    [Theory]
    [InlineData(0F)]
    [InlineData(0.35F)]
    [InlineData(1F)]
    public void AValidExpo_IsKept_WithoutAWarning(float expo)
    {
        var id = $"axis-{Guid.NewGuid():N}";
        using var trace = new TraceRecorder();
        var input = Axis(id: id);

        input.Expo = expo;

        Assert.Equal(expo, input.Expo);
        Assert.DoesNotContain(trace.Events, e => e.Message.Contains(id));
    }

    [Fact]
    public void ChangingExpo_IsAPersistentChange()
    {
        var input = Axis();
        var changed = new List<string?>();
        input.PersistentPropertyChanged += (_, e) => changed.Add(e.PropertyName);

        input.Expo = 0.4F;

        Assert.Contains(nameof(IInput.Expo), changed);
    }

    /// <summary>A pan-like dynamic command (speeds 1..24) that records every speed and direction it runs with.</summary>
    private sealed class RecordingSpeedCommand : IDynamicCommand
    {
        public const int Min = 1;
        public const int Max = 24;

        public RecordingSpeedCommand(IGamepad gamepad) : base(gamepad, new NullCompanionService()) { }

        public List<(int Speed, Direction Direction)> Runs { get; } = new();

        public override string CommandName => GetType().Name;
        public override string AxisParameterName => "Max speed";
        public override string ButtonParameterName => "Speed";
        public override int MaxValue => Max;
        public override int MinValue => Min;
        public override IEnumerable<CommandDirectionOption> ButtonDirections { get; } = new[]
        {
            new CommandDirectionOption("Right", Direction.High),
            new CommandDirectionOption("Left", Direction.Low),
        };

        public override void Execute(int value, Direction direction) => Runs.Add((value, direction));
    }
}
