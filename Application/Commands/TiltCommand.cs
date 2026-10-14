using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Commands;

public class TiltCommand : IDynamicCommand
{
    private readonly ISpeedProfilesService _speedProfiles;

    public TiltCommand(IGamepad gamepad, IBitfocusCompanionService companionService, ISpeedProfilesService speedProfiles) : base(gamepad, companionService)
    {
        _speedProfiles = speedProfiles;
    }

    public override string CommandName => "Tilt";
    public override string AxisParameterName => "Max speed";
    public override string ButtonParameterName => "Speed";

    public override IEnumerable<CommandDirectionOption> ButtonDirections => buttonDirectionsList;
    private static readonly IReadOnlyCollection<CommandDirectionOption> buttonDirectionsList = new CommandDirectionOption[] {
        new CommandDirectionOption("Up", Direction.High),
        new CommandDirectionOption("Down", Direction.Low),
    };

    public override int LegacyStepMax => 20;
    public override int LegacyStepMin => 1;

    protected override void Move(double speed, Direction direction)
    {
        // Every tilt move, on every camera, runs at the active speed profile's tilt percentage.
        speed *= _speedProfiles.Profiles.Active.Tilt / 100.0;
        TiltDir tiltDir;

        switch (direction)
        {
            case Direction.Stop:
                tiltDir = TiltDir.Stop;
                break;
            case Direction.High:
                tiltDir = TiltDir.Up;
                break;
            case Direction.Low:
                tiltDir = TiltDir.Down;
                break;
            default:
                tiltDir = TiltDir.Stop;
                break;
        }

        Gamepad.SelectedCamera?.Tilt(speed, tiltDir);
    }
}
