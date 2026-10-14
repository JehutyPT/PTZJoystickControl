using Xunit;

namespace PtzJoystickControl.Tests;

// Tests that assert wall-clock timings of thread-pool work (the panel link's ping timeout, stop
// repeat spreads). Run on their own: in a parallel run, tests that block pool threads starve the
// timers and socket reads under test for 100–200 ms, far past the margins these tests assert.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingBoundCollection
{
    public const string Name = "Timing-bound";
}
