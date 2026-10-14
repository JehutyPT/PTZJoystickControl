using PtzJoystickControl.SkaarhojPanel;
using Xunit;

namespace PtzJoystickControl.Tests.Skaarhoj;

// The link's dead-man decisions, fed explicit timestamps (ms after connect) with the link's default
// timings: 500 ms ping, 1 s timeout, 1 s connect and wake grace.
public sealed class PanelDeadManTests
{
    private static long At(long ms) => StopwatchTicks.From(TimeSpan.FromMilliseconds(ms));

    // Whole milliseconds, as the link logs them.
    private static long Ms(TimeSpan? span) => (long)Math.Round(span!.Value.TotalMilliseconds);

    private static PanelDeadMan Connected() => new(
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

        Assert.Null(OnTimeCheck(deadMan, 2200).Silence);
        var verdict = OnTimeCheck(deadMan, 2325);

        Assert.NotNull(verdict.Silence);
        Assert.Equal(1125, Ms(verdict.Silence));
        Assert.False(verdict.NothingHeard);
    }

    [Fact]
    public void WithNothingHeard_DropsOnlyAfterTheGracePlus1s_AndSaysSinceConnecting()
    {
        var deadMan = Connected();

        Assert.Null(OnTimeCheck(deadMan, 1500).Silence);
        Assert.Null(OnTimeCheck(deadMan, 2000).Silence);
        var verdict = OnTimeCheck(deadMan, 2125);

        Assert.NotNull(verdict.Silence);
        Assert.Equal(2125, Ms(verdict.Silence));
        Assert.True(verdict.NothingHeard);
    }

    [Fact]
    public void ALineHeardDuringTheGrace_DoesNotStartTheCountBeforeTheGraceEnds()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(100));

        Assert.Null(OnTimeCheck(deadMan, 2000).Silence);
        var verdict = OnTimeCheck(deadMan, 2125);

        Assert.NotNull(verdict.Silence);
        Assert.Equal(2025, Ms(verdict.Silence));
        Assert.False(verdict.NothingHeard);
    }

    [Fact]
    public void AWake_RearmsTheGrace_SoTheCountStartsNoEarlierThan1sAfterIt()
    {
        var deadMan = Connected();
        // _isSleeping=0 is itself a line heard.
        deadMan.LineHeard(At(1500));
        deadMan.Woke(At(1500));

        // Without the wake, 1100 ms after the last line would drop.
        Assert.Null(OnTimeCheck(deadMan, 2600).Silence);
        Assert.Null(OnTimeCheck(deadMan, 3500).Silence);
        Assert.NotNull(OnTimeCheck(deadMan, 3625).Silence);
    }

    [Fact]
    public void ACheckMoreThan250msLate_IsAFreeze_AndTheFirstRestartsTheCountFromIt()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1100));
        Assert.Null(OnTimeCheck(deadMan, 1500).Silence);

        // Scheduled for 2100, run at 2400: 1300 ms after the last line, but the app froze, not the panel.
        Assert.Null(deadMan.Check(At(2400), At(2100)).Silence);

        Assert.Null(OnTimeCheck(deadMan, 3400).Silence);
        var verdict = OnTimeCheck(deadMan, 3525);
        Assert.NotNull(verdict.Silence);
        // The reason still gives the panel's real silence, since its last line.
        Assert.Equal(2425, Ms(verdict.Silence));
    }

    [Fact]
    public void ACheck250msLateOrLess_IsNoFreeze()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1100));

        Assert.NotNull(deadMan.Check(At(2350), At(2100)).Silence);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(251, true)]
    public void TheFreezeThreshold_Stays250ms_WithShortenedTimings(long lateMs, bool freeze)
    {
        // The link tests' timings: 20 ms ping, 100 ms timeout, 250 ms grace.
        var deadMan = new PanelDeadMan(connectedAt: At(0),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));
        deadMan.LineHeard(At(300));

        // Past the 100 ms timeout either way: only a freeze restarts the count and keeps the link.
        var verdict = deadMan.Check(At(300 + lateMs), At(300));

        Assert.Equal(freeze, verdict.Stall != null);
        Assert.Equal(!freeze, verdict.Silence != null);
    }

    [Fact]
    public void ASecondFreeze_WithNothingHeardSinceTheFirst_DoesNotRestartTheCount()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1100));
        Assert.Null(deadMan.Check(At(2400), At(2100)).Silence);
        Assert.Null(OnTimeCheck(deadMan, 3000).Silence);

        // 1100 ms after the forgiven freeze at 2400: this freeze is not forgiven, so the count runs out.
        Assert.NotNull(deadMan.Check(At(3500), At(3200)).Silence);
    }

    [Fact]
    public void AFreezeAfterThePanelWasHeardAgain_RestartsTheCountAgain()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1100));
        Assert.Null(deadMan.Check(At(2400), At(2100)).Silence);
        deadMan.LineHeard(At(2410));
        Assert.Null(OnTimeCheck(deadMan, 3000).Silence);

        // 1290 ms after the last line, but the panel answered since the last forgiven freeze.
        Assert.Null(deadMan.Check(At(3700), At(3400)).Silence);

        Assert.Null(OnTimeCheck(deadMan, 4700).Silence);
        Assert.NotNull(OnTimeCheck(deadMan, 4825).Silence);
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

        Assert.Equal(new[] { false, false, false, true }, verdicts.Select(v => v.Silence != null));
        // One run of late checks: one warning, at its first check.
        Assert.Equal(new[] { true, false, false, false }, verdicts.Select(v => v.Stall != null));
    }

    [Fact]
    public void OnlyTheFirstLateCheckInARun_GivesAWarning_UntilAnOnTimeCheckEndsTheRun()
    {
        var deadMan = Connected();
        deadMan.LineHeard(At(1100));
        Assert.Null(OnTimeCheck(deadMan, 1500).Stall);

        Assert.Equal(375, Ms(deadMan.Check(At(2000), At(1625)).Stall));
        deadMan.LineHeard(At(2010));
        Assert.Null(deadMan.Check(At(2400), At(2125)).Stall);
        Assert.Null(deadMan.Check(At(2800), At(2525)).Stall);

        // An on-time check ends the run; the next late check starts a new one.
        Assert.Null(OnTimeCheck(deadMan, 2925).Stall);
        Assert.Equal(1400, Ms(deadMan.Check(At(4450), At(3050)).Stall));
    }
}
