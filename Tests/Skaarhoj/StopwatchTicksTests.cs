using System.Diagnostics;
using PtzJoystickControl.SkaarhojPanel;
using Xunit;

namespace PtzJoystickControl.Tests.Skaarhoj;

public sealed class StopwatchTicksTests
{
    [Fact]
    public void OneSecond_IsStopwatchFrequencyTicks()
    {
        Assert.Equal(Stopwatch.Frequency, StopwatchTicks.From(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Milliseconds_ConvertExactly()
    {
        Assert.Equal(Stopwatch.Frequency / 4, StopwatchTicks.From(TimeSpan.FromMilliseconds(250)));
        Assert.Equal(Stopwatch.Frequency * 1125 / 1000, StopwatchTicks.From(TimeSpan.FromMilliseconds(1125)));
        Assert.Equal(0, StopwatchTicks.From(TimeSpan.Zero));
    }

    [Fact]
    public void LongSpans_DoNotOverflow()
    {
        Assert.Equal(Stopwatch.Frequency * 86400, StopwatchTicks.From(TimeSpan.FromDays(1)));
    }
}
