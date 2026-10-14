using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;
using System.Diagnostics;

namespace PtzJoystickControl.Core.Commands
{
    public enum Direction
    {
        Stop,
        High,
        Low,
    }

    public abstract class IDynamicCommand : ICommand
    {
        private IGamepad gamepad = null!;
        private IBitfocusCompanionService companionService = null!;
        public IDynamicCommand(IGamepad gamepad, IBitfocusCompanionService companionService)
        {
            Gamepad = gamepad;
            CompanionService = companionService;
        }

        public IGamepad Gamepad { get => gamepad; private set => gamepad = value ?? throw new ArgumentNullException("Gamepad cannot be Null!"); }
        public IBitfocusCompanionService CompanionService { get => companionService; private set => companionService = value ?? throw new ArgumentNullException("CompanionService cannot be Null!"); }

        public abstract string CommandName { get; }
        public abstract string AxisParameterName { get; }
        public abstract string ButtonParameterName { get; }

        /// <summary>
        /// "Max speed" (axis) and "Speed" (button): whole percentages, 0..100, of the axis's full range.
        /// </summary>
        public IEnumerable<CommandValueOption> Options => percentOptions;
        private static readonly IReadOnlyList<CommandValueOption> percentOptions = Enumerable.Range(0, 101)
            .Select(i => new CommandValueOption($"{i} %", i))
            .ToArray();

        /// <summary>
        /// The VISCA speed steps "Max speed"/"Speed" were saved in before they became percentages.
        /// Used only to migrate saved settings; the camera maps speed fractions onto its own range.
        /// </summary>
        public abstract int LegacyStepMax { get; }
        public abstract int LegacyStepMin { get; }
        public abstract IEnumerable<CommandDirectionOption> ButtonDirections { get; }

        /// <summary>Runs at a signed percentage of the full speed: negative is <see cref="Direction.Low"/>, 0 stops.</summary>
        public void Execute(int value)
        {
            Direction direction;

            if (value < 0)
                direction = Direction.Low;
            else if (value > 0)
                direction = Direction.High;
            else
                direction = Direction.Stop;

            Execute(Math.Abs((long)value) / 100.0, direction);
        }

        public void Execute(CommandValueOption value)
        {
            Execute(value.Value);
        }

        /// <summary>
        /// Moves the selected camera at a speed fraction (0..1 of the axis's full range) in
        /// <paramref name="direction"/>, or stops it. A fraction outside 0..1 or not finite is
        /// clamped into 0..1 with a warning; it never throws.
        /// </summary>
        public void Execute(double speed, Direction direction)
        {
            if (!double.IsFinite(speed) || speed < 0 || speed > 1)
            {
                var clamped = double.IsNaN(speed) ? 0 : Math.Clamp(speed, 0, 1);
                Trace.TraceWarning($"{CommandName}: speed fraction {speed} is not between 0 and 1; using {clamped}");
                speed = clamped;
            }

            Move(speed, direction);
        }

        /// <summary>Moves or stops the selected camera; <paramref name="speed"/> is already within 0..1.</summary>
        protected abstract void Move(double speed, Direction direction);

        /// <summary>Runs at a "Speed" percentage option in a button direction.</summary>
        public void Execute(CommandValueOption value, CommandDirectionOption direction)
        {
            if (value != null!)
                Execute(value.Value / 100.0, direction.Direction);
        }
    }
}
