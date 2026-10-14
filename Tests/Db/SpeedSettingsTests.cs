using System.Diagnostics;
using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Tests.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Db;

// "Max speed" (axis) and "Speed" (button) of pan, tilt, zoom and focus are saved as percentages,
// marked by SpeedIsPercentage. Files from before carry VISCA steps and no marker: each step s is
// migrated once to ceil(100 · (s − Min) / (Max − Min)), which still sends step s at full deflection.
public sealed class SpeedSettingsTests : IDisposable
{
    private readonly string _dir;
    // Unique per test: Trace is global and test classes run in parallel.
    private readonly string _axisId = $"axis-{Guid.NewGuid():N}";
    private readonly string _buttonId = $"button-{Guid.NewGuid():N}";

    public SpeedSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PtzSpeedSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string FilePath(string name = "Gamepads.json") => Path.Combine(_dir, name);

    public static TheoryData<Type, int, int> SpeedCommands => new()
    {
        { typeof(PanCommand), 1, 24 },
        { typeof(TiltCommand), 1, 20 },
        { typeof(ZoomCommand), 0, 7 },
        { typeof(FocusMoveCommand), 0, 7 },
    };

    private sealed record Loaded(FakeGamepad Gamepad, Input Axis, Input Button, SpeedRecordingCamera Camera);

    private Loaded NewGamepad()
    {
        var gamepad = new FakeGamepad();
        var camera = new SpeedRecordingCamera();
        gamepad.SelectedCamera = camera;
        gamepad.CommandList.AddRange(new ICommand[]
        {
            new PanCommand(gamepad, new NullCompanionService()),
            new TiltCommand(gamepad, new NullCompanionService()),
            new ZoomCommand(gamepad, new NullCompanionService()),
            new FocusMoveCommand(gamepad, new NullCompanionService()),
            new PresetRecallSpeedComamnd(gamepad, new NullCompanionService()),
            new RecordingStaticCommand(gamepad),
        });
        var axis = new Input(_axisId, "Axis 1", InputType.Axis, gamepad.Commands);
        var button = new Input(_buttonId, "Button 1", InputType.Button, gamepad.Commands);
        gamepad.InputList.AddRange(new[] { axis, button });
        return new Loaded(gamepad, axis, button, camera);
    }

    private Loaded Load(GamepadSettingsStore store)
    {
        var loaded = NewGamepad();
        GamepadInputSetup.Configure(loaded.Gamepad, store.GetGamepadSettingsById(loaded.Gamepad.Id));
        return loaded;
    }

    private Loaded Load(string? fileName = null) => Load(new GamepadSettingsStore(FilePath(fileName ?? "Gamepads.json")));

    private static string Value(int? value) => value == null ? "null" : $$"""{"Name":"{{value}}","Value":{{value}}}""";

    // A settings file as written before speeds were percentages (no SpeedIsPercentage), or with
    // the marker when `marked`.
    private void WriteFile(Type? axisCommand, int? axisValue, Type? buttonCommand = null, int? buttonValue = null,
        Type? secondCommand = null, int? secondValue = null, bool marked = false, string fileName = "Gamepads.json")
    {
        string Cmd(Type? t) => t == null ? "null" : $"\"{t}\"";
        string marker = marked ? "\"SpeedIsPercentage\":true," : "";
        File.WriteAllText(FilePath(fileName), $$$"""
            [{"Id":"fake-gamepad","Name":"Fake gamepad","IsActivated":true,"Inputs":[
              {"CommandType":{{{Cmd(axisCommand)}}},"CommandDirection":null,"CommandValue":{{{Value(axisValue)}}},
               "Id":"{{{_axisId}}}","Inverted":false,"DeadZoneHigh":0,"DeadZoneLow":0,{{{marker}}}
               "SecondInputSettings":{"CommandType":{{{Cmd(secondCommand)}}},"CommandDirection":null,"CommandValue":{{{Value(secondValue)}}},"Id":"","Inverted":false,"DeadZoneHigh":0,"DeadZoneLow":0,{{{marker}}}"SecondInputSettings":null}},
              {"CommandType":{{{Cmd(buttonCommand)}}},"CommandDirection":{"Name":"Right","Direction":1},"CommandValue":{{{Value(buttonValue)}}},
               "Id":"{{{_buttonId}}}","Inverted":false,"DeadZoneHigh":0,"DeadZoneLow":0,{{{marker}}}"SecondInputSettings":null}
            ]}]
            """);
    }

