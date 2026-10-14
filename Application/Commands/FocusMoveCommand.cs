using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Commands;

public class FocusMoveCommand : IDynamicCommand
{
    public FocusMoveCommand(IGamepad gamepad, IBitfocusCompanionService companionService) : base(gamepad, companionService)
    {
    }

    public override string CommandName => "Manual focus";
    public override string AxisParameterName => "Max speed";
    public override string ButtonParameterName => "Speed";

    public override int LegacyStepMax => 7;
    public override int LegacyStepMin => 0;

    public override IEnumerable<CommandDirectionOption> ButtonDirections => buttonDirectionsList;
    private static readonly IReadOnlyCollection<CommandDirectionOption> buttonDirectionsList = new CommandDirectionOption[] {
        new CommandDirectionOption("Far", Direction.High),
        new CommandDirectionOption("Near", Direction.Low)
    };

    protected override void Move(double speed, Direction direction)
    {
        FocusDir focusDir;

        switch (direction)
        {
            case Direction.Stop:
                focusDir = FocusDir.Stop;
                break;
            case Direction.High:
                focusDir = FocusDir.Far;
                break;
            case Direction.Low:
                focusDir = FocusDir.Near;
                break;
            default:
                focusDir = FocusDir.Stop;
                break;
        }

        Gamepad.SelectedCamera?.Focus(speed, focusDir);
    }
}
