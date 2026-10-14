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

    [Fact]
    public void ACheckMoreThan250msLate_IsAFreeze_AndTheFirstRestartsTheCountFromIt()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1100));
        Assert.False(OnTimeCheck(deadMan, 1500).Drop);

        // Scheduled for 2100, run at 2400: 1300 ms after the last line, but the app froze, not the panel.
        Assert.False(deadMan.Check(At(2400), At(2100)).Drop);

        Assert.False(OnTimeCheck(deadMan, 3400).Drop);
        var verdict = OnTimeCheck(deadMan, 3525);
        Assert.True(verdict.Drop);
        // The reason still gives the panel's real silence, since its last line.
        Assert.Equal("the panel was silent for 2425 ms (timeout 1000 ms)", verdict.DropReason);
    }

    [Fact]
    public void ACheck250msLateOrLess_IsNoFreeze()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1100));

        Assert.True(deadMan.Check(At(2350), At(2100)).Drop);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(251, true)]
    public void TheFreezeThreshold_Stays250ms_WithShortenedTimings(long lateMs, bool freeze)
    {
        // The link tests' timings: 20 ms ping, 100 ms timeout, 250 ms grace.
        var deadMan = new PanelDeadMan(Endpoint, connectedAt: At(0),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));
        deadMan.LineHeard(At(300));

        // Past the 100 ms timeout either way: only a freeze restarts the count and keeps the link.
        var verdict = deadMan.Check(At(300 + lateMs), At(300));

        Assert.Equal(freeze, verdict.FreezeWarning != null);
        Assert.Equal(!freeze, verdict.Drop);
    }

    [Fact]
    public void ASecondFreeze_WithNothingHeardSinceTheFirst_DoesNotRestartTheCount()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1100));
        Assert.False(deadMan.Check(At(2400), At(2100)).Drop);
        Assert.False(OnTimeCheck(deadMan, 3000).Drop);

        // 1100 ms after the forgiven freeze at 2400: this freeze is not forgiven, so the count runs out.
        Assert.True(deadMan.Check(At(3500), At(3200)).Drop);
    }

    [Fact]
    public void AFreezeAfterThePanelWasHeardAgain_RestartsTheCountAgain()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1100));
        Assert.False(deadMan.Check(At(2400), At(2100)).Drop);
        deadMan.LineHeard(At(2410));
        Assert.False(OnTimeCheck(deadMan, 3000).Drop);

        // 1290 ms after the last line, but the panel answered since the last forgiven freeze.
        Assert.False(deadMan.Check(At(3700), At(3400)).Drop);

        Assert.False(OnTimeCheck(deadMan, 4700).Drop);
        Assert.True(OnTimeCheck(deadMan, 4825).Drop);
    }

    [Fact]
    public void WithThePanelGone_AndEveryCheckLate_DropsAtTheFirstCheckMoreThan1sAfterTheLastForgivenFreeze()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1100));

        // Every check runs 300 ms late, so they land 425 ms apart. Only the first freeze is forgiven.
        var verdicts = new[]
        {
            deadMan.Check(At(1500), At(1200)),
            deadMan.Check(At(1925), At(1625)),
            deadMan.Check(At(2350), At(2050)),
            deadMan.Check(At(2775), At(2475)),
        };

        Assert.Equal(new[] { false, false, false, true }, verdicts.Select(v => v.Drop));
        // One run of late checks: one warning, at its first check.
        Assert.Equal(new[] { true, false, false, false }, verdicts.Select(v => v.FreezeWarning != null));
    }

    [Fact]
    public void OnlyTheFirstLateCheckInARun_GivesAWarning_UntilAnOnTimeCheckEndsTheRun()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1100));
        Assert.Null(OnTimeCheck(deadMan, 1500).FreezeWarning);

        Assert.Equal(
            "SKAARHOJ panel: the app stalled, not the panel at 10.0.0.5:9923: a dead-man check ran 375 ms late",
            deadMan.Check(At(2000), At(1625)).FreezeWarning);
        deadMan.LineHeard(At(2010));
        Assert.Null(deadMan.Check(At(2400), At(2125)).FreezeWarning);
        Assert.Null(deadMan.Check(At(2800), At(2525)).FreezeWarning);

        // An on-time check ends the run; the next late check starts a new one.
        Assert.Null(OnTimeCheck(deadMan, 2925).FreezeWarning);
        Assert.Equal(
            "SKAARHOJ panel: the app stalled, not the panel at 10.0.0.5:9923: a dead-man check ran 1400 ms late",
            deadMan.Check(At(4450), At(3050)).FreezeWarning);
    }
}
