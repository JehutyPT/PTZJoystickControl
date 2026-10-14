using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Commands;

public class ZoomCommand : IDynamicCommand
{
    public ZoomCommand(IGamepad gamepad, IBitfocusCompanionService companionService) : base(gamepad, companionService)
    {
    }

    public override string CommandName => "Zoom";
    public override string AxisParameterName => "Max speed";
    public override string ButtonParameterName => "Speed";

    public override int LegacyStepMax => 7;
    public override int LegacyStepMin => 0;

    public override IEnumerable<CommandDirectionOption> ButtonDirections => buttonDirectionsList;
    private static readonly IReadOnlyCollection<CommandDirectionOption> buttonDirectionsList = new CommandDirectionOption[] {
        new CommandDirectionOption("Tele", Direction.High),
        new CommandDirectionOption("Wide", Direction.Low),
    };

    protected override void Move(double speed, Direction direction)
    {
        ZoomDir zoomDir;

        switch (direction)
        {
            case Direction.Stop:
                zoomDir = ZoomDir.Stop;
                break;
            case Direction.High:
                zoomDir = ZoomDir.Tele;
                break;
            case Direction.Low:
                zoomDir = ZoomDir.Wide;
                break;
            default:
                zoomDir = ZoomDir.Stop;
                break;
        }

        Gamepad.SelectedCamera?.Zoom(speed, zoomDir);
    }
}
