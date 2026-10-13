using System.Text;
using PtzJoystickControl.SkaarhojPanel;
using Xunit;

namespace PtzJoystickControl.Tests.Skaarhoj;

public class LineFramerTests
{
    private static List<string> Feed(LineFramer framer, string chunk)
    {
        var bytes = Encoding.ASCII.GetBytes(chunk);
        return framer.Append(bytes, 0, bytes.Length).ToList();
    }

    [Fact]
    public void SeveralLinesInOneRead_AreSplitOnNewline()
    {
        var framer = new LineFramer();

        Assert.Equal(new[] { "RDY", "map=1:1", "ack" }, Feed(framer, "RDY\nmap=1:1\nack\n"));
    }

    [Fact]
    public void ALineSplitOverReads_IsJoined()
    {
        var framer = new LineFramer();

        Assert.Empty(Feed(framer, "HWC#60=Sp"));
        Assert.Empty(Feed(framer, "eed:-5"));
        Assert.Equal(new[] { "HWC#60=Speed:-500" }, Feed(framer, "00\na"));
        Assert.Equal(new[] { "ack" }, Feed(framer, "ck\n"));
    }

    [Fact]
    public void ByteByByte_YieldsEachLineOnce()
    {
        var framer = new LineFramer();
        var lines = new List<string>();

        foreach (var c in "RDY\nmap=12:12\n\nack\n")
            lines.AddRange(Feed(framer, c.ToString()));

        Assert.Equal(new[] { "RDY", "map=12:12", "", "ack" }, lines);
    }

    [Fact]
    public void OnlyTheGivenRangeOfTheBuffer_IsRead()
    {
        var framer = new LineFramer();
        var buffer = Encoding.ASCII.GetBytes("xxack\nyy");

        Assert.Equal(new[] { "ack" }, framer.Append(buffer, 2, 4).ToList());
    }

    [Fact]
    public void ALineLongerThanTheLimit_IsDropped_AndFramingRecoversAtTheNextNewline()
    {
        var framer = new LineFramer(maxLineLength: 8);

        Assert.Empty(Feed(framer, "0123456789"));
        Assert.Empty(Feed(framer, "abcdef"));
        Assert.Equal(1, framer.DroppedLines);
        Assert.Equal(new[] { "ack" }, Feed(framer, "xyz\nack\n"));
        Assert.Equal(1, framer.DroppedLines);
    }

    [Fact]
    public void NonAsciiBytes_DoNotThrow()
    {
        var framer = new LineFramer();
        var bytes = new byte[] { 0xFF, 0xC3, 0x28, (byte)'\n' };

        var lines = framer.Append(bytes, 0, bytes.Length).ToList();

        Assert.Single(lines);
    }
}
