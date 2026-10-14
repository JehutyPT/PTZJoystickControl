using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;

namespace PtzJoystickControl.Application.Services;

/// <summary>
/// Joins several controller sources (USB joysticks, the SKAARHOJ panel) into one controller list.
/// Each source keeps its own typed collections; this one mirrors them in source order and sends
/// <see cref="ActivateGamepad"/>/<see cref="DeactivateGamepad"/> to the source that owns the
/// controller.
/// <para>
/// One controller at a time: activating one first deactivates any other that is activated, and if
/// several start activated (from saved settings) only the first is kept. A source activating a
/// controller on its own (one saved as active plugged in or coming back) asks first through
/// <see cref="InputQueue.MayActivate"/>: if another controller is activated, that one keeps control.
/// </para>
/// </summary>
public sealed class CompositeGamepadsService : IGamepadsService
{
    private readonly IGamepadsService[] _services;
    // The controllers lock every source changes its collections under (InputQueue.ControllersLock),
    // from the input loop and the UI thread. Held here whenever a source's collections or the merged
    // ones are read or changed. One lock for all, so taking it inside a source's change cannot deadlock.
    private readonly object _sync;

    public CompositeGamepadsService(InputQueue inputQueue, params IGamepadsService[] services)
    {
        _sync = inputQueue.ControllersLock;
        _services = services;
        lock (_sync)
        {
            inputQueue.ActivationRule = MayActivate;
            foreach (var service in _services)
            {
                service.Gamepads.CollectionChanged += OnSourceGamepadsChanged;
                service.ActiveGamepads.CollectionChanged += OnSourceActiveGamepadsChanged;
            }

            Sync(Gamepads, s => s.Gamepads);
            Sync(ActiveGamepads, s => s.ActiveGamepads);

            var activated = Gamepads.Where(g => g.IsActivated).ToList();
            foreach (var extra in activated.Skip(1))
            {
                Trace.TraceWarning($"Controllers {activated[0].Name} and {extra.Name} were both saved as active; keeping {activated[0].Name} only");
                DeactivateGamepad(extra);
            }
        }
    }

    public ObservableCollection<IGamepadInfo> Gamepads { get; } = new();
    public ObservableCollection<IGamepad> ActiveGamepads { get; } = new();

    public void ActivateGamepad(IGamepadInfo gamepad)
    {
        lock (_sync)
        {
            var owner = OwnerOf(gamepad);
            if (owner == null)
            {
                Trace.TraceWarning($"Controller {gamepad.Name} is not in the controller list; not activated");
                return;
            }

            // Never two controllers at once: stop the other one before this one can move anything.
            foreach (var other in Gamepads.Where(g => g.IsActivated && !ReferenceEquals(g, gamepad)).ToList())
                DeactivateGamepad(other);

            owner.ActivateGamepad(gamepad);
        }
    }

    public void DeactivateGamepad(IGamepadInfo gamepad)
    {
        lock (_sync)
            OwnerOf(gamepad)?.DeactivateGamepad(gamepad);
    }

    private bool MayActivate(IGamepadInfo gamepad)
    {
        lock (_sync)
        {
            var active = Gamepads.FirstOrDefault(g => g.IsActivated && !ReferenceEquals(g, gamepad));
            if (active == null)
                return true;
            Trace.TraceWarning($"Controller {gamepad.Name} was saved as active but {active.Name} is active; keeping {active.Name} only");
            return false;
        }
    }

    // Under _sync.
    private IGamepadsService? OwnerOf(IGamepadInfo gamepad) =>
        _services.FirstOrDefault(s => s.Gamepads.Contains(gamepad));

    private void OnSourceGamepadsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        lock (_sync)
            Sync(Gamepads, s => s.Gamepads);
    }

    private void OnSourceActiveGamepadsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        lock (_sync)
            Sync(ActiveGamepads, s => s.ActiveGamepads);
    }

    // Under _sync. Brings merged in line with the sources by single moves, inserts and removes, never
    // a reset, so a bound selection (the selected controller) is not lost.
    private void Sync<T>(ObservableCollection<T> merged, Func<IGamepadsService, IEnumerable<T>> select) where T : class
    {
        var target = _services.SelectMany(s => select(s).ToArray()).ToList();

        for (int i = merged.Count - 1; i >= 0; i--)
            if (!target.Contains(merged[i]))
                merged.RemoveAt(i);

        for (int i = 0; i < target.Count; i++)
        {
            if (i < merged.Count && ReferenceEquals(merged[i], target[i]))
                continue;
            var at = merged.IndexOf(target[i]);
            if (at >= 0)
                merged.Move(at, i);
            else
                merged.Insert(i, target[i]);
        }
    }
}
