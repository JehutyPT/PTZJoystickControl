using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;

namespace PtzJoystickControl.Application.Devices;

/// <summary>
/// Sets up a freshly loaded gamepad's inputs from its saved settings.
/// </summary>
public static class GamepadInputSetup
{
    /// <summary>
    /// Applies <paramref name="settings"/> (if any) to the gamepad's inputs. Stored entries for
    /// inputs the gamepad does not have (such as the removed Companion virtual buttons) are
    /// ignored, and dropped the next time the gamepad's settings are saved.
    /// </summary>
    public static void Configure(IGamepad gamepad, GamepadSettings? settings)
    {
        if (settings != null)
            ApplySettings(gamepad, settings);
    }

    private static void ApplySettings(IGamepad gamepad, GamepadSettings settings)
    {
        var commandsDict = gamepad.Commands.ToDictionary(val => val.GetType().ToString());

        foreach (IInput input in gamepad.Inputs)
        {
            InputSettings? storedInput = settings.Inputs?.FirstOrDefault(i => i.Id == input.Id);

            if (storedInput != null)
            {
                if (storedInput.CommandType != null && commandsDict.TryGetValue(storedInput.CommandType, out var command))
                    input.SelectedCommand = command;
                input.CommandDirection = storedInput.CommandDirection;
                input.CommandValue = storedInput.CommandValue;
                input.Inverted = storedInput.Inverted;
                input.Saturation = storedInput.DeadZoneHigh;
                input.DeadZone = storedInput.DeadZoneLow;
                if (input.SecondInput != null && storedInput.SecondInputSettings != null)
                {
                    if (storedInput.SecondInputSettings.CommandType != null && commandsDict.TryGetValue(storedInput.SecondInputSettings.CommandType, out var secondCommand))
                        input.SecondInput.SelectedCommand = secondCommand;
                    input.SecondInput.CommandDirection = storedInput.SecondInputSettings.CommandDirection;
                    input.SecondInput.CommandValue = storedInput.SecondInputSettings.CommandValue;
                }
            }
        }
    }
}
