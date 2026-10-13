using System.Text;

namespace PtzJoystickControl.Application.Logging;

/// <summary>
/// Append-only log file that rolls by size: when the next line would push
/// <c>PTZJoystickControl.log</c> past <see cref="MaxBytes"/>, it becomes
/// <c>PTZJoystickControl.1.log</c> (older archives shift up) and at most
/// <see cref="MaxArchives"/> archives are kept, so disk use stays bounded.
/// A file left oversized by an earlier run is rolled by <see cref="RollIfOversized"/>,
/// which the <see cref="AppLogListener"/> writer thread calls when it starts. Every line
/// is flushed as it is written. Nothing here ever throws: a file that cannot be opened,
/// written or rolled is skipped and retried on the next line.
/// Not thread-safe; only the <see cref="AppLogListener"/> writer thread calls it, so its
/// disk I/O never runs on a logging (input) thread.
/// </summary>
public sealed class RollingLogFile : IDisposable
{
    public const string BaseName = "PTZJoystickControl";
    public const long DefaultMaxBytes = 5 * 1024 * 1024;
    public const int DefaultMaxArchives = 4;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private StreamWriter? _writer;
    private long _length;

    public RollingLogFile(string directory, long maxBytes = DefaultMaxBytes, int maxArchives = DefaultMaxArchives)
    {
        _directory = directory;
        MaxBytes = maxBytes;
        MaxArchives = Math.Max(0, maxArchives);
        Path = ArchivePath(0);
    }

    /// <summary>The active log file.</summary>
    public string Path { get; }
    internal long MaxBytes { get; }
    internal int MaxArchives { get; }

    /// <summary>
    /// Rolls the active file if it is already larger than <see cref="MaxBytes"/>, e.g. left
    /// over from an earlier run. Never throws.
    /// </summary>
    public void RollIfOversized()
    {
        try
        {
            long length = _writer != null ? _length : new FileInfo(Path).Length;
            if (length > MaxBytes)
                Roll();
        }
        catch
        {
            // Missing file or unusable directory: nothing to roll.
        }
    }

    public void Append(string line)
    {
        try
        {
            string text = line + Environment.NewLine;
            long bytes = Utf8NoBom.GetByteCount(text);

            if (!EnsureOpen())
                return;
            if (_length > 0 && _length + bytes > MaxBytes)
            {
                Roll();
                if (!EnsureOpen())
                    return;
            }

            _writer!.Write(text);
            _writer.Flush();
            _length += bytes;
        }
        catch
        {
            // The write failed (disk full, file removed, ...). Reopen on the next line.
            Close();
        }
    }

    /// <summary>Flushes buffered text; with <paramref name="toDisk"/>, through the OS cache too.</summary>
    public void Flush(bool toDisk = false)
    {
        try
        {
            _writer?.Flush();
            if (toDisk && _writer?.BaseStream is FileStream stream)
                stream.Flush(flushToDisk: true);
        }
        catch
        {
            Close();
        }
    }

    public void Dispose() => Close();

    private bool EnsureOpen()
    {
        if (_writer != null)
            return true;
        try
        {
            Directory.CreateDirectory(_directory);
            // ReadWrite|Delete sharing lets people and tools read the file while the app runs.
            var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _length = stream.Length;
            _writer = new StreamWriter(stream, Utf8NoBom);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Roll()
    {
        Close();

        // Each step on its own: a failure leaves at worst an extra or missing archive.
        TryRun(() => File.Delete(ArchivePath(MaxArchives)));
        for (int i = MaxArchives - 1; i >= 1; i--)
        {
            int from = i;
            TryRun(() =>
            {
                if (File.Exists(ArchivePath(from)))
                    File.Move(ArchivePath(from), ArchivePath(from + 1), overwrite: true);
            });
        }
        if (MaxArchives > 0)
            TryRun(() => File.Move(Path, ArchivePath(1), overwrite: true));
        else
            TryRun(() => File.Delete(Path));
    }

    private string ArchivePath(int index) => System.IO.Path.Combine(
        _directory, index == 0 ? $"{BaseName}.log" : $"{BaseName}.{index}.log");

    private void Close()
    {
        var writer = _writer;
        _writer = null;
        _length = 0;
        try { writer?.Dispose(); } catch { }
    }

    private static void TryRun(Action action)
    {
        try { action(); } catch { }
    }
}