    private IEnumerable<(TraceEventType Type, string Message)> InputEvents(TraceRecorder trace) =>
        trace.Events.Where(e => e.Message.Contains(_axisId) || e.Message.Contains(_buttonId));

    // The VISCA speed the camera would get for the last move.
    private static int LastViscaSpeed(SpeedRecordingCamera camera, int min, int max)
    {
        var run = camera.Runs.Last();
        Assert.NotEqual(0, run.Sign);
        return ViscaIpDevice.ToViscaSpeed(run.Speed, (byte)min, (byte)max);
    }

    [Theory]
    [MemberData(nameof(SpeedCommands))]
    public void EveryLegacyStep_IsMigrated_AndStillSendsThatStepAtFullDeflectionAndForAPressedButton(Type command, int min, int max)
    {
        using var trace = new TraceRecorder();
        for (int step = min; step <= max; step++)
        {
            WriteFile(command, step, command, step);

            var loaded = Load();

            loaded.Axis.InputValue = 1;
            Assert.Equal(step, LastViscaSpeed(loaded.Camera, min, max));
            loaded.Axis.InputValue = -1;
            Assert.Equal(step, LastViscaSpeed(loaded.Camera, min, max));

            loaded.Button.InputValue = 1;
            Assert.Equal(step, LastViscaSpeed(loaded.Camera, min, max));
        }
        Assert.Empty(InputEvents(trace));
    }

    [Theory]
    [InlineData(typeof(PanCommand), 24, 100)]
    [InlineData(typeof(PanCommand), 12, 48)]
    [InlineData(typeof(PanCommand), 1, 0)]
    [InlineData(typeof(TiltCommand), 20, 100)]
    [InlineData(typeof(TiltCommand), 7, 32)]
    [InlineData(typeof(ZoomCommand), 7, 100)]
    [InlineData(typeof(ZoomCommand), 5, 72)]
    [InlineData(typeof(FocusMoveCommand), 0, 0)]
    [InlineData(typeof(FocusMoveCommand), 1, 15)]
    public void ALegacyStep_LoadsAsItsPercentage(Type command, int step, int percent)
    {
        WriteFile(command, step, command, step);

        var loaded = Load();

        Assert.Equal(percent, loaded.Axis.CommandValue!.Value);
        Assert.Equal($"{percent} %", loaded.Axis.CommandValue!.Name);
        Assert.Equal(percent, loaded.Button.CommandValue!.Value);
    }

    [Fact]
    public void ASecondInputHoldingASpeedCommand_IsMigratedToo()
    {
        WriteFile(typeof(RecordingStaticCommand), 1, secondCommand: typeof(ZoomCommand), secondValue: 5);

        var loaded = Load();

        Assert.Equal(72, loaded.Axis.SecondInput!.CommandValue!.Value);
        Assert.Equal(1, loaded.Axis.CommandValue!.Value);
    }

