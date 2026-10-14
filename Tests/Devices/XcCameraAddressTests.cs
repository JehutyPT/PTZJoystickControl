using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// The address and port setters accept what the settings panel may save and reject the rest, as the
// VISCA camera's setters do, so the panel can show the error.
public class XcCameraAddressTests
{
    [Fact]
    public void ANewCamera_HasNoAddress_AndPort80()
    {
        using var camera = new XcCamera(XcTestCameras.UniqueName());

        Assert.Equal("", camera.Address);
        Assert.Equal(80, camera.Port);
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("cr-n500-1")]
    [InlineData("cam1.studio.example")]
    [InlineData("")]
    public void AnIpv4AddressAHostNameOrNothing_IsAccepted(string address)
    {
        using var camera = SetterOnlyCamera();

        camera.Address = address;

        Assert.Equal(address, camera.Address);
    }

    [Theory]
    [InlineData("192.168.1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("192.168.1.256")]
    // A leading zero could be read as octal.
    [InlineData("192.168.01.20")]
    // A bare number is a valid address to some parsers (12 is 0.0.0.12).
    [InlineData("12")]
    [InlineData("192.168..20")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("[::1]")]
    [InlineData("cam 1")]
    [InlineData(" 10.0.0.1")]
    [InlineData("http://10.0.0.1")]
    [InlineData("10.0.0.1:80")]
    [InlineData("10.0.0.1/24")]
    public void AnythingElse_IsRejected_AndTheAddressKept(string address)
    {
        using var camera = SetterOnlyCamera();
        camera.Address = "10.0.0.1";

        Assert.ThrowsAny<ArgumentException>(() => camera.Address = address);
        Assert.Equal("10.0.0.1", camera.Address);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8080)]
    [InlineData(65535)]
    public void APortFrom1To65535_IsAccepted(int port)
    {
        using var camera = SetterOnlyCamera();

        camera.Port = port;

        Assert.Equal(port, camera.Port);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void APortOutside1To65535_IsRejected_AndThePortKept(int port)
    {
        using var camera = SetterOnlyCamera();

        Assert.ThrowsAny<ArgumentException>(() => camera.Port = port);
        Assert.Equal(XcCameraBase.DefaultPort, camera.Port);
    }

    [Fact]
    public void AnEmptyName_IsRejected_AndTheNameKept()
    {
        using var camera = SetterOnlyCamera();
        var name = camera.Name;

        Assert.ThrowsAny<ArgumentException>(() => camera.Name = " ");
        Assert.Equal(name, camera.Name);
    }

    // Disposed first, so giving it an address sends nothing: these tests exercise only the setters.
    private static XcCamera SetterOnlyCamera()
    {
        var camera = new XcCamera(XcTestCameras.UniqueName());
        camera.Dispose();
        return camera;
    }
}
