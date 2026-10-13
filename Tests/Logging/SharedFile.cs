namespace PtzJoystickControl.Tests.Logging;

internal static class SharedFile
{
    /// <summary>
    /// Reads the non-empty lines of a file that a <c>RollingLogFile</c> may still hold
    /// open for writing.
    /// </summary>
    internal static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }
}
