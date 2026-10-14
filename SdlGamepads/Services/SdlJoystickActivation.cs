using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Db;
using PtzJoystickControl.SdlGamepads.Models;

namespace PtzJoystickControl.SdlGamepads.Services;

/// <summary>
/// Whether a joystick that is plugged in or comes back becomes active, kept apart from SDL so it
/// can be tested without the native library.
/// </summary>
internal static class SdlJoystickActivation
{
    // Under the controllers lock. Plugged in saved as active: only if no other controller is (one at
    // a time; the rule logs the warning). Otherwise it stays off and is saved as not activated.
    public static void OnConnected(SdlGamepadInfo gamepadInfo, InputQueue inputQueue, IGamepadSettingsStore settingsStore, Action<SdlGamepadInfo> activate)
    {
        if (!gamepadInfo.IsActivated)
            return;

        if (inputQueue.MayActivate(gamepadInfo))
            activate(gamepadInfo);
        else
        {
            gamepadInfo.IsActivated = false;
            PersistDeactivated(settingsStore, gamepadInfo.Id);
        }
    }

    public static void PersistDeactivated(IGamepadSettingsStore settingsStore, string id)
    {
        var settings = settingsStore.GetGamepadSettingsById(id);
        if (settings is { IsActivated: true })
        {
            settings.IsActivated = false;
            settingsStore.SaveGamepadSettings(settings);
        }
    }
}
