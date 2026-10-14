using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

// The address, port and username setters accept what the settings panel may save and reject the rest,
// as the VISCA camera's setters do, so the panel can show the error.
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

    [Fact]
    public void ANewCamera_HasNoLogin()
    {
        using var camera = new XcCamera(XcTestCameras.UniqueName());

        Assert.Equal("", camera.Username);
        Assert.Equal("", camera.Password);
    }

    [Theory]
    [InlineData("fake-xc-user")]
    [InlineData("fake xc user")]
    [InlineData("fäke-xc-üser")]
    [InlineData("")]
    public void AUsernameWithoutControlCharacters_IsAccepted(string username)
    {
        using var camera = SetterOnlyCamera();

        camera.Username = username;

        Assert.Equal(username, camera.Username);
    }

    // The message does not repeat the username, which the settings panel shows and the log may record.
    [Theory]
    [InlineData("fake-xc\nuser")]
    [InlineData("fake-xc\ruser")]
    [InlineData("fake-xc\tuser")]
    [InlineData("fake-xc\0user")]
    [InlineData("fake-xc\u007fuser")]
    public void AUsernameWithAControlCharacter_IsRejected_WithoutRepeatingIt_AndTheUsernameKept(string username)
    {
        using var camera = SetterOnlyCamera();
        camera.Username = "fake-xc-user";

        var rejected = Assert.ThrowsAny<ArgumentException>(() => camera.Username = username);

        Assert.DoesNotContain("fake-xc", rejected.Message);
        Assert.Equal("fake-xc-user", camera.Username);
    }

    [Theory]
    [InlineData("fake-xc-password")]
    [InlineData("fake-xc\npassword")]
    [InlineData(" ")]
    [InlineData("")]
    public void AnyPassword_IsAccepted(string password)
    {
        using var camera = SetterOnlyCamera();

        camera.Password = password;

        Assert.Equal(password, camera.Password);
    }

    [Fact]
    public void ANullUsernameOrPassword_IsEmpty()
    {
        using var camera = SetterOnlyCamera();
        camera.Username = "fake-xc-user";
        camera.Password = "fake-xc-password";

        camera.Username = null!;
        camera.Password = null!;

        Assert.Equal("", camera.Username);
        Assert.Equal("", camera.Password);
    }

    [Fact]
    public void ChangingTheLogin_RaisesTheChangeAndThePersistentChange_OncePerChange()
    {
        using var camera = SetterOnlyCamera();
        var changed = new List<string?>();
        var persistent = new List<string?>();
        camera.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        camera.PersistentPropertyChanged += (_, e) => persistent.Add(e.PropertyName);

        camera.Username = "fake-xc-user";
        camera.Password = "fake-xc-password";
        camera.Password = "fake-xc-password";

        var expected = new[] { nameof(XcCamera.Username), nameof(XcCamera.Password) };
        Assert.Equal(expected, changed);
        Assert.Equal(expected, persistent);
    }

    // Disposed first, so giving it an address sends nothing: these tests exercise only the setters.
    private static XcCamera SetterOnlyCamera()
    {
        var camera = new XcCamera(XcTestCameras.UniqueName());
        camera.Dispose();
        return camera;
    }
}
