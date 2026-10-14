using System.Diagnostics;
using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Tests.Services;
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
            Assert.Equal((1.0, Direction.High), _speed.Runs.Last());

            input.InputValue = -raw;
            Assert.Equal(-1F, input.InputValue);
            Assert.Equal((1.0, Direction.Low), _speed.Runs.Last());
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
        Assert.True(_speed.Runs.Last().Speed > 0);
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

    // The real speed commands with the VISCA ranges they were saved in: pan 1..24, tilt 1..20,
    // zoom and focus 0..7.
    public static TheoryData<string, int, int> SpeedCommands => new()
    {
        { "Pan", 1, 24 },
        { "Tilt", 1, 20 },
        { "Zoom", 0, 7 },
        { "Focus", 0, 7 },
    };

    private static (Input Axis, SpeedRecordingCamera Camera) RealSpeedAxis(string commandName, int percent)
    {
        var gamepad = new FakeGamepad();
        var camera = new SpeedRecordingCamera();
        gamepad.SelectedCamera = camera;
        IDynamicCommand command = commandName switch
        {
            "Pan" => new PanCommand(gamepad, new NullCompanionService(), TestSpeedProfiles.Full()),
            "Tilt" => new TiltCommand(gamepad, new NullCompanionService(), TestSpeedProfiles.Full()),
            "Zoom" => new ZoomCommand(gamepad, new NullCompanionService(), TestSpeedProfiles.Full()),
            _ => new FocusMoveCommand(gamepad, new NullCompanionService(), TestSpeedProfiles.Full()),
        };
        gamepad.CommandList.Add(command);
        var axis = new Input("axis-1", "Axis 1", InputType.Axis, gamepad.Commands) { DeadZone = 0, Saturation = 0 };
        axis.SelectedCommand = command;
        axis.CommandValue = axis.Values!.Single(v => v.Value == percent);
        return (axis, camera);
    }

    // The VISCA speed and direction a run would put on the wire, through ViscaIpDevice's quantization.
    private static (int Speed, int Sign) Visca((double Speed, int Sign) run, int min, int max) =>
        run.Sign == 0 ? (0, 0) : (ViscaIpDevice.ToViscaSpeed(run.Speed, (byte)min, (byte)max), run.Sign);

    // Before speeds were fractions, Input sent step = Min + (int)Map(x, -1, 1, -(s - Min), s - Min) in
    // float, never a stop outside the deadzone. Copied from that code: the reference for "unchanged".
    private static (int Speed, int Sign) LegacyVisca(float x, int legacyStep, int min)
    {
        int range = legacyStep - min;
        float mapped = Util.Map(x, -1, 1, -range, range);
        if (mapped == 0 && x != 0)
            mapped = x > 0 ? float.Epsilon : -float.Epsilon;
        if (mapped < 0) return ((int)(Math.Abs(mapped) + min), -1);
        if (mapped > 0) return ((int)(mapped + min), 1);
        return (0, 0);
    }

    [Theory]
    [MemberData(nameof(SpeedCommands))]
    public void AtMaxSpeed100_EveryInputValue_SendsTodaysViscaSpeedAndDirection(string command, int min, int max)
    {
        var (axis, camera) = RealSpeedAxis(command, percent: 100);

        foreach (var x in Sweep())
        {
            axis.InputValue = x;
            Assert.Equal(LegacyVisca(x, max, min), Visca(camera.Runs.Last(), min, max));
        }
    }

    [Theory]
    [MemberData(nameof(SpeedCommands))]
    public void AMigratedMaxSpeed_SendsTheSavedStepAtFullDeflection_AndIsWithinOneStepEverywhere(string command, int min, int max)
    {
        for (int step = min; step <= max; step++)
        {
            // The migration rule: the smallest whole percentage that still reaches step s.
            int percent = (int)Math.Ceiling(100.0 * (step - min) / (max - min) - 1e-9);
            var (axis, camera) = RealSpeedAxis(command, percent);

            foreach (var x in Sweep())
            {
                axis.InputValue = x;
                var actual = Visca(camera.Runs.Last(), min, max);
                var legacy = LegacyVisca(x, step, min);
                Assert.Equal(legacy.Sign, actual.Sign);
                Assert.True(Math.Abs(actual.Speed - legacy.Speed) <= 1, $"{command} step {step} ({percent} %), x {x}: {actual.Speed} vs {legacy.Speed}");
            }

            axis.InputValue = 1;
            Assert.Equal((step, 1), Visca(camera.Runs.Last(), min, max));
            axis.InputValue = -1;
            Assert.Equal((step, -1), Visca(camera.Runs.Last(), min, max));
        }
    }

    [Theory]
    [MemberData(nameof(SpeedCommands))]
    public void SpeedRisesWithDeflection_AndOnlyTheDeadzoneStops(string command, int min, int max)
    {
        var (axis, camera) = RealSpeedAxis(command, percent: 37);
        axis.DeadZone = 0.1F;
        axis.Expo = 0.5F;

        double previous = 0;
        foreach (var x in Sweep().Where(x => x >= 0))
        {
            axis.InputValue = x;
            var run = camera.Runs.Last();
            if (x <= 0.1F)
                Assert.Equal(0, run.Sign);
            else
            {
                Assert.Equal(1, run.Sign);
                Assert.True(run.Speed > 0 && run.Speed <= 1, $"x {x}: speed {run.Speed}");
                Assert.True(run.Speed >= previous, $"x {x}: {run.Speed} < {previous}");
                Assert.InRange(Visca(run, min, max).Speed, min, max);
                previous = run.Speed;
            }
        }
    }

    [Theory]
    [InlineData(100, 1.0, Direction.High, 1)]
    [InlineData(37, 0.37, Direction.High, 1)]
    [InlineData(0, 0.0, Direction.High, 1)]
    [InlineData(100, 1.0, Direction.Low, -1)]
    [InlineData(37, 0.37, Direction.Low, -1)]
    public void AButtonMappedToASpeedCommand_RunsAtItsSpeedPercentageInItsDirection_AndStopsOnRelease(int percent, double expected, Direction direction, int sign)
    {
        var gamepad = new FakeGamepad();
        var camera = new SpeedRecordingCamera();
        gamepad.SelectedCamera = camera;
        var pan = new PanCommand(gamepad, new NullCompanionService(), TestSpeedProfiles.Full());
        gamepad.CommandList.Add(pan);
        var button = new Input("button-1", "Button 1", InputType.Button, gamepad.Commands);
        button.SelectedCommand = pan;
        button.CommandValue = button.Values!.Single(v => v.Value == percent);
        button.CommandDirection = button.Directions!.Single(d => d.Direction == direction);

        button.InputValue = 1;
        Assert.Equal(sign, camera.Runs.Last().Sign);
        Assert.Equal(expected, camera.Runs.Last().Speed, 9);

        button.InputValue = 0;
        Assert.Equal(0, camera.Runs.Last().Sign);
    }

    [Fact]
    public void ANewlyMappedSpeedCommand_OffersZeroTo100Percent_AndDefaultsTo100()
    {
        var input = Axis();

        Assert.Equal(Enumerable.Range(0, 101), input.Values!.Select(v => v.Value));
        Assert.Equal("37 %", input.Values!.Single(v => v.Value == 37).Name);
        Assert.Equal(100, input.CommandValue!.Value);
    }

    [Fact]
    public void TheOutputValue_IsTheEffectiveSpeedAsAWholePercentage()
    {
        var input = Axis(deadZone: 0, saturation: 0);
        input.CommandValue = input.Values!.Single(v => v.Value == 50);

        input.InputValue = 0.74F;
        Assert.Equal((Direction.High, 37), (input.CurrentDirection, input.CurrentValue));

        input.InputValue = -1;
        Assert.Equal((Direction.Low, 50), (input.CurrentDirection, input.CurrentValue));

        input.InputValue = 0;
        Assert.Equal(Direction.Stop, input.CurrentDirection);
    }

    // Full deflection reads exactly the Max speed. At 29, 57 and 58 % (pan step 14 migrates to 57 %)
    // the speed × 100 lands a hair below the whole number (28.999999999999996), so the readout must
    // round, not truncate.
    [Theory]
    [InlineData(29)]
    [InlineData(50)]
    [InlineData(57)]
    [InlineData(58)]
    [InlineData(100)]
    public void AtFullDeflection_TheOutputValue_IsTheMaxSpeed(int percent)
    {
        var input = Axis();
        input.CommandValue = input.Values!.Single(v => v.Value == percent);

        input.InputValue = 1;
        Assert.Equal((Direction.High, percent), (input.CurrentDirection, input.CurrentValue));

        input.InputValue = -1;
        Assert.Equal((Direction.Low, percent), (input.CurrentDirection, input.CurrentValue));
    }

    [Theory]
    [InlineData(double.NaN, 0.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 1.0)]
    public void AnOutOfRangeSpeedFraction_IsClampedWithAWarning_NeverThrown(double bad, double clamped)
    {
        var gamepad = new FakeGamepad();
        var camera = new SpeedRecordingCamera();
        gamepad.SelectedCamera = camera;
        var zoom = new ZoomCommand(gamepad, new NullCompanionService(), TestSpeedProfiles.Full());
        using var trace = new TraceRecorder();

        var thrown = Record.Exception(() => zoom.Execute(bad, Direction.High));

        Assert.Null(thrown);
        Assert.Equal((clamped, 1), camera.Runs.Last());
        Assert.Contains(trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains("Zoom: speed fraction " + bad));
    }

    /// <summary>A pan-like dynamic command that records every speed fraction and direction it runs with.</summary>
    private sealed class RecordingSpeedCommand : IDynamicCommand
    {
        public RecordingSpeedCommand(IGamepad gamepad) : base(gamepad, new NullCompanionService()) { }

        public List<(double Speed, Direction Direction)> Runs { get; } = new();

        public override string CommandName => GetType().Name;
        public override string AxisParameterName => "Max speed";
        public override string ButtonParameterName => "Speed";
        public override int LegacyStepMax => 24;
        public override int LegacyStepMin => 1;
        public override IEnumerable<CommandDirectionOption> ButtonDirections { get; } = new[]
        {
            new CommandDirectionOption("Right", Direction.High),
            new CommandDirectionOption("Left", Direction.Low),
        };

        protected override void Move(double speed, Direction direction) => Runs.Add((speed, direction));
    }
}
