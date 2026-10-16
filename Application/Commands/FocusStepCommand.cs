using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Commands;

/// <summary>
/// Nudges the selected camera's focus position toward far or near by a share of its focus position
/// range (<see cref="ICamera.FocusStep"/>): one step per run, so once per detent on a relative input
/// and once per press on a button. The speed profile does not scale it.
/// </summary>
public class FocusStepCommand : IStaticCommand
{
    /// <summary>The largest step, in hundredths of a percent of the focus range: 5 %.</summary>
    internal const int MaxStep = 500;

    public FocusStepCommand(IGamepad gamepad, IBitfocusCompanionService companionService) : base(gamepad, companionService)
    {
    }

    public override string CommandName => "Focus step";

    public override string AxisParameterName => "Step";

    public override string ButtonParameterName => "Step";

    // The signed share of the focus range in hundredths of a percent, Far positive; finest first.
    public override IEnumerable<CommandValueOption> Options => optionsList;
    private static readonly IEnumerable<CommandValueOption> optionsList = new CommandValueOption[] {
        new CommandValueOption("Far 0.05 %", 5),
        new CommandValueOption("Near 0.05 %", -5),
        new CommandValueOption("Far 0.1 %", 10),
        new CommandValueOption("Near 0.1 %", -10),
        new CommandValueOption("Far 0.25 %", 25),
        new CommandValueOption("Near 0.25 %", -25),
        new CommandValueOption("Far 0.5 %", 50),
        new CommandValueOption("Near 0.5 %", -50),
        new CommandValueOption("Far 1 %", 100),
        new CommandValueOption("Near 1 %", -100),
        new CommandValueOption("Far 2 %", 200),
        new CommandValueOption("Near 2 %", -200),
        new CommandValueOption("Far 5 %", MaxStep),
        new CommandValueOption("Near 5 %", -MaxStep),
    };

    // A value off the list can only come from a hand-edited settings file: 0 does nothing, and
    // anything beyond ±5 % is limited to it. Never throws.
    public override void Execute(int value)
    {
        if (value == 0)
            return;

        var step = Math.Clamp(value, -MaxStep, MaxStep);
        Gamepad.SelectedCamera?.FocusStep(Math.Abs(step) / 10000.0, step > 0 ? FocusDir.Far : FocusDir.Near);
    }
}
