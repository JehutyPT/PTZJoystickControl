using System.ComponentModel;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using Xunit;

namespace PtzJoystickControl.Tests.Companion;

public class BitfocusCompanionRequestParsingTests
{
    private static readonly ICamera CamA = new ViscaIpDevice("Cam A") { CompanionNumber = 1 };
    private static readonly ICamera CamB = new ViscaIpDevice("Cam B") { CompanionNumber = 2 };
    private static readonly ICamera CamC = new ViscaIpDevice("Cam C") { CompanionNumber = 7 };
    private static readonly ICamera[] Cameras = { CamA, CamB, CamC };

    private static KeyValuePair<string?, string?> P(string? name, string? value) => new(name, value);

    [Theory]
    [InlineData("SelectCamera", "1", "Cam A")]
    [InlineData("SelectCamera", "7", "Cam C")]
    // The operator's existing Companion buttons: a legacy alias of SelectCamera.
    [InlineData("SetCameraPreview", "1", "Cam A")]
    [InlineData("SetCameraPreview", "2", "Cam B")]
    public void CameraByCompanionNumber_ReturnsTheCameraWithThatNumber(string command, string value, string camera)
    {
        var result = BitfocusCompanionEvent.ParseRequest(new[] { P(command, value) }, Cameras);

        Assert.True(result.IsAccepted);
        Assert.Null(result.RejectionReason);
        var companionEvent = Assert.Single(result.Events);
        Assert.Equal(camera, companionEvent.Camera.Name);
        Assert.Equal(int.Parse(value), companionEvent.CompanionNumber);
    }

    [Fact]
    public void SeveralCommands_ReturnTheirCamerasInOrder()
    {
        var result = BitfocusCompanionEvent.ParseRequest(new[] { P("SelectCamera", "2"), P("SetCameraPreview", "1") }, Cameras);

        Assert.True(result.IsAccepted);
        Assert.Equal(new[] { CamB, CamA }, result.Events.Select(e => e.Camera));
        Assert.Null(result.SpeedProfileRequest);
    }

