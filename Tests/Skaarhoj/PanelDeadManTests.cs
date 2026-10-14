using System.Diagnostics;
using PtzJoystickControl.SkaarhojPanel;
using Xunit;

namespace PtzJoystickControl.Tests.Skaarhoj;

// The link's dead-man decisions, fed explicit timestamps (ms after connect) with the link's default
// timings: 500 ms ping, 1 s timeout, 1 s connect and wake grace.
public sealed class PanelDeadManTests
{
    private const string Endpoint = "10.0.0.5:9923";

    private static long At(long ms) => ms * Stopwatch.Frequency / 1000;

    private static PanelDeadMan Connected() => new(
        Endpoint,
        connectedAt: At(0),
        SkaarhojPanelLink.DefaultPingInterval,
        SkaarhojPanelLink.DefaultSilenceTimeout,
        SkaarhojPanelLink.DefaultConnectGrace);

    [Fact]
    public void FirstPing_IsDue500msAfterConnect_WhenNothingIsHeard()
    {
        var deadMan = Connected();

        Assert.Equal(At(500), deadMan.NextPingDue);
    }

    [Fact]
    public void AfterAPing_TheNextIsDue500msLater_WhenNothingIsHeard()
    {
        var deadMan = Connected();

        deadMan.PingSent(At(500));

        Assert.Equal(At(1000), deadMan.NextPingDue);
    }

    [Fact]
    public void EveryLineHeard_PutsTheNextPing500msAfterIt()
    {
        var deadMan = Connected();
        deadMan.PingSent(At(500));

        // A moving stick: lines closer together than the ping interval keep pushing the ping back.
        foreach (var ms in new long[] { 520, 570, 620, 900, 1350 })
        {
            deadMan.LineHeard(At(ms));
            Assert.Equal(At(ms + 500), deadMan.NextPingDue);
        }
    }

    [Fact]
    public void APingSentAfterTheLastLine_TimesTheNextPing()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(100));
        deadMan.PingSent(At(600));

        Assert.Equal(At(1100), deadMan.NextPingDue);
    }

    // Checks run every 125 ms; these land on that grid unless a test says otherwise.
    private static PanelDeadMan.Verdict OnTimeCheck(PanelDeadMan deadMan, long ms) => deadMan.Check(At(ms), At(ms));

    [Fact]
    public void ChecksRun_EveryQuarterOfThePingInterval()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(125), Connected().CheckInterval);
    }

    [Fact]
    public void AfterTheGrace_DropsOnlyOnceThePanelHasBeenSilentForMoreThan1s_AndSaysForHowLong()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1200));

        Assert.False(OnTimeCheck(deadMan, 2200).Drop);
        var verdict = OnTimeCheck(deadMan, 2325);

        Assert.True(verdict.Drop);
        Assert.Equal("the panel was silent for 1125 ms (timeout 1000 ms)", verdict.DropReason);
    }

    [Fact]
    public void WithNothingHeard_DropsOnlyAfterTheGracePlus1s_AndSaysSinceConnecting()
    {
        var deadMan = Connected();

        Assert.False(OnTimeCheck(deadMan, 1500).Drop);
        Assert.False(OnTimeCheck(deadMan, 2000).Drop);
        var verdict = OnTimeCheck(deadMan, 2125);

        Assert.True(verdict.Drop);
        Assert.Equal("the panel was silent for 2125 ms since connecting (timeout 1000 ms after the 1000 ms connect grace)", verdict.DropReason);
        Assert.False(deadMan.HasHeard);
    }

    [Fact]
    public void ALineHeardDuringTheGrace_DoesNotStartTheCountBeforeTheGraceEnds()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(100));

        Assert.False(OnTimeCheck(deadMan, 2000).Drop);
        var verdict = OnTimeCheck(deadMan, 2125);

        Assert.True(verdict.Drop);
        Assert.Equal("the panel was silent for 2025 ms (timeout 1000 ms)", verdict.DropReason);
        Assert.True(deadMan.HasHeard);
    }

    [Fact]
    public void AWake_RearmsTheGrace_SoTheCountStartsNoEarlierThan1sAfterIt()
    {
        var deadMan = Connected();
        // _isSleeping=0 is itself a line heard.
        deadMan.LineHeard(At(1500));
        deadMan.Woke(At(1500));

        // Without the wake, 1100 ms after the last line would drop.
        Assert.False(OnTimeCheck(deadMan, 2600).Drop);
        Assert.False(OnTimeCheck(deadMan, 3500).Drop);
        Assert.True(OnTimeCheck(deadMan, 3625).Drop);
    }
}
