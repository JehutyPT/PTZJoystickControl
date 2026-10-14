using System.Diagnostics;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Tests.Skaarhoj;
using Xunit;

namespace PtzJoystickControl.Tests.Db;

// SpeedProfiles.json holds the speed profiles and which one is active. Loading never throws: whatever
// is wrong with the file, the app starts (docs/live-safety.md §2).
public sealed class SpeedProfileSettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly TraceRecorder _trace = new();

    public SpeedProfileSettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PtzSpeedProfileSettingsStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "SpeedProfiles.json");
    }

    public void Dispose()
    {
        _trace.Dispose();
        try
        {
            File.SetAttributes(_dir, FileAttributes.Directory);
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
    }

    private SpeedProfileList Load(string? contents)
    {
        if (contents != null)
            File.WriteAllText(_path, contents);
        SpeedProfileList? profiles = null;
        var thrown = Record.Exception(() => profiles = new SpeedProfileSettingsStore(_path).GetSpeedProfiles());
        Assert.Null(thrown);
        return profiles!;
    }

    // Filtered to this test's file: other test classes log through the same global Trace in parallel.
    private IEnumerable<(TraceEventType Type, string Message)> LoggedAboutThisFile =>
        _trace.Events.Where(e => e.Message.Contains(_path));

    private static (string Name, int Pan, int Tilt, int Zoom, int Focus)[] Rows(SpeedProfileList profiles) =>
        profiles.Profiles.Select(p => (p.Name, p.Pan, p.Tilt, p.Zoom, p.Focus)).ToArray();

    private static readonly (string, int, int, int, int)[] DefaultRows =
    {
        ("Fine", 15, 15, 30, 40),
        ("Medium", 35, 35, 55, 70),
        ("Fast", 65, 65, 80, 100),
        ("Full", 100, 100, 100, 100),
    };

    [Fact]
    public void WithNoFile_TheDefaultProfilesAreUsed_WithFineActive_AndNothingIsWarnedOrWritten()
    {
        var profiles = Load(null);

        Assert.Equal(DefaultRows, Rows(profiles));
        Assert.Equal(1, profiles.ActivePosition);
        Assert.DoesNotContain(LoggedAboutThisFile, e => e.Type is TraceEventType.Warning or TraceEventType.Error);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void SavedProfilesAndTheActivePosition_LoadBack()
    {
        var saved = new SpeedProfileList(new[]
        {
            new SpeedProfile("Slow", 5, 6, 7, 8),
            new SpeedProfile("Quick", 90, 91, 92, 93),
        }, activePosition: 2);

        Assert.True(new SpeedProfileSettingsStore(_path).SaveSpeedProfiles(saved));
        var loaded = Load(null);

        Assert.Equal(new[] { ("Slow", 5, 6, 7, 8), ("Quick", 90, 91, 92, 93) }, Rows(loaded));
        Assert.Equal(2, loaded.ActivePosition);
        Assert.Equal(new[] { "SpeedProfiles.json" }, FolderEntries());
        Assert.DoesNotContain(LoggedAboutThisFile, e => e.Type is TraceEventType.Warning or TraceEventType.Error);
    }

    // JSON written with ' for " to keep it readable. The second profile is the bad one, so a usable
    // first profile cannot hide it.
    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{'ActivePosition':1,'Profiles':[]}")]
    [InlineData("{'ActivePosition':1,'Profiles':[{'Name':'Fine','Pan':1,'Tilt':2,'Zoom':3,'Focus':4},null]}")]
    [InlineData("{'ActivePosition':1,'Profiles':[{'Name':'Fine','Pan':1,'Tilt':2,'Zoom':3,'Focus':4},{'Pan':1,'Tilt':2,'Zoom':3,'Focus':4}]}")]
    [InlineData("{'ActivePosition':1,'Profiles':[{'Name':'Fine','Pan':1,'Tilt':2,'Zoom':3,'Focus':4},{'Name':' ','Pan':1,'Tilt':2,'Zoom':3,'Focus':4}]}")]
    [InlineData("{'ActivePosition':1,'Profiles':[{'Name':'Fine','Pan':1,'Tilt':2,'Zoom':3,'Focus':4},{'Name':'Fast','Pan':1,'Tilt':2,'Zoom':3}]}")]
    [InlineData("{'ActivePosition':1,'Profiles':[{'Name':'Fine','Pan':1,'Tilt':2,'Zoom':3,'Focus':4},{'Name':'Fast','Pan':15.5,'Tilt':2,'Zoom':3,'Focus':4}]}")]
    [InlineData("{'ActivePosition':1,'Profiles':[{'Name':'Fine','Pan':1,'Tilt':2,'Zoom':3,'Focus':4},{'Name':'Fast','Pan':'15','Tilt':2,'Zoom':3,'Focus':4}]}")]
    public void AFileThatCannotBeUsed_LogsAnErrorNamingTheFile_AndTheDefaultsAreUsed(string contents)
    {
        var profiles = Load(contents.Replace('\'', '"'));

        Assert.Equal(DefaultRows, Rows(profiles));
        Assert.Equal(1, profiles.ActivePosition);
        Assert.Contains(LoggedAboutThisFile, e => e.Type == TraceEventType.Error);
    }

    [Fact]
    public void AWholePercentageOutside1To100_IsClampedInto1To100_WithAWarningNamingTheFileAndTheProfile()
    {
        var profiles = Load("{'ActivePosition':2,'Profiles':[{'Name':'Fine','Pan':10,'Tilt':20,'Zoom':30,'Focus':40},{'Name':'Odd','Pan':0,'Tilt':-5,'Zoom':150,'Focus':100}]}".Replace('\'', '"'));

        Assert.Equal(new[] { ("Fine", 10, 20, 30, 40), ("Odd", 1, 1, 100, 100) }, Rows(profiles));
        Assert.Equal(2, profiles.ActivePosition);
        Assert.Contains(LoggedAboutThisFile, e => e.Type == TraceEventType.Warning && e.Message.Contains("Odd"));
        Assert.DoesNotContain(LoggedAboutThisFile, e => e.Type == TraceEventType.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("'ActivePosition':null,")]
    [InlineData("'ActivePosition':0,")]
    [InlineData("'ActivePosition':-1,")]
    [InlineData("'ActivePosition':3,")]
    public void AMissingActivePosition_OrOneOutsideTheList_MakesPosition1Active_WithAWarningNamingTheFile(string activePosition)
    {
        var profiles = Load(("{" + activePosition + "'Profiles':[{'Name':'Slow','Pan':10,'Tilt':20,'Zoom':30,'Focus':40},{'Name':'Quick','Pan':90,'Tilt':90,'Zoom':90,'Focus':90}]}").Replace('\'', '"'));

        Assert.Equal(new[] { ("Slow", 10, 20, 30, 40), ("Quick", 90, 90, 90, 90) }, Rows(profiles));
        Assert.Equal(1, profiles.ActivePosition);
        Assert.Contains(LoggedAboutThisFile, e => e.Type == TraceEventType.Warning);
        Assert.DoesNotContain(LoggedAboutThisFile, e => e.Type == TraceEventType.Error);
    }

    // Removing write permission from the folder makes creating the temp file fail: a save that dies
    // before the replace step. Windows ignores the read-only attribute on folders, so it is skipped there.
    [Fact]
    public void ASaveThatFails_ReturnsFalse_LogsAnErrorNamingTheFile_AndKeepsThePreviousFile()
    {
        if (OperatingSystem.IsWindows()) return;
        const string previous = "previous speed profiles that must survive a failed save";
        File.WriteAllText(_path, previous);
        File.SetAttributes(_dir, FileAttributes.Directory | FileAttributes.ReadOnly);

        var saved = new SpeedProfileSettingsStore(_path).SaveSpeedProfiles(SpeedProfileList.Defaults);

        Assert.False(saved);
        Assert.Equal(previous, File.ReadAllText(_path));
        Assert.Equal(new[] { "SpeedProfiles.json" }, FolderEntries());
        Assert.Contains(LoggedAboutThisFile, e => e.Type == TraceEventType.Error);
    }

    private string[] FolderEntries() =>
        Directory.EnumerateFileSystemEntries(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;
}
