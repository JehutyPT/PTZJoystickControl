using System.Diagnostics;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Tests.Devices;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Db;

// An axis's expo is saved in Gamepads.json with its other settings. Files from before expo existed
// load as linear (expo 0), and a bad stored value is replaced by 0 with a warning.
public sealed class ExpoSettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    // Unique per test: Trace is global and test classes run in parallel.
    private readonly string _axisId = $"axis-{Guid.NewGuid():N}";

    public ExpoSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PtzExpoSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "Gamepads.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (FakeGamepad Gamepad, Input Axis) GamepadWithAxis()
    {
        var gamepad = new FakeGamepad();
        gamepad.CommandList.Add(new RecordingDynamicCommand(gamepad));
        var axis = new Input(_axisId, "Axis 1", InputType.Axis, gamepad.Commands);
        gamepad.InputList.Add(axis);
        return (gamepad, axis);
    }

    private Input LoadAxis()
    {
        var (gamepad, axis) = GamepadWithAxis();
        GamepadInputSetup.Configure(gamepad, new GamepadSettingsStore(_path).GetGamepadSettingsById(gamepad.Id));
        return axis;
    }

    // A Gamepads.json as written before expo existed, optionally with an extra key on the axis.
    private void WriteFile(string extraAxisJson = "") => File.WriteAllText(_path, $$$"""
        [{"Id":"fake-gamepad","Name":"Fake gamepad","IsActivated":true,"Inputs":[
          {"CommandType":"{{{typeof(RecordingDynamicCommand)}}}","CommandDirection":null,"CommandValue":{"Name":"5","Value":5},
           "Id":"{{{_axisId}}}","Inverted":true,"DeadZoneHigh":0.1,"DeadZoneLow":0.15,{{{extraAxisJson}}}
           "SecondInputSettings":{"CommandType":null,"CommandDirection":null,"CommandValue":null,"Id":"","Inverted":false,"DeadZoneHigh":0,"DeadZoneLow":0,"SecondInputSettings":null}}
        ]}]
        """);

    private IEnumerable<(TraceEventType Type, string Message)> AxisEvents(TraceRecorder trace) =>
        trace.Events.Where(e => e.Message.Contains(_axisId));

    [Fact]
    public void Expo_SavesAndLoads()
    {
        var (saved, savedAxis) = GamepadWithAxis();
        savedAxis.SelectedCommand = saved.Commands.Single();
        savedAxis.Expo = 0.4F;

        Assert.True(new GamepadSettingsStore(_path).SaveGamepadSettings(new GamepadSettings(saved)));

        Assert.Equal(0.4F, LoadAxis().Expo);
    }

    [Fact]
    public void AFileFromBeforeExpo_LoadsLinear_WithoutAWarning_AndKeepsItsOtherSettings()
    {
        using var trace = new TraceRecorder();
        WriteFile();

        var axis = LoadAxis();

        Assert.Equal(0F, axis.Expo);
        Assert.IsType<RecordingDynamicCommand>(axis.SelectedCommand);
        Assert.True(axis.Inverted);
        Assert.Equal(0.15F, axis.DeadZone);
        Assert.Equal(0.1F, axis.Saturation);
        // Saved as VISCA step 5 of 0..7: migrated to ceil(100 · 5 / 7) = 72 %.
        Assert.Equal(72, axis.CommandValue!.Value);
        Assert.Empty(AxisEvents(trace));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.2")]
    [InlineData("1e40")]
    public void AnOutOfRangeStoredExpo_LoadsAsZero_WithAWarning_AndTheRestStillLoads(string storedExpo)
    {
        using var trace = new TraceRecorder();
        WriteFile($"\"Expo\":{storedExpo},");

        var axis = LoadAxis();

        Assert.Equal(0F, axis.Expo);
        var warning = Assert.Single(AxisEvents(trace));
        Assert.Equal(TraceEventType.Warning, warning.Type);
        Assert.IsType<RecordingDynamicCommand>(axis.SelectedCommand);
        Assert.True(axis.Inverted);
        Assert.Equal(0.15F, axis.DeadZone);
    }

    [Fact]
    public void TheSecondInputsExpo_IsNotLoaded()
    {
        WriteFile("\"Expo\":0.5,");
        var json = File.ReadAllText(_path).Replace("\"DeadZoneLow\":0,", "\"DeadZoneLow\":0,\"Expo\":0.7,");
        File.WriteAllText(_path, json);

        var axis = LoadAxis();

        Assert.Equal(0.5F, axis.Expo);
        Assert.Equal(0F, axis.SecondInput!.Expo);
    }
}
