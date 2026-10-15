namespace PtzJoystickControl.Application;

/// <summary>
/// The outcome of <see cref="SettingsFolder.Resolve"/>: either the settings folder to start on and
/// the arguments to pass on, or the one line that says why the app cannot start.
/// </summary>
public sealed class SettingsFolderResolution
{
    private SettingsFolderResolution(string path, bool fromSwitch, string[] passedOnArguments, string? failure)
    {
        Path = path;
        FromSwitch = fromSwitch;
        PassedOnArguments = passedOnArguments;
        Failure = failure;
    }

    public bool IsResolved => Failure == null;

    /// <summary>The settings folder's full path; empty when <see cref="IsResolved"/> is false.</summary>
    public string Path { get; }

    /// <summary>True when <c>--settings-dir</c> named the folder, false for the default folder.</summary>
    public bool FromSwitch { get; }

    /// <summary>Every argument except the switch and its folder, in order, to pass on to Avalonia.</summary>
    public string[] PassedOnArguments { get; }

    /// <summary>The line to write to standard error before exiting with code 2, or null.</summary>
    public string? Failure { get; }

    internal static SettingsFolderResolution Resolved(string path, bool fromSwitch, string[] passedOnArguments) =>
        new(path, fromSwitch, passedOnArguments, null);

    internal static SettingsFolderResolution Failed(string failure) =>
        new("", false, Array.Empty<string>(), failure);
}
