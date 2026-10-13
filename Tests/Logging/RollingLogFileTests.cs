using System.Text;
using PtzJoystickControl.Application.Logging;
using Xunit;

namespace PtzJoystickControl.Tests.Logging;

public sealed class RollingLogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptzlog-" + Guid.NewGuid().ToString("N"));

    public RollingLogFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static int LineBytes(string line) => Encoding.UTF8.GetByteCount(line + Environment.NewLine);

    [Fact]
    public void Path_IsPTZJoystickControlLog_InTheGivenDirectory()
    {
        using var file = new RollingLogFile(_dir);
        Assert.Equal(PathOf("PTZJoystickControl.log"), file.Path);
        Assert.Equal(5 * 1024 * 1024, RollingLogFile.DefaultMaxBytes);
        Assert.Equal(4, RollingLogFile.DefaultMaxArchives);
    }

    [Fact]
    public void Append_WritesUtf8WithoutBom_AndIsReadableWhileOpen()
    {
        using var file = new RollingLogFile(_dir);
        file.Append("câmara 1");

        // Opened for reading while the writer still holds it.
        using var reader = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        reader.CopyTo(bytes);
        Assert.Equal(Encoding.UTF8.GetBytes("câmara 1" + Environment.NewLine), bytes.ToArray());
    }

    [Fact]
    public void Append_KeepsExistingContent()
    {
        File.WriteAllText(PathOf("PTZJoystickControl.log"), "earlier" + Environment.NewLine);
        using var file = new RollingLogFile(_dir);
        file.Append("later");

        Assert.Equal(new[] { "earlier", "later" }, AppLogListenerTests.ReadShared(file.Path));
    }

    [Fact]
    public void Append_RollsBySize_AndKeepsAtMostMaxArchives()
    {
        // Each line is 10 bytes with its newline on every platform's NewLine length; cap fits 3 lines.
        var line = new string('x', 10 - Environment.NewLine.Length);
        long cap = 3 * LineBytes(line);
        using var file = new RollingLogFile(_dir, cap, maxArchives: 2);

        for (int i = 0; i < 3 * 5; i++)
            file.Append(line);

        var names = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "PTZJoystickControl.1.log", "PTZJoystickControl.2.log", "PTZJoystickControl.log" }, names);
        foreach (var name in names)
            Assert.True(new FileInfo(PathOf(name!)).Length <= cap, $"{name} exceeds the cap");
        Assert.Equal(3, AppLogListenerTests.ReadShared(file.Path).Length);
    }

    [Fact]
    public void Append_RollsInOrder_NewestArchiveIsOne()
    {
        var cap = LineBytes("a");
        using var file = new RollingLogFile(_dir, cap, maxArchives: 3);
        file.Append("a");
        file.Append("b");
        file.Append("c");

        Assert.Equal(new[] { "c" }, AppLogListenerTests.ReadShared(file.Path));
        Assert.Equal("b", File.ReadAllText(PathOf("PTZJoystickControl.1.log")).Trim());
        Assert.Equal("a", File.ReadAllText(PathOf("PTZJoystickControl.2.log")).Trim());
    }

    [Fact]
    public void AnOversizedFileFromAnEarlierRun_IsRolledOnTheFirstAppend()
    {
        File.WriteAllText(PathOf("PTZJoystickControl.log"), new string('o', 100));
        using var file = new RollingLogFile(_dir, maxBytes: 50, maxArchives: 1);
        file.Append("new run");

        Assert.Equal(100, new FileInfo(PathOf("PTZJoystickControl.1.log")).Length);
        Assert.Equal(new[] { "new run" }, AppLogListenerTests.ReadShared(file.Path));
    }

    [Fact]
    public void Append_ToAnUnusableDirectory_DoesNotThrow()
    {
        var notADir = PathOf("file");
        File.WriteAllText(notADir, "");
        using var file = new RollingLogFile(notADir);

        file.Append("dropped");
        file.Flush(toDisk: true);
    }
}
