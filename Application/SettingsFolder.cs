using System.Runtime.InteropServices;

namespace PtzJoystickControl.Application;

/// <summary>
/// The one place that decides which folder holds the settings files and the log: the default folder
/// for the OS, or the folder <c>--settings-dir</c> names (#59). A <c>--settings-dir</c> folder that
/// cannot be used fails startup and never falls back to the default folder, which holds the real
/// settings.
/// </summary>
public static class SettingsFolder
{
    public const string Switch = "--settings-dir";

    /// <summary>
    /// Resolves the settings folder from the command-line arguments (see <see cref="Parse"/>). A
    /// folder named by <see cref="Switch"/> is created, with its parents, and checked by creating and
    /// deleting a uniquely named file in it. The default folder is neither created nor checked here:
    /// the startup code creates it, with no check, as before.
    /// </summary>
    public static SettingsFolderResolution Resolve(IReadOnlyList<string> args)
    {
        var resolution = Parse(args);
        if (!resolution.FromSwitch)
            return resolution;
        try
        {
            Directory.CreateDirectory(resolution.Path);
            // A unique name, and CreateNew, so the check can never overwrite or delete a file already there.
            var probe = Path.Combine(resolution.Path, $"PTZJoystickControl.write-check.{Guid.NewGuid():N}.tmp");
            new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
            File.Delete(probe);
            return resolution;
        }
        catch (Exception e)
        {
            return CannotBeUsed(resolution.Path, e);
        }
    }

    /// <summary>
    /// Reads <see cref="Switch"/> and its folder from <paramref name="args"/>, without touching the
    /// disk. The switch is matched exactly and its folder is the next argument, made a full path
    /// against the current directory. Every other argument is passed on, in order, unless it starts
    /// with <c>--</c>. The first problem found, left to right, is the failure.
    /// </summary>
    internal static SettingsFolderResolution Parse(IReadOnlyList<string> args)
    {
        string? folder = null;
        var passedOn = new List<string>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == Switch)
            {
                if (folder != null)
                    return CannotStart($"{Switch} is given more than once");
                if (i + 1 == args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith('-'))
                    return CannotStart($"{Switch} needs a folder, as in {Switch} /path/to/folder");
                folder = args[++i];
            }
            // A mistyped switch must not start the app on the default folder.
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
                return CannotStart($"unknown argument '{args[i]}'; to choose the settings folder, " +
                    $"give it as the next argument: {Switch} <folder>");
            else
                passedOn.Add(args[i]);
        }

        if (folder == null)
            return SettingsFolderResolution.Resolved(DefaultPath(), fromSwitch: false, passedOn.ToArray());
        try
        {
            return SettingsFolderResolution.Resolved(Path.GetFullPath(folder), fromSwitch: true, passedOn.ToArray());
        }
        catch (Exception e)
        {
            // No full path to give (a NUL in it, say): the folder as typed.
            return CannotBeUsed(folder, e);
        }
    }

    private static SettingsFolderResolution CannotBeUsed(string folder, Exception e) =>
        CannotStart($"settings folder '{folder}' ({Switch}) cannot be used: {e.Message}");

    private static SettingsFolderResolution CannotStart(string reason) =>
        SettingsFolderResolution.Failed($"PTZJoystickControl: cannot start: {reason}");

    // The folder for this OS, as before #59.
    private static string DefaultPath() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".PTZJoystickControl")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PTZJoystickControl");
}
