using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.SdlGamepads.Models;
using PtzJoystickControl.SdlGamepads.Services;
using PtzJoystickControl.Tests.Services;
using PtzJoystickControl.Tests.Skaarhoj;
using System.Diagnostics;
using Xunit;

namespace PtzJoystickControl.Tests.SdlGamepads;

// What SdlGamepadsService.AddJoystick does with a joystick's saved activation when it is plugged in
// or comes back, under the one-active-controller rule. AddJoystick itself needs native SDL.
public sealed class SdlJoystickActivationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptz-sdl-" + Guid.NewGuid().ToString("N"));
    // Unique per test: trace listeners are global and test classes run in parallel.
    private readonly string _id = "usb-" + Guid.NewGuid().ToString("N") + "#0";
    private readonly InputQueue _queue = new();
    private readonly TraceRecorder _trace = new();
    private readonly List<string> _activated = new();

    public SdlJoystickActivationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _trace.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private GamepadSettingsStore Store() => new(Path.Combine(_dir, "Gamepads.json"));

    private SdlGamepadInfo Saved(bool activated)
    {
        Assert.True(Store().SaveGamepadSettings(new GamepadSettings { Id = _id, Name = _id, IsActivated = activated }));
        return new SdlGamepadInfo { Id = _id, Name = _id, IsActivated = activated, IsConnected = true };
    }

    // The rule as installed in the app, with the panel source holding one controller.
    private void PanelSource(bool panelActive)
    {
        var panel = new CompositeGamepadsServiceTests.FakeGamepadsService("panel", _queue);
        var info = panel.AddInfo("panel");
        _ = new CompositeGamepadsService(_queue, panel);
        if (panelActive)
            panel.ActivateGamepad(info);
    }

    private void Connect(SdlGamepadInfo info)
    {
        lock (_queue.ControllersLock)
            SdlJoystickActivation.OnConnected(info, _queue, Store(), i => _activated.Add(i.Id));
    }

    private IEnumerable<string> Warnings =>
        _trace.Events.Where(e => e.Type == TraceEventType.Warning && e.Message.Contains(_id)).Select(e => e.Message);

    [Fact]
    public void SavedActive_PluggedInWhileAnotherControllerIsActive_StaysOff_SavesIt_AndWarnsOnce()
    {
        PanelSource(panelActive: true);
        var info = Saved(activated: true);

        Connect(info);

        Assert.Empty(_activated);
        Assert.False(info.IsActivated);
        Assert.False(Store().GetGamepadSettingsById(_id)!.IsActivated);
        Assert.Single(Warnings);
    }

    [Fact]
    public void SavedActive_PluggedInWhileNoOtherControllerIsActive_Activates()
    {
        PanelSource(panelActive: false);
        var info = Saved(activated: true);

        Connect(info);

        Assert.Equal(new[] { _id }, _activated);
        Assert.True(info.IsActivated);
        Assert.True(Store().GetGamepadSettingsById(_id)!.IsActivated);
        Assert.Empty(Warnings);
    }

    [Fact]
    public void SavedInactive_PluggedIn_StaysOff_WithoutAWarning()
    {
        PanelSource(panelActive: true);
        var info = Saved(activated: false);

        Connect(info);

        Assert.Empty(_activated);
        Assert.False(info.IsActivated);
        Assert.False(Store().GetGamepadSettingsById(_id)!.IsActivated);
        Assert.Empty(Warnings);
    }
}
