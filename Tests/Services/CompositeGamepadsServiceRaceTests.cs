using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Services;

// Two threads contend for the controllers lock flat out. Run on their own, so they cannot slow the
// timing-bound tests (stop repeats, the panel link) running in parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ContendedCollection
{
    public const string Name = "Contended controller lists";
}

[Collection(ContendedCollection.Name)]
public class CompositeGamepadsServiceRaceTests
{
    [Fact]
    public void ActivatingFromTheUi_WhileTheInputLoopChangesAnotherSource_NeverThrows_AndStaysInLine()
    {
        // The UI thread selects the panel on and off while the input loop plugs and unplugs a
        // joystick: each change makes the merged list read every source.
        var queue = new InputQueue();
        var usb = new CompositeGamepadsServiceTests.FakeGamepadsService("usb", queue);
        var panelSource = new CompositeGamepadsServiceTests.FakeGamepadsService("panel", queue);
        var panel = panelSource.AddInfo("skaarhoj:1");
        var composite = new CompositeGamepadsService(queue, usb, panelSource);
        const int rounds = 10000;
        using var start = new Barrier(2);

        var ui = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < rounds; i++)
            {
                composite.ActivateGamepad(panel);
                composite.DeactivateGamepad(panel);
            }
        });
        var inputLoop = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < rounds; i++)
                usb.RemoveInfo(usb.AddInfo($"joystick {i}"));
        });

        Assert.Null(Record.Exception(() => Task.WaitAll(ui, inputLoop)));
        Assert.Equal(new IGamepadInfo[] { panel }, composite.Gamepads);
        Assert.Empty(composite.ActiveGamepads);
    }
}
