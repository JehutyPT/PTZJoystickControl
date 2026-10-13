using PtzJoystickControl.SkaarhojPanel;
using Xunit;

namespace PtzJoystickControl.Tests.Skaarhoj;

// Every format from docs/skaarhoj-integration.md, "Input messages" and "Queries".
public class RawPanelParserTests
{
    [Theory]
    [InlineData("HWC#60=Speed:-500", 60, -500)]
    [InlineData("HWC#61=Speed:500", 61, 500)]
    [InlineData("HWC#58=Speed:0", 58, 0)]
    // Out of range is accepted unclamped: dropping it would leave the previous speed standing (#37 clamps).
    [InlineData("HWC#62=Speed:1200", 62, 1200)]
    [InlineData("HWC#62=Speed:-99999", 62, -99999)]
    public void Speed_IsParsedWithAnyInteger(string line, int hwc, int speed)
    {
        Assert.Equal(new RawPanelMessage.Speed(hwc, speed), RawPanelParser.Parse(line));
    }

    [Fact]
    public void Down_WithEdge_IsParsed()
    {
        Assert.Equal(new RawPanelMessage.Down(1, 2), RawPanelParser.Parse("HWC#1.2=Down"));
    }

    [Fact]
    public void Up_WithoutEdge_IsParsed()
    {
        Assert.Equal(new RawPanelMessage.Up(63, null), RawPanelParser.Parse("HWC#63=Up"));
    }

    [Fact]
    public void Up_WithEdge_AndDown_WithoutEdge_AreParsed()
    {
        Assert.Equal(new RawPanelMessage.Up(1, 2), RawPanelParser.Parse("HWC#1.2=Up"));
        Assert.Equal(new RawPanelMessage.Down(63, null), RawPanelParser.Parse("HWC#63=Down"));
    }

    [Theory]
    [InlineData("HWC#57=Enc:-1", 57, -1)]
    [InlineData("HWC#46=Enc:1", 46, 1)]
    public void Encoder_IsParsed(string line, int hwc, int steps)
    {
        Assert.Equal(new RawPanelMessage.Encoder(hwc, steps), RawPanelParser.Parse(line));
    }

    [Fact]
    public void Press_IsParsed()
    {
        Assert.Equal(new RawPanelMessage.Press(57), RawPanelParser.Parse("HWC#57=Press"));
    }

    [Fact]
    public void Map_IsParsed()
    {
        Assert.Equal(new RawPanelMessage.Map(12, 12), RawPanelParser.Parse("map=12:12"));
    }

    [Fact]
    public void Ready_AndAck_AreParsed()
    {
        Assert.Equal(new RawPanelMessage.Ready(), RawPanelParser.Parse("RDY"));
        Assert.Equal(new RawPanelMessage.Ack(), RawPanelParser.Parse("ack"));
    }

    [Theory]
    [InlineData("_isSleeping=1", "isSleeping", "1")]
    [InlineData("_sleepTimer=14400000", "sleepTimer", "14400000")]
    [InlineData("_model=SK_PTZEXTREMEV2", "model", "SK_PTZEXTREMEV2")]
    [InlineData("_connections=192.168.1.10;", "connections", "192.168.1.10;")]
    [InlineData("_version=v2.5.27", "version", "v2.5.27")]
    // Split on the first '=' only: topology replies carry '=' in their value.
    [InlineData("_panelTopology_HWC={\"a\"=1}", "panelTopology_HWC", "{\"a\"=1}")]
    [InlineData("_empty=", "empty", "")]
    public void Reply_IsParsedAsKeyAndValue(string line, string key, string value)
    {
        Assert.Equal(new RawPanelMessage.Reply(key, value), RawPanelParser.Parse(line));
    }

    [Theory]
    [InlineData("HWC#60=Speed:-500\r", "HWC#60=Speed:-500")]
    [InlineData("ack\r", "ack")]
    [InlineData("map=12:12\r", "map=12:12")]
    public void TrailingCarriageReturn_IsStripped(string withCr, string withoutCr)
    {
        Assert.Equal(RawPanelParser.Parse(withoutCr), RawPanelParser.Parse(withCr));
        Assert.IsNotType<RawPanelMessage.Unknown>(RawPanelParser.Parse(withCr));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("BSY")]
    [InlineData("Ack")]
    [InlineData("HWC#")]
    [InlineData("HWC#=Down")]
    [InlineData("HWC#x=Speed:1")]
    [InlineData("HWC#-1=Down")]
    [InlineData("HWC#60=Speed:abc")]
    [InlineData("HWC#60=Speed:")]
    [InlineData("HWC#60=Speed:99999999999")]
    [InlineData("HWC#60=Speed")]
    [InlineData("HWC#60")]
    [InlineData("HWC#60=")]
    [InlineData("HWC#60=Sideways")]
    [InlineData("HWC#57=Enc:x")]
    [InlineData("HWC#1.x=Down")]
    [InlineData("HWC#1.=Down")]
    [InlineData("HWC#1.2.3=Down")]
    [InlineData("HWC#1.2=Speed:5")]
    [InlineData("map=")]
    [InlineData("map=12")]
    [InlineData("map=a:b")]
    [InlineData("map=1:2:3")]
    [InlineData("_")]
    [InlineData("_=1")]
    [InlineData("_model")]
    public void MalformedOrUnknownInput_IsUnknown_AndDoesNotThrow(string line)
    {
        var message = RawPanelParser.Parse(line);

        var unknown = Assert.IsType<RawPanelMessage.Unknown>(message);
        Assert.Equal(line, unknown.Line);
    }

    [Fact]
    public void NullInput_IsUnknown_AndDoesNotThrow()
    {
        Assert.IsType<RawPanelMessage.Unknown>(RawPanelParser.Parse(null!));
    }
}
