using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using Xunit;

namespace PtzJoystickControl.Tests.Db;

public sealed class SettingsStoreAtomicWriteTests : IDisposable
{
    private const string PreviousContent = "previous settings that must survive a failed save";

    private readonly string _dir;

    public SettingsStoreAtomicWriteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PtzSettingsStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            File.SetAttributes(_dir, FileAttributes.Directory);
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
    }

    private string FilePath(string name) => Path.Combine(_dir, name);

    // Removing write permission from the folder makes creating the temp file fail, which
    // simulates a save that dies before the replace step. Windows ignores the read-only
    // attribute on folders, so callers skip the failure-path tests there (CI runs on Linux).
    private void BlockNewFilesInFolder() =>
        File.SetAttributes(_dir, FileAttributes.Directory | FileAttributes.ReadOnly);

    private static bool CanBlockNewFiles => !OperatingSystem.IsWindows();

    private string[] FolderEntries() =>
        Directory.EnumerateFileSystemEntries(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    private static GamepadSettings Gamepad(string id, string name) =>
        new() { Id = id, Name = name, IsActivated = true, Inputs = new List<InputSettings>() };

    [Fact]
    public void GamepadSave_OverExistingFile_ReplacesContentAndReadsBack()
    {
        var path = FilePath("Gamepads.json");
        File.WriteAllText(path, new string('x', 10_000));

        Assert.True(new GamepadSettingsStore(path).SaveGamepadSettings(Gamepad("pad-1", "Joystick")));

        var reloaded = new GamepadSettingsStore(path).GetGamepadSettingsById("pad-1");
        Assert.NotNull(reloaded);
        Assert.Equal("Joystick", reloaded!.Name);
        Assert.Equal(new[] { "Gamepads.json" }, FolderEntries());
    }

    [Fact]
    public void GamepadSave_WhenWriteFailsBeforeReplace_ReturnsFalseAndKeepsPreviousFile()
    {
        if (!CanBlockNewFiles) return;
        var path = FilePath("Gamepads.json");
        File.WriteAllText(path, PreviousContent);
        BlockNewFilesInFolder();

        var saved = new GamepadSettingsStore(path).SaveGamepadSettings(Gamepad("pad-1", "Joystick"));

        Assert.False(saved);
        Assert.Equal(PreviousContent, File.ReadAllText(path));
        Assert.Equal(new[] { "Gamepads.json" }, FolderEntries());
    }

    [Fact]
    public void CameraSave_OverExistingFile_ReplacesContentAndLeavesNoTempFile()
    {
        var path = FilePath("Cameras.json");
        File.WriteAllText(path, new string('x', 10_000));

        Assert.True(new CameraSettingsStore(path).SaveCameras(Enumerable.Empty<ViscaDeviceBase>()));

        Assert.Equal("[]", File.ReadAllText(path));
        Assert.Empty(new CameraSettingsStore(path).GetAllCameras());
        Assert.Equal(new[] { "Cameras.json" }, FolderEntries());
    }

    [Fact]
    public void CameraSave_WhenWriteFailsBeforeReplace_ReturnsFalseAndKeepsPreviousFile()
    {
        if (!CanBlockNewFiles) return;
        var path = FilePath("Cameras.json");
        File.WriteAllText(path, PreviousContent);
        BlockNewFilesInFolder();

        var saved = new CameraSettingsStore(path).SaveCameras(Enumerable.Empty<ViscaDeviceBase>());

        Assert.False(saved);
        Assert.Equal(PreviousContent, File.ReadAllText(path));
        Assert.Equal(new[] { "Cameras.json" }, FolderEntries());
    }

    [Fact]
    public void CameraSave_WhenReplaceFails_ReturnsFalseAndRemovesTempFile()
    {
        // The target path is a directory, so the temp file is written but cannot replace it.
        var path = FilePath("Cameras.json");
        Directory.CreateDirectory(path);

        var saved = new CameraSettingsStore(path).SaveCameras(Enumerable.Empty<ViscaDeviceBase>());

        Assert.False(saved);
        Assert.Equal(new[] { "Cameras.json" }, FolderEntries());
    }

    [Fact]
    public void CameraSave_OverlappingSaves_AllSucceedAndLeaveNoTempFile()
    {
        var path = FilePath("Cameras.json");
        var store = new CameraSettingsStore(path);

        var results = new bool[64];
        Parallel.For(0, results.Length, i => results[i] = store.SaveCameras(Enumerable.Empty<ViscaDeviceBase>()));

        Assert.All(results, Assert.True);
        Assert.Equal("[]", File.ReadAllText(path));
        Assert.Equal(new[] { "Cameras.json" }, FolderEntries());
    }
}
