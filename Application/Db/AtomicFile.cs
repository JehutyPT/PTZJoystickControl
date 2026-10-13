using System.Text;

namespace PtzJoystickControl.Application.Db;

/// <summary>
/// Writes a settings file so a crash mid-save leaves either the old or the new content on
/// disk, never a partial file. Flushing before the rename also covers power loss on a
/// best-effort basis (drive caches and directory metadata are not forced to disk).
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="contents"/> to a uniquely named sibling temp file, flushes it to disk, then
    /// moves it over <paramref name="path"/>. On failure the temp file is removed and the
    /// exception propagates; the existing file at <paramref name="path"/> is left as it was.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        // Same folder as the target, so the final move is a rename on one volume. A unique
        // name per save keeps overlapping saves from truncating or deleting each other's temp file.
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
                stream.Write(bytes, 0, bytes.Length);
                // Force the data to disk before the rename, so the rename cannot land first.
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception)
        {
            // Best effort: a leftover temp file is harmless; nothing ever reads it.
        }
    }
}
