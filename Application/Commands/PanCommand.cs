using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Commands;

public class PanCommand : IDynamicCommand
{
    private readonly ISpeedProfilesService _speedProfiles;

    public PanCommand(IGamepad gamepad, IBitfocusCompanionService companionService, ISpeedProfilesService speedProfiles) : base(gamepad, companionService)
    {
        _speedProfiles = speedProfiles;
    }

    public override string CommandName => "Pan";
    public override string AxisParameterName => "Max speed";
    public override string ButtonParameterName => "Speed";

    public override int LegacyStepMax => 24;
    public override int LegacyStepMin => 1;
    public override IEnumerable<CommandDirectionOption> ButtonDirections => buttonDirectionsList;
    public static readonly IReadOnlyCollection<CommandDirectionOption> buttonDirectionsList = new CommandDirectionOption[] {
        new CommandDirectionOption("Right", Direction.High),
        new CommandDirectionOption("Left", Direction.Low)
    };

    protected override void Move(double speed, Direction direction)
    {
        // Every pan move, on every camera, runs at the active speed profile's pan percentage.
        speed *= _speedProfiles.Profiles.Active.Pan / 100.0;
        PanDir panDir;

        switch (direction)
        {
            case Direction.Stop:
                panDir = PanDir.Stop;
                break;
            case Direction.High:
                panDir = PanDir.Right;
                break;
            case Direction.Low:
                panDir = PanDir.Left;
                break;
            default:
                panDir = PanDir.Stop;
                break;
        }

        Gamepad.SelectedCamera?.Pan(speed, panDir);
    }

}