    [Theory]
    [InlineData("Gamepads.json")]
    [InlineData("SkaarhojGamepads.json")]
    public void AMigratedFile_SavedAndReloaded_KeepsItsPercentages(string fileName)
    {
        // 0, 1, 7, 20 and 24 are valid both as steps and as percentages.
        foreach (var (command, step) in new[] { (typeof(PanCommand), 1), (typeof(PanCommand), 24), (typeof(TiltCommand), 20), (typeof(ZoomCommand), 7), (typeof(FocusMoveCommand), 0), (typeof(FocusMoveCommand), 1) })
        {
            WriteFile(command, step, command, step, fileName: fileName);
            var first = Load(fileName);
            var expected = (first.Axis.CommandValue!.Value, first.Button.CommandValue!.Value);

            for (int i = 0; i < 2; i++)
            {
                Assert.True(new GamepadSettingsStore(FilePath(fileName)).SaveGamepadSettings(new GamepadSettings(Load(fileName).Gamepad)));
                var reloaded = Load(fileName);
                Assert.Equal(expected, (reloaded.Axis.CommandValue!.Value, reloaded.Button.CommandValue!.Value));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(100)]
    public void AMarkedValue_LoadsAsThatPercentage(int percent)
    {
        WriteFile(typeof(PanCommand), percent, typeof(ZoomCommand), percent, marked: true);

        var loaded = Load();

        Assert.Equal(percent, loaded.Axis.CommandValue!.Value);
        Assert.Equal(percent, loaded.Button.CommandValue!.Value);
    }

    [Fact]
    public void ConfiguringTwiceFromOneStore_MigratesTheSameWay()
    {
        WriteFile(typeof(PanCommand), 12, typeof(ZoomCommand), 5);
        var store = new GamepadSettingsStore(FilePath());

        var first = Load(store);
        var second = Load(store);

        Assert.Equal(48, first.Axis.CommandValue!.Value);
        Assert.Equal(48, second.Axis.CommandValue!.Value);
        Assert.Equal(72, second.Button.CommandValue!.Value);
    }

    [Fact]
    public void StaticCommandValues_LoadUnchanged_WithOrWithoutTheMarker()
    {
        foreach (var marked in new[] { false, true })
        {
            WriteFile(typeof(PresetRecallSpeedComamnd), 24, typeof(RecordingStaticCommand), 2, marked: marked);

            var loaded = Load();

            Assert.Equal(24, loaded.Axis.CommandValue!.Value);
            Assert.Equal(2, loaded.Button.CommandValue!.Value);
        }
    }

    [Theory]
    [InlineData(typeof(PanCommand), 30, 100)]
    [InlineData(typeof(PanCommand), 0, 0)]
    [InlineData(typeof(ZoomCommand), -1, 0)]
    [InlineData(typeof(ZoomCommand), 8, 100)]
    public void AnOutOfRangeLegacyStep_IsClampedWithAWarning_AndTheRestStillLoads(Type command, int step, int percent)
    {
        using var trace = new TraceRecorder();
        WriteFile(command, step, typeof(RecordingStaticCommand), 2);

        var thrown = Record.Exception(() => Load());
        var loaded = Load();

        Assert.Null(thrown);
        Assert.Equal(percent, loaded.Axis.CommandValue!.Value);
        Assert.Equal(2, loaded.Button.CommandValue!.Value);
        var warning = InputEvents(trace).First();
        Assert.Equal(TraceEventType.Warning, warning.Type);
        Assert.Contains(_axisId, warning.Message);
        Assert.DoesNotContain(InputEvents(trace), e => e.Type == TraceEventType.Error);
    }

    [Theory]
    [InlineData(101, 100)]
    [InlineData(-5, 0)]
    public void AnOutOfRangeMarkedPercentage_IsClampedWithAWarning(int stored, int percent)
    {
        using var trace = new TraceRecorder();
        WriteFile(typeof(TiltCommand), stored, marked: true);

        var loaded = Load();

        Assert.Equal(percent, loaded.Axis.CommandValue!.Value);
        var warning = Assert.Single(InputEvents(trace));
        Assert.Equal(TraceEventType.Warning, warning.Type);
    }

    [Fact]
    public void AFileWithoutSpeedMappings_LoadsWithoutWarnings()
    {
        using var trace = new TraceRecorder();
        WriteFile(null, null, typeof(RecordingStaticCommand), 1);

        var loaded = Load();

        Assert.Null(loaded.Axis.SelectedCommand);
        Assert.Equal(1, loaded.Button.CommandValue!.Value);
        Assert.Empty(InputEvents(trace));
    }

    [Fact]
    public void SavedSettings_CarryTheMarker_OnEveryInputAndSecondInput()
    {
        var saved = NewGamepad();
        saved.Axis.SelectedCommand = saved.Gamepad.Commands.OfType<PanCommand>().Single();

        var settings = new GamepadSettings(saved.Gamepad);

        Assert.All(settings.Inputs!, i => Assert.True(i.SpeedIsPercentage));
        Assert.True(settings.Inputs!.Single(i => i.Id == _axisId).SecondInputSettings!.SpeedIsPercentage);
    }
}
