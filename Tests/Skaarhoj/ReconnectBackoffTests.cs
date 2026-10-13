using PtzJoystickControl.SkaarhojPanel;
using Xunit;

namespace PtzJoystickControl.Tests.Skaarhoj;

public class ReconnectBackoffTests
{
    [Fact]
    public void Delay_DoublesFromTheInitialDelay_AndIsCappedAtTheMaximum()
    {
        var initial = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(30);

        var delays = Enumerable.Range(1, 9).Select(attempt => ReconnectBackoff.Delay(attempt, initial, max));

        Assert.Equal(new[] { 1.0, 2, 4, 8, 16, 30, 30, 30, 30 }, delays.Select(d => d.TotalSeconds).ToArray());
        Assert.Equal(max, ReconnectBackoff.Delay(int.MaxValue, initial, max));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Delay_BeforeTheFirstAttempt_IsTheInitialDelay(int attempt)
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ReconnectBackoff.Delay(attempt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Failures_AreLoggedOnlyOccasionally()
    {
        var logged = Enumerable.Range(1, 200).Where(ReconnectBackoff.ShouldLog).ToArray();

        Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 64, 128 }, logged);
    }
}