    [Theory]
    [InlineData("faster", CompanionSpeedProfileRequest.Faster)]
    [InlineData("finer", CompanionSpeedProfileRequest.Finer)]
    [InlineData("current", CompanionSpeedProfileRequest.Current)]
    public void SpeedProfile_IsAcceptedAsItsRequest_WithNoCameraEvents(string value, CompanionSpeedProfileRequest expected)
    {
        var result = BitfocusCompanionEvent.ParseRequest(new[] { P("SpeedProfile", value) }, Cameras);

        Assert.True(result.IsAccepted);
        Assert.Equal(expected, result.SpeedProfileRequest);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void AStepWithACameraSelection_IsAcceptedWithBoth()
    {
        var result = BitfocusCompanionEvent.ParseRequest(new[] { P("SelectCamera", "2"), P("SpeedProfile", "finer") }, Cameras);

        Assert.True(result.IsAccepted);
        Assert.Equal(CompanionSpeedProfileRequest.Finer, result.SpeedProfileRequest);
        Assert.Equal(new[] { CamB }, result.Events.Select(e => e.Camera));
    }

    public static IEnumerable<object[]> RejectedRequests() => new[]
    {
        // Numeric names, which Enum.Parse/Enum.TryParse would accept.
        new object[] { "numeric name 0", new[] { P("0", "1") } },
        new object[] { "numeric name 1", new[] { P("1", "1") } },
        // Unknown names and other casings.
        new object[] { "unknown name", new[] { P("foo", "1") } },
        new object[] { "lower case name", new[] { P("setcamerapreview", "1") } },
        new object[] { "upper case name", new[] { P("SELECTCAMERA", "1") } },
        // HttpListener files "?1", "?foo" and "?SelectCamera" under a null name.
        new object[] { "null name", new[] { P(null, "1") } },
        new object[] { "null name for a command word", new[] { P(null, "SelectCamera") } },
        // Known command, no camera with that number.
        new object[] { "unknown number", new[] { P("SelectCamera", "3") } },
        new object[] { "unknown number, legacy", new[] { P("SetCameraPreview", "3") } },
        // Not a plain positive decimal integer.
        new object[] { "zero", new[] { P("SelectCamera", "0") } },
        new object[] { "negative", new[] { P("SelectCamera", "-1") } },
        new object[] { "plus sign", new[] { P("SelectCamera", "+1") } },
        new object[] { "leading space", new[] { P("SelectCamera", " 1") } },
        new object[] { "decimal", new[] { P("SelectCamera", "1.0") } },
        new object[] { "name instead of number", new[] { P("SelectCamera", "Cam A") } },
        new object[] { "overflow", new[] { P("SelectCamera", "99999999999") } },
        new object[] { "non-ASCII digit", new[] { P("SelectCamera", "١") } },
        new object[] { "empty value", new[] { P("SelectCamera", "") } },
        new object[] { "missing value", new[] { P("SelectCamera", null) } },
        new object[] { "duplicate parameters combined", new[] { P("SetCameraPreview", "1,2") } },
        // No parameters at all ("/", "/favicon.ico").
        new object[] { "empty query", Array.Empty<KeyValuePair<string?, string?>>() },
        // All or nothing: one bad parameter rejects the valid ones around it.
        new object[] { "valid then unknown", new[] { P("SelectCamera", "1"), P("foo", "1") } },
        new object[] { "unknown then valid", new[] { P("foo", "1"), P("SelectCamera", "2") } },
        new object[] { "valid then unknown number", new[] { P("SelectCamera", "1"), P("SetCameraPreview", "9") } },
        // SpeedProfile takes exactly faster, finer or current (case-sensitive), once, and current only on its own.
        new object[] { "speed profile, lower case name", new[] { P("speedprofile", "faster") } },
        new object[] { "speed profile, upper case value", new[] { P("SpeedProfile", "Faster") } },
        new object[] { "speed profile, unknown value", new[] { P("SpeedProfile", "up") } },
        new object[] { "speed profile, number", new[] { P("SpeedProfile", "1") } },
        new object[] { "speed profile, empty value", new[] { P("SpeedProfile", "") } },
        new object[] { "speed profile, missing value", new[] { P("SpeedProfile", null) } },
        new object[] { "speed profile, bare word", new[] { P(null, "SpeedProfile") } },
        new object[] { "speed profile, duplicate parameters combined", new[] { P("SpeedProfile", "faster,finer") } },
        new object[] { "speed profile given twice", new[] { P("SpeedProfile", "faster"), P("SpeedProfile", "faster") } },
        new object[] { "current then a camera", new[] { P("SpeedProfile", "current"), P("SelectCamera", "1") } },
        new object[] { "a camera then current", new[] { P("SelectCamera", "1"), P("SpeedProfile", "current") } },
        new object[] { "step then unknown number", new[] { P("SpeedProfile", "faster"), P("SelectCamera", "9") } },
        new object[] { "unknown number then step", new[] { P("SelectCamera", "9"), P("SpeedProfile", "finer") } },
    };

    [Theory]
    [MemberData(nameof(RejectedRequests))]
    public void UnknownOrMalformedRequest_IsRejectedWithAReasonAndNoEvents(string scenario, KeyValuePair<string?, string?>[] parameters)
    {
        var result = BitfocusCompanionEvent.ParseRequest(parameters, Cameras);

        Assert.False(result.IsAccepted, scenario);
        Assert.Empty(result.Events);
        Assert.Null(result.SpeedProfileRequest);
        Assert.False(string.IsNullOrWhiteSpace(result.RejectionReason), scenario);
    }

    [Fact]
    public void NumberSharedByTwoCameras_IsRejected_NamingBothCameras()
    {
        var left = new ViscaIpDevice("Left") { CompanionNumber = 3 };
        var right = new ViscaIpDevice("Right") { CompanionNumber = 3 };

        var result = BitfocusCompanionEvent.ParseRequest(new[] { P("SelectCamera", "3") }, new ICamera[] { CamA, left, right });

        Assert.False(result.IsAccepted);
        Assert.Empty(result.Events);
        Assert.Contains("Left", result.RejectionReason);
        Assert.Contains("Right", result.RejectionReason);
    }

    [Fact]
    public void NoCameras_RejectsEveryNumber()
    {
        var result = BitfocusCompanionEvent.ParseRequest(new[] { P("SetCameraPreview", "1") }, Array.Empty<ICamera>());

        Assert.False(result.IsAccepted);
    }

    [Fact]
    public void CameraOfAnotherCameraType_IsSelectedByItsCompanionNumber()
    {
        var other = new NonViscaCamera("Other") { CompanionNumber = 4 };

        var result = BitfocusCompanionEvent.ParseRequest(new[] { P("SelectCamera", "4") }, new ICamera[] { CamA, other, CamC });

        Assert.True(result.IsAccepted);
        var companionEvent = Assert.Single(result.Events);
        Assert.Same(other, companionEvent.Camera);
        Assert.Equal(4, companionEvent.CompanionNumber);
    }

    // A camera of another camera type: it implements only the camera abstraction, not the VISCA base type.
    private sealed class NonViscaCamera : ICamera
    {
        public NonViscaCamera(string name) => Name = name;

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public string Name { get; }
        public int? CompanionNumber { get; set; }
        public bool Connected => true;
        public bool IsMoving => false;

        public void Pan(double panSpeed, PanDir panDir) { }
        public void Tilt(double tiltSpeed, TiltDir tiltDir) { }
        public void Zoom(double zoomSpeed, ZoomDir zoomDir) { }
        public void Focus(double focusSpeed, FocusDir focusDir) { }
        public void Stop() { }
        public void FocusMode(FocusMode focusMode) { }
        public void FocusLock(FocusLock focusLock) { }
        public void Power(Power power) { }
        public void Preset(Preset preset, byte presetNumber) { }
        public void PresetRecallSpeed(byte speed) { }
    }
}
