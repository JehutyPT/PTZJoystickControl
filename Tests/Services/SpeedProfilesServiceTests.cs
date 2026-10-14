using System.Diagnostics;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Services;

// The one global list of speed profiles: stepping, choosing the active one and editing the list.
// Every change is saved and announced; a request that changes nothing does neither.
public sealed class SpeedProfilesServiceTests
{
    private MemorySpeedProfileStore _store = null!;
    private int _changes;

    // Four profiles, finest first, each with its own percentages.
    private SpeedProfilesService Start(int activePosition) => Start(new SpeedProfileList(new[]
    {
        new SpeedProfile("A", 10, 11, 12, 13),
        new SpeedProfile("B", 20, 21, 22, 23),
        new SpeedProfile("C", 30, 31, 32, 33),
        new SpeedProfile("D", 40, 41, 42, 43),
    }, activePosition));

    private SpeedProfilesService Start(SpeedProfileList saved)
    {
        _store = new MemorySpeedProfileStore(saved);
        var service = new SpeedProfilesService(_store);
        service.ProfilesChanged += (_, _) => _changes++;
        return service;
    }

    private static string[] Names(SpeedProfileList profiles) => profiles.Profiles.Select(p => p.Name).ToArray();

    private void AssertSavedAndAnnouncedOnce(SpeedProfilesService service)
    {
        Assert.Same(service.Profiles, _store.Saves.Single());
        Assert.Equal(1, _changes);
    }

    private void AssertNothingSavedOrAnnounced()
    {
        Assert.Empty(_store.Saves);
        Assert.Equal(0, _changes);
    }

    [Theory]
    [InlineData(1, "B")]
    [InlineData(3, "D")]
    public void StepFaster_MakesTheNextFasterProfileActive(int from, string active)
    {
        var service = Start(activePosition: from);

        service.StepFaster();

        Assert.Equal(from + 1, service.Profiles.ActivePosition);
        Assert.Equal(active, service.Profiles.Active.Name);
        AssertSavedAndAnnouncedOnce(service);
    }

    [Fact]
    public void StepFaster_AtTheFastest_StaysThere_WithoutWrapping()
    {
        var service = Start(activePosition: 4);

        service.StepFaster();

        Assert.Equal(4, service.Profiles.ActivePosition);
        AssertNothingSavedOrAnnounced();
    }

    [Theory]
    [InlineData(4, "C")]
    [InlineData(2, "A")]
    public void StepFiner_MakesTheNextFinerProfileActive(int from, string active)
    {
        var service = Start(activePosition: from);

        service.StepFiner();

        Assert.Equal(from - 1, service.Profiles.ActivePosition);
        Assert.Equal(active, service.Profiles.Active.Name);
        AssertSavedAndAnnouncedOnce(service);
    }

    [Fact]
    public void StepFiner_AtTheFinest_StaysThere_WithoutWrapping()
    {
        var service = Start(activePosition: 1);

        service.StepFiner();

        Assert.Equal(1, service.Profiles.ActivePosition);
        AssertNothingSavedOrAnnounced();
    }

    [Fact]
    public void SetActive_MakesThatProfileActive()
    {
        var service = Start(activePosition: 1);

        service.SetActive(3);

        Assert.Equal(3, service.Profiles.ActivePosition);
        Assert.Equal("C", service.Profiles.Active.Name);
        AssertSavedAndAnnouncedOnce(service);
    }

