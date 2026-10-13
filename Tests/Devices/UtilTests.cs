using PtzJoystickControl.Application.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

public class UtilTests
{
    [Theory]
    [InlineData(0f, 0f, 1f, 0f, 100f, 0f)]
    [InlineData(1f, 0f, 1f, 0f, 100f, 100f)]
    [InlineData(0.5f, 0f, 1f, 0f, 100f, 50f)]
    [InlineData(0f, -1f, 1f, 0f, 24f, 12f)]
    public void Map_LinearlyMapsValueBetweenRanges(float value, float fromLow, float fromHigh, float toLow, float toHigh, float expected)
    {
        Assert.Equal(expected, Util.Map(value, fromLow, fromHigh, toLow, toHigh), 4);
    }

    [Fact]
    public void Map_InvertsWhenTargetRangeIsReversed()
    {
        Assert.Equal(75f, Util.Map(0.25f, 0f, 1f, 100f, 0f), 4);
    }
}
