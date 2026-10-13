namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// One line received from a SKAARHOJ Raw Panel, parsed by <see cref="RawPanelParser"/>.
/// Formats are in docs/skaarhoj-integration.md ("Input messages", "Queries").
/// </summary>
public abstract record RawPanelMessage
{
    private RawPanelMessage() { }

    /// <summary><c>HWC#&lt;id&gt;=Speed:&lt;n&gt;</c>: joystick axis or zoom rocker. Not clamped; the documented range is -500..500.</summary>
    public sealed record Speed(int Hwc, int Value) : RawPanelMessage;

    /// <summary><c>HWC#&lt;id&gt;[.&lt;edge&gt;]=Down</c>. <paramref name="Edge"/> is set for four-way buttons (1 top, 2 left, 4 bottom, 8 right).</summary>
    public sealed record Down(int Hwc, int? Edge) : RawPanelMessage;

    /// <summary><c>HWC#&lt;id&gt;[.&lt;edge&gt;]=Up</c>. May arrive without a matching <see cref="Down"/>.</summary>
    public sealed record Up(int Hwc, int? Edge) : RawPanelMessage;

    /// <summary><c>HWC#&lt;id&gt;=Enc:&lt;±n&gt;</c>: encoder steps, clockwise positive.</summary>
    public sealed record Encoder(int Hwc, int Steps) : RawPanelMessage;

    /// <summary><c>HWC#&lt;id&gt;=Press</c>: encoder push.</summary>
    public sealed record Press(int Hwc) : RawPanelMessage;

    /// <summary><c>map=&lt;from&gt;:&lt;to&gt;</c>: sent on connect and whenever any client connects.</summary>
    public sealed record Map(int From, int To) : RawPanelMessage;

    /// <summary><c>RDY</c>. Not always first, and not sent to a second client.</summary>
    public sealed record Ready : RawPanelMessage;

    /// <summary><c>ack</c>: the reply to <c>ping</c>.</summary>
    public sealed record Ack : RawPanelMessage;

    /// <summary><c>_&lt;key&gt;=&lt;value&gt;</c>, e.g. <c>_model=SK_PTZEXTREMEV2</c> (key <c>model</c>).</summary>
    public sealed record Reply(string Key, string Value) : RawPanelMessage;

    /// <summary>Anything else, including empty or malformed lines. <paramref name="Line"/> is the line as received.</summary>
    public sealed record Unknown(string Line) : RawPanelMessage;
}
