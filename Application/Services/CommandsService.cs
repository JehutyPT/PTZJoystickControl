using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Services;

public class CommandsService : ICommandsService
{
    private IBitfocusCompanionService _bitfocusCompanionService;
    private readonly ISpeedProfilesService _speedProfiles;

    public CommandsService(IBitfocusCompanionService bitfocusCompanionService, ISpeedProfilesService speedProfiles)
    {
        _bitfocusCompanionService = bitfocusCompanionService;
        _speedProfiles = speedProfiles;
    }

    public IEnumerable<ICommand> GetCommandsForGamepad(IGamepad gamepad)
    {
        return new ICommand[]
        {
            new PanCommand(gamepad, _bitfocusCompanionService, _speedProfiles),
            new TiltCommand(gamepad, _bitfocusCompanionService, _speedProfiles),
            new ZoomCommand(gamepad, _bitfocusCompanionService, _speedProfiles),
            new FocusMoveCommand(gamepad, _bitfocusCompanionService, _speedProfiles),
            new FocusStepCommand(gamepad, _bitfocusCompanionService),
            new FocusModeCommand(gamepad, _bitfocusCompanionService),
            new FocusLockCommand(gamepad, _bitfocusCompanionService),
            new PresetCommand(gamepad, _bitfocusCompanionService),
            new PresetRecallSpeedComamnd(gamepad, _bitfocusCompanionService),
            new SelectCameraCommand(gamepad, _bitfocusCompanionService),
            new PowerCommand(gamepad, _bitfocusCompanionService)
        };
    }
}
