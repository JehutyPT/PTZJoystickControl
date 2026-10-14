using System.Diagnostics;
using PtzJoystickControl.Core.Commands;
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
                input.CommandValue = LoadCommandValue(input, storedInput, input.Id);
                input.Inverted = storedInput.Inverted;
                input.Saturation = storedInput.DeadZoneHigh;
                input.DeadZone = storedInput.DeadZoneLow;
                // Validated by the setter: a bad value becomes 0 with a warning.
                input.Expo = storedInput.Expo;
                if (input.SecondInput != null && storedInput.SecondInputSettings != null)
                {
                    if (storedInput.SecondInputSettings.CommandType != null && commandsDict.TryGetValue(storedInput.SecondInputSettings.CommandType, out var secondCommand))
                        input.SecondInput.SelectedCommand = secondCommand;
                    input.SecondInput.CommandDirection = storedInput.SecondInputSettings.CommandDirection;
                    input.SecondInput.CommandValue = LoadCommandValue(input.SecondInput, storedInput.SecondInputSettings, $"{input.Id} (second command)");
                }
            }
        }
    }

    /// <summary>
    /// The stored command value, with a speed (dynamic command) value read as a percentage of the
    /// full range. One saved without <see cref="InputSettings.SpeedIsPercentage"/> is a VISCA step
    /// <c>s</c> from before percentages, migrated to <c>ceil(100 · (s − Min) / (Max − Min))</c>: the
    /// smallest percentage that still sends step <c>s</c> at full deflection. Out-of-range values
    /// (hand-edited files) are clamped with a warning. The stored settings are never modified.
    /// </summary>
    private static CommandValueOption? LoadCommandValue(IInput input, InputSettings stored, string logName)
    {
        if (input.SelectedCommand is not IDynamicCommand command || stored.CommandValue is null)
            return stored.CommandValue;

        int percent;
        if (stored.SpeedIsPercentage)
        {
            percent = stored.CommandValue.Value;
            if (percent < 0 || percent > 100)
            {
                percent = Math.Clamp(percent, 0, 100);
                Trace.TraceWarning($"Input {logName}: {command.CommandName} speed {stored.CommandValue.Value} % is not between 0 and 100; using {percent} %");
            }
        }
        else
        {
            int min = command.LegacyStepMin, max = command.LegacyStepMax;
            int step = stored.CommandValue.Value;
            if (step < min || step > max)
            {
                step = Math.Clamp(step, min, max);
                Trace.TraceWarning($"Input {logName}: {command.CommandName} saved speed step {stored.CommandValue.Value} is not between {min} and {max}; using {step}");
            }
            // Integer ceiling: exact, so a step that is a whole percentage never rounds up past it.
            percent = (100 * (step - min) + (max - min - 1)) / (max - min);
        }

        return command.Options.FirstOrDefault(o => o.Value == percent);
    }
}
