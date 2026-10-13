using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Devices;

/// <summary>
/// Sets up a freshly loaded gamepad's inputs: the Companion virtual buttons, then its saved settings.
/// </summary>
public static class GamepadInputSetup
{
    /// <summary>
    /// Adds one virtual button input per Companion event, numbered after the gamepad's physical
    /// buttons, then applies <paramref name="settings"/> (if any) to every input, virtual ones included.
    /// </summary>
    /// <param name="addInput">Adds an input to the gamepad under the given id.</param>
    /// <param name="buttonIdFormat">Format of a button input's id from its button number; a Companion press looks the virtual button up by it.</param>
    public static void Configure(IGamepad gamepad, Action<string, IInput> addInput, string buttonIdFormat,
        IEnumerable<BitfocusCompanionEvent> companionEvents, GamepadSettings? settings)
    {
        // Before the saved-settings check: a gamepad seen for the first time needs them too.
        AddCompanionButtons(gamepad, addInput, buttonIdFormat, companionEvents);

        if (settings != null)
            ApplySettings(gamepad, settings);
    }

    private static void AddCompanionButtons(IGamepad gamepad, Action<string, IInput> addInput, string buttonIdFormat,
        IEnumerable<BitfocusCompanionEvent> companionEvents)
    {
        var buttonCount = gamepad.Inputs.Count(i => i.InputType == InputType.Button);

        IBitfocusCompanionService.SetJoystickButtonCount(buttonCount);

        foreach (var companionEvent in companionEvents)
        {
            var id = string.Format(buttonIdFormat, companionEvent.Button);
            var name = string.Format(companionEvent.EventNameFormatString, companionEvent.Value);

            addInput(id, new Input(id, name, InputType.Button, gamepad.Commands));
        }
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
