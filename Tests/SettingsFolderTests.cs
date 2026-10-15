using PtzJoystickControl.Application;
using Xunit;

namespace PtzJoystickControl.Tests;

public sealed class SettingsFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PtzSettingsFolderTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
    }

    // The documented folders (docs/development.md#logs), built from the environment rather than from
    // the code's own lookup. Never created or read: the default folder holds the real settings.
    private static string DocumentedDefaultFolder()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetEnvironmentVariable("APPDATA")!, "PTZJoystickControl");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetEnvironmentVariable("HOME")!, "Library", "Application Support", "PTZJoystickControl");
        return Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".PTZJoystickControl");
    }

    [Fact]
    public void Parse_NoSwitch_GivesTheDefaultFolderAndTheArgumentsUnchanged()
    {
        var resolution = SettingsFolder.Parse(new[] { "-m", "-psn_0_12345" });

        Assert.True(resolution.IsResolved);
        Assert.Equal(DocumentedDefaultFolder(), resolution.Path);
        Assert.False(resolution.FromSwitch);
        Assert.Equal(new[] { "-m", "-psn_0_12345" }, resolution.PassedOnArguments);
    }

    [Fact]
    public void Parse_NoArguments_GivesTheDefaultFolder()
    {
        var resolution = SettingsFolder.Parse(Array.Empty<string>());

        Assert.True(resolution.IsResolved);
        Assert.Equal(DocumentedDefaultFolder(), resolution.Path);
        Assert.False(resolution.FromSwitch);
        Assert.Empty(resolution.PassedOnArguments);
    }

    [Fact]
    public void Parse_SwitchAmongOtherArguments_TakesItsFolderAndPassesTheRestOnInOrder()
    {
        var folder = Path.Combine(_dir, "settings");

        var resolution = SettingsFolder.Parse(new[] { "-m", "--settings-dir", folder, "-psn_0_12345" });

        Assert.True(resolution.IsResolved);
        Assert.Null(resolution.Failure);
        Assert.Equal(folder, resolution.Path);
        Assert.True(resolution.FromSwitch);
        Assert.Equal(new[] { "-m", "-psn_0_12345" }, resolution.PassedOnArguments);
    }

    [Fact]
    public void Parse_SwitchAlone_TakesItsFolderAndPassesNothingOn()
    {
        var folder = Path.Combine(_dir, "settings");

        var resolution = SettingsFolder.Parse(new[] { "--settings-dir", folder });

        Assert.True(resolution.IsResolved);
        Assert.Equal(folder, resolution.Path);
        Assert.True(resolution.FromSwitch);
        Assert.Empty(resolution.PassedOnArguments);
    }

    [Fact]
    public void Parse_RelativeFolder_GivesItsFullPathAgainstTheCurrentDirectory()
    {
        var resolution = SettingsFolder.Parse(new[] { "--settings-dir", Path.Combine("relative", "settings") });

        Assert.True(resolution.IsResolved);
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "relative", "settings"), resolution.Path);
        Assert.True(resolution.FromSwitch);
    }

    [Theory]
    [InlineData(null)] // the switch is the last argument
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-m")]
    public void Parse_SwitchWithoutAFolder_FailsWithTheNeedsAFolderLine(string? next)
    {
        var args = next == null ? new[] { "-m", "--settings-dir" } : new[] { "--settings-dir", next };

        var resolution = SettingsFolder.Parse(args);

        Assert.False(resolution.IsResolved);
        Assert.Equal("PTZJoystickControl: cannot start: --settings-dir needs a folder, as in --settings-dir /path/to/folder",
            resolution.Failure);
    }

    [Theory]
    [InlineData("settings", "settings")]
    [InlineData("settings", "other")]
    public void Parse_SwitchTwice_FailsWithTheMoreThanOnceLine(string first, string second)
    {
        var args = new[] { "--settings-dir", Path.Combine(_dir, first), "-m", "--settings-dir", Path.Combine(_dir, second) };

        var resolution = SettingsFolder.Parse(args);

        Assert.False(resolution.IsResolved);
        Assert.Equal("PTZJoystickControl: cannot start: --settings-dir is given more than once", resolution.Failure);
    }

    [Theory]
    [InlineData("--settings-dir=settings", null)]
    [InlineData("--settings-folder", "settings")]
    [InlineData("--", null)]
    [InlineData("--Settings-Dir", "settings")] // matched exactly
    public void Parse_OtherDoubleDashArgument_FailsWithTheUnknownArgumentLineNamingIt(string unknown, string? next)
    {
        var args = next == null ? new[] { "-m", unknown } : new[] { "-m", unknown, next };

        var resolution = SettingsFolder.Parse(args);

        Assert.False(resolution.IsResolved);
        Assert.Equal($"PTZJoystickControl: cannot start: unknown argument '{unknown}'; to choose the settings folder, " +
            "give it as the next argument: --settings-dir <folder>", resolution.Failure);
    }

    [Fact]
    public void Parse_UnknownDoubleDashArgumentAfterTheSwitch_FailsNamingIt()
    {
        var args = new[] { "--settings-dir", Path.Combine(_dir, "settings"), "--minimized" };

        var resolution = SettingsFolder.Parse(args);

        Assert.False(resolution.IsResolved);
        Assert.Equal("PTZJoystickControl: cannot start: unknown argument '--minimized'; to choose the settings folder, " +
            "give it as the next argument: --settings-dir <folder>", resolution.Failure);
    }

    [Fact]
    public void Resolve_MissingFolderTwoLevelsDeep_CreatesItAndLeavesItEmpty()
    {
        var folder = Path.Combine(_dir, "missing", "settings");

        var resolution = SettingsFolder.Resolve(new[] { "--settings-dir", folder, "-m" });

        Assert.True(resolution.IsResolved);
        Assert.Equal(folder, resolution.Path);
        Assert.True(resolution.FromSwitch);
        Assert.Equal(new[] { "-m" }, resolution.PassedOnArguments);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Fact]
    public void Resolve_ExistingFolderWithFiles_AcceptsItAndLeavesItsFilesUnchanged()
    {
        var folder = Path.Combine(_dir, "settings");
        Directory.CreateDirectory(folder);
        var cameras = Path.Combine(folder, "Cameras.json");
        File.WriteAllText(cameras, "[]");
        var written = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(cameras, written);

        var resolution = SettingsFolder.Resolve(new[] { "--settings-dir", folder });

        Assert.True(resolution.IsResolved);
        Assert.Equal(folder, resolution.Path);
        Assert.Equal(new[] { "Cameras.json" }, Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
        Assert.Equal("[]", File.ReadAllText(cameras));
        Assert.Equal(written, File.GetLastWriteTimeUtc(cameras));
    }

    // The line for a folder that cannot be created or written to, up to the system's reason.
    private static string CannotBeUsedPrefix(string fullPath) =>
        $"PTZJoystickControl: cannot start: settings folder '{fullPath}' (--settings-dir) cannot be used: ";

    private static void AssertCannotBeUsed(string fullPath, SettingsFolderResolution resolution)
    {
        Assert.False(resolution.IsResolved);
        Assert.NotNull(resolution.Failure);
        var prefix = CannotBeUsedPrefix(fullPath);
        Assert.StartsWith(prefix, resolution.Failure);
        Assert.False(string.IsNullOrWhiteSpace(resolution.Failure![prefix.Length..]), "the line gives no reason");
        Assert.DoesNotContain('\n', resolution.Failure);
    }

    [Fact]
    public void Resolve_FolderInsideAFile_FailsWithTheCannotBeUsedLine()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "file");
        File.WriteAllText(file, "not a folder");
        var folder = Path.Combine(file, "settings");

        var resolution = SettingsFolder.Resolve(new[] { "--settings-dir", folder });

        AssertCannotBeUsed(folder, resolution);
    }

    [Fact]
    public void Resolve_FolderWithNoFullPath_FailsWithTheCannotBeUsedLineGivingItAsTyped()
    {
        // .NET rejects a NUL in any path on every OS, so the folder has no full path to give.
        var folder = Path.Combine(_dir, "bad\0folder");

        var resolution = SettingsFolder.Resolve(new[] { "--settings-dir", folder });

        AssertCannotBeUsed(folder, resolution);
    }

    [Fact]
    public void Resolve_FolderTheUserCannotWriteTo_FailsWithTheCannotBeUsedLine()
    {
        // Windows ignores the read-only attribute on folders, as in SettingsStoreAtomicWriteTests
        // (CI runs on Linux).
        if (OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(_dir, "read-only");
        Directory.CreateDirectory(folder);
        File.SetAttributes(folder, FileAttributes.Directory | FileAttributes.ReadOnly);
        SettingsFolderResolution resolution;
        try
        {
            resolution = SettingsFolder.Resolve(new[] { "--settings-dir", folder });
        }
        finally
        {
            File.SetAttributes(folder, FileAttributes.Directory);
        }

        AssertCannotBeUsed(folder, resolution);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }
}
