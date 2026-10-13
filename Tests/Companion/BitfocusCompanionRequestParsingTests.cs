using PtzJoystickControl.Core.Model;
using Xunit;

namespace PtzJoystickControl.Tests.Companion;

public class BitfocusCompanionRequestParsingTests
{
    private static KeyValuePair<string?, string?> P(string? name, string? value) => new(name, value);

    [Theory]
    [InlineData("1", 0)]
    [InlineData("2", 1)]
    public void SetCameraPreview_WithSupportedCamera_ReturnsTheExistingSupportedEvent(string value, int supportedIndex)
    {
        var result = BitfocusCompanionEvent.ParseRequest(new[] { P("SetCameraPreview", value) });

        Assert.True(result.IsAccepted);
        Assert.Null(result.RejectionReason);
        var companionEvent = Assert.Single(result.Events);
        Assert.Same(BitfocusCompanionEvent.SupportedEvents[supportedIndex], companionEvent);
    }

    public static IEnumerable<object[]> RejectedRequests() => new[]
    {
        // Numeric names, which Enum.Parse/Enum.TryParse would accept.
        new object[] { "numeric name 0", new[] { P("0", "1") } },
        new object[] { "numeric name 5", new[] { P("5", "1") } },
        // Unknown names and other casings.
        new object[] { "unknown name", new[] { P("foo", "1") } },
        new object[] { "lower case name", new[] { P("setcamerapreview", "1") } },
        new object[] { "upper case name", new[] { P("SETCAMERAPREVIEW", "1") } },
        // HttpListener files "?1", "?foo" and "?SetCameraPreview" under a null name.
        new object[] { "null name", new[] { P(null, "1") } },
        new object[] { "null name for a command word", new[] { P(null, "SetCameraPreview") } },
        // Known command, unsupported value.
        new object[] { "unsupported camera", new[] { P("SetCameraPreview", "3") } },
        new object[] { "empty value", new[] { P("SetCameraPreview", "") } },
        new object[] { "missing value", new[] { P("SetCameraPreview", null) } },
        new object[] { "duplicate parameters combined", new[] { P("SetCameraPreview", "1,2") } },
        // No parameters at all ("/", "/favicon.ico").
        new object[] { "empty query", Array.Empty<KeyValuePair<string?, string?>>() },
        // All or nothing: one bad parameter rejects the valid ones around it.
        new object[] { "valid then unknown", new[] { P("SetCameraPreview", "1"), P("foo", "1") } },
        new object[] { "unknown then valid", new[] { P("foo", "1"), P("SetCameraPreview", "2") } },
        new object[] { "valid then unsupported value", new[] { P("SetCameraPreview", "1"), P("SetCameraPreview", "9") } },
    };

    [Theory]
    [MemberData(nameof(RejectedRequests))]
    public void UnknownOrMalformedRequest_IsRejectedWithAReasonAndNoEvents(string scenario, KeyValuePair<string?, string?>[] parameters)
    {
        var result = BitfocusCompanionEvent.ParseRequest(parameters);

        Assert.False(result.IsAccepted, scenario);
        Assert.Empty(result.Events);
        Assert.False(string.IsNullOrWhiteSpace(result.RejectionReason), scenario);
    }
}