    [Fact]
    public void SetActive_OnTheActiveProfile_ChangesNothing()
    {
        var service = Start(activePosition: 2);

        service.SetActive(2);

        Assert.Equal(2, service.Profiles.ActivePosition);
        AssertNothingSavedOrAnnounced();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void SetActive_OutsideTheList_IsALoggedNoOp(int position)
    {
        var service = Start(activePosition: 2);
        using var trace = new TraceRecorder();

        var thrown = Record.Exception(() => service.SetActive(position));

        Assert.Null(thrown);
        Assert.Equal(2, service.Profiles.ActivePosition);
        AssertNothingSavedOrAnnounced();
        Assert.Contains(trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains($"make speed profile {position} active"));
    }

    [Fact]
    public void Add_AppendsProfileN_At100PercentOnEveryAxis_AndKeepsTheActiveProfile()
    {
        var service = Start(activePosition: 2);

        service.Add();

        Assert.Equal(new[] { "A", "B", "C", "D", "Profile 5" }, Names(service.Profiles));
        Assert.Equal(new SpeedProfile("Profile 5", 100, 100, 100, 100), service.Profiles.Profiles[4]);
        Assert.Equal(2, service.Profiles.ActivePosition);
        Assert.Equal("B", service.Profiles.Active.Name);
        AssertSavedAndAnnouncedOnce(service);
    }

    [Fact]
    public void Edit_RenamesThatProfile_AndChangesItsPercentages_AndKeepsTheActiveProfile()
    {
        var service = Start(activePosition: 3);

        service.Edit(3, new SpeedProfile("Wide", 1, 2, 99, 100));

        Assert.Equal(new[]
        {
            new SpeedProfile("A", 10, 11, 12, 13),
            new SpeedProfile("B", 20, 21, 22, 23),
            new SpeedProfile("Wide", 1, 2, 99, 100),
            new SpeedProfile("D", 40, 41, 42, 43),
        }, service.Profiles.Profiles);
        Assert.Equal(3, service.Profiles.ActivePosition);
        AssertSavedAndAnnouncedOnce(service);
    }

    [Theory]
    [InlineData(0, "Wide", 50, 50, 50, 50)]
    [InlineData(5, "Wide", 50, 50, 50, 50)]
    [InlineData(3, "", 50, 50, 50, 50)]
    [InlineData(3, "   ", 50, 50, 50, 50)]
    [InlineData(3, null, 50, 50, 50, 50)]
    [InlineData(3, "Wide", 0, 50, 50, 50)]
    [InlineData(3, "Wide", 50, 101, 50, 50)]
    [InlineData(3, "Wide", 50, 50, -1, 50)]
    [InlineData(3, "Wide", 50, 50, 50, 101)]
    public void Edit_ThatIsInvalid_IsALoggedNoOp(int position, string? name, int pan, int tilt, int zoom, int focus)
    {
        var service = Start(activePosition: 2);
        var before = service.Profiles;
        using var trace = new TraceRecorder();

        var thrown = Record.Exception(() => service.Edit(position, new SpeedProfile(name!, pan, tilt, zoom, focus)));

        Assert.Null(thrown);
        Assert.Same(before, service.Profiles);
        AssertNothingSavedOrAnnounced();
        Assert.Contains(trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains($"edit speed profile {position}"));
    }

    [Fact]
    public void Edit_WithNoProfile_IsALoggedNoOp()
    {
        var service = Start(activePosition: 2);
        var before = service.Profiles;
        using var trace = new TraceRecorder();

        var thrown = Record.Exception(() => service.Edit(3, null!));

        Assert.Null(thrown);
        Assert.Same(before, service.Profiles);
        AssertNothingSavedOrAnnounced();
        Assert.Contains(trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains("edit speed profile 3"));
    }

    [Fact]
    public void Edit_ThatChangesNothing_IsNotSavedOrAnnounced()
    {
        var service = Start(activePosition: 2);

        service.Edit(3, new SpeedProfile("C", 30, 31, 32, 33));

        Assert.Equal("C", service.Profiles.Profiles[2].Name);
        AssertNothingSavedOrAnnounced();
    }

    // MoveUp(3) swaps C with B above it; whichever profile was active stays active, wherever it now is.
    [Theory]
    [InlineData(1, 1, "A")]
    [InlineData(2, 3, "B")]
    [InlineData(3, 2, "C")]
    [InlineData(4, 4, "D")]
    public void MoveUp_SwapsTheProfileWithTheOneAbove_AndTheSameProfileStaysActive(int active, int activeAfter, string activeName)
    {
        var service = Start(activePosition: active);

        service.MoveUp(3);

        Assert.Equal(new[] { "A", "C", "B", "D" }, Names(service.Profiles));
        Assert.Equal(activeAfter, service.Profiles.ActivePosition);
        Assert.Equal(activeName, service.Profiles.Active.Name);
        AssertSavedAndAnnouncedOnce(service);
    }

    // MoveDown(2) swaps B with C below it; whichever profile was active stays active, wherever it now is.
    [Theory]
    [InlineData(1, 1, "A")]
    [InlineData(2, 3, "B")]
    [InlineData(3, 2, "C")]
    [InlineData(4, 4, "D")]
    public void MoveDown_SwapsTheProfileWithTheOneBelow_AndTheSameProfileStaysActive(int active, int activeAfter, string activeName)
    {
        var service = Start(activePosition: active);

        service.MoveDown(2);

        Assert.Equal(new[] { "A", "C", "B", "D" }, Names(service.Profiles));
        Assert.Equal(activeAfter, service.Profiles.ActivePosition);
        Assert.Equal(activeName, service.Profiles.Active.Name);
        AssertSavedAndAnnouncedOnce(service);
    }

    [Theory]
    [InlineData("up", 1)]
    [InlineData("down", 4)]
    [InlineData("up", 0)]
    [InlineData("up", 5)]
    [InlineData("down", 0)]
    [InlineData("down", 5)]
    [InlineData("up", int.MinValue)]
    [InlineData("down", int.MaxValue)]
    public void AMove_PastEitherEndOrOutsideTheList_IsALoggedNoOp(string direction, int position)
    {
        var service = Start(activePosition: 2);
        var before = service.Profiles;
        using var trace = new TraceRecorder();

        var thrown = Record.Exception(() =>
        {
            if (direction == "up")
                service.MoveUp(position);
            else
                service.MoveDown(position);
        });

        Assert.Null(thrown);
        Assert.Same(before, service.Profiles);
        AssertNothingSavedOrAnnounced();
        Assert.Contains(trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains($"move speed profile {position} {direction}"));
    }

    [Theory]
    [InlineData(3, 1, "B C D", 2, "C")]
    [InlineData(2, 4, "A B C", 2, "B")]
    [InlineData(1, 3, "A B D", 1, "A")]
    public void Remove_AnotherProfile_KeepsTheSameProfileActive(int active, int removed, string namesAfter, int activeAfter, string activeName)
    {
        var service = Start(activePosition: active);

        service.Remove(removed);

        Assert.Equal(namesAfter.Split(' '), Names(service.Profiles));
        Assert.Equal(activeAfter, service.Profiles.ActivePosition);
        Assert.Equal(activeName, service.Profiles.Active.Name);
        AssertSavedAndAnnouncedOnce(service);
    }

    [Theory]
    [InlineData(3, "A B D", "A")]
    [InlineData(4, "A B C", "A")]
    [InlineData(1, "B C D", "B")]
    public void Remove_TheActiveProfile_MakesPosition1Active(int active, string namesAfter, string activeName)
    {
        var service = Start(activePosition: active);

        service.Remove(active);

        Assert.Equal(namesAfter.Split(' '), Names(service.Profiles));
        Assert.Equal(1, service.Profiles.ActivePosition);
        Assert.Equal(activeName, service.Profiles.Active.Name);
        AssertSavedAndAnnouncedOnce(service);
    }

    [Fact]
    public void Remove_TheOnlyProfile_IsALoggedNoOp_SoTheListAlwaysKeepsOne()
    {
        var service = Start(new SpeedProfileList(new[] { new SpeedProfile("Only", 50, 50, 50, 50) }, 1));
        var before = service.Profiles;
        using var trace = new TraceRecorder();

        var thrown = Record.Exception(() => service.Remove(1));

        Assert.Null(thrown);
        Assert.Same(before, service.Profiles);
        AssertNothingSavedOrAnnounced();
        Assert.Contains(trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains("remove speed profile 1"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Remove_OutsideTheList_IsALoggedNoOp(int position)
    {
        var service = Start(activePosition: 2);
        var before = service.Profiles;
        using var trace = new TraceRecorder();

        var thrown = Record.Exception(() => service.Remove(position));

        Assert.Null(thrown);
        Assert.Same(before, service.Profiles);
        AssertNothingSavedOrAnnounced();
        Assert.Contains(trace.Events, e => e.Type == TraceEventType.Warning && e.Message.Contains($"remove speed profile {position}"));
    }

    // A failing listener (a window, later Companion) must not stop the operator's change or reach the caller.
    [Fact]
    public void ASubscriberThatThrows_DoesNotReachTheCaller_AndTheChangeStillHolds_WithAnError()
    {
        var service = Start(activePosition: 1);
        service.ProfilesChanged += (_, _) => throw new InvalidOperationException("listener broke");
        using var trace = new TraceRecorder();

        var thrown = Record.Exception(() => service.StepFaster());

        Assert.Null(thrown);
        Assert.Equal("B", service.Profiles.Active.Name);
        AssertSavedAndAnnouncedOnce(service);
        Assert.Contains(trace.Events, e => e.Type == TraceEventType.Error && e.Message.Contains("listener broke"));
    }

    // The input loop reads the active profile for every movement while the window (or, with #46, Companion)
    // changes it. Every profile here is named after the one percentage it has on all four axes, so a read that
    // mixed two profiles would show.
    [Fact]
    public void TheActiveProfile_ReadOnAnotherThreadWhileItIsSteppedAndEdited_IsAlwaysOneWholeProfile()
    {
        const int minChanges = 5_000;
        const int minReads = 5_000;
        var service = Start(new SpeedProfileList(new[] { 10, 20, 30, 40 }.Select(Uniform), 1));
        var reads = 0;
        var mixed = 0;
        var changing = true;
        Exception? failure = null;

        var reader = new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref changing))
                {
                    var active = service.Profiles.Active;
                    if (active.Name != $"V{active.Pan}" || active.Tilt != active.Pan || active.Zoom != active.Pan || active.Focus != active.Pan)
                        Interlocked.Increment(ref mixed);
                    Interlocked.Increment(ref reads);
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        }) { IsBackground = true };

        // Keeps changing until the reader has read often enough while the changes were going on.
        var writer = new Thread(() =>
        {
            try
            {
                var faster = true;
                for (var changes = 0; changes < minChanges || Volatile.Read(ref reads) < minReads; changes++)
                {
                    var profiles = service.Profiles;
                    if (profiles.ActivePosition == profiles.Count)
                        faster = false;
                    else if (profiles.ActivePosition == 1)
                        faster = true;

                    if (faster)
                        service.StepFaster();
                    else
                        service.StepFiner();
                    service.Edit(service.Profiles.ActivePosition, Uniform(changes % SpeedProfile.MaxPercent + 1));
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                Volatile.Write(ref changing, false);
            }
        }) { IsBackground = true };

        reader.Start();
        writer.Start();

        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "the changes did not finish");
        Assert.True(reader.Join(TimeSpan.FromSeconds(30)), "the reader did not finish");
        Assert.Null(failure);
        Assert.Equal(0, mixed);
        Assert.True(reads >= minReads, $"only {reads} reads");
    }

    private static SpeedProfile Uniform(int percent) => new($"V{percent}", percent, percent, percent, percent);
}
