using System.Globalization;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// Parses one Raw Panel line. Pure, and never throws: anything it does not recognise is
/// <see cref="RawPanelMessage.Unknown"/> (live-safety §2: no throwing on outside data).
/// </summary>
public static class RawPanelParser
{
    private const string HwcPrefix = "HWC#";
    private const string MapPrefix = "map=";
    private const string SpeedPrefix = "Speed:";
    private const string EncoderPrefix = "Enc:";

    public static RawPanelMessage Parse(string line)
    {
        if (line == null)
            return new RawPanelMessage.Unknown(string.Empty);

        var text = line.EndsWith('\r') ? line[..^1] : line;

        return text switch
        {
            "ack" => new RawPanelMessage.Ack(),
            "RDY" => new RawPanelMessage.Ready(),
            _ when text.StartsWith(HwcPrefix, StringComparison.Ordinal) => ParseHwc(text, line),
            _ when text.StartsWith(MapPrefix, StringComparison.Ordinal) => ParseMap(text, line),
            _ when text.StartsWith('_') => ParseReply(text, line),
            _ => new RawPanelMessage.Unknown(line),
        };
    }

    // HWC#<id>[.<edge>]=<action>
    private static RawPanelMessage ParseHwc(string text, string line)
    {
        var body = text.AsSpan(HwcPrefix.Length);
        var equals = body.IndexOf('=');
        if (equals < 0)
            return new RawPanelMessage.Unknown(line);

        var address = body[..equals];
        var action = body[(equals + 1)..];

        int? edge = null;
        var dot = address.IndexOf('.');
        if (dot >= 0)
        {
            if (!TryParseId(address[(dot + 1)..], out var parsedEdge))
                return new RawPanelMessage.Unknown(line);
            edge = parsedEdge;
            address = address[..dot];
        }

        if (!TryParseId(address, out var hwc))
            return new RawPanelMessage.Unknown(line);

        if (action.SequenceEqual("Down"))
            return new RawPanelMessage.Down(hwc, edge);
        if (action.SequenceEqual("Up"))
            return new RawPanelMessage.Up(hwc, edge);

        // Only buttons carry an edge.
        if (edge != null)
            return new RawPanelMessage.Unknown(line);

        if (action.SequenceEqual("Press"))
            return new RawPanelMessage.Press(hwc);
        if (action.StartsWith(SpeedPrefix) && TryParseSigned(action[SpeedPrefix.Length..], out var speed))
            return new RawPanelMessage.Speed(hwc, speed);
        if (action.StartsWith(EncoderPrefix) && TryParseSigned(action[EncoderPrefix.Length..], out var steps))
            return new RawPanelMessage.Encoder(hwc, steps);

        return new RawPanelMessage.Unknown(line);
    }

    // map=<from>:<to>
    private static RawPanelMessage ParseMap(string text, string line)
    {
        var body = text.AsSpan(MapPrefix.Length);
        var colon = body.IndexOf(':');
        if (colon < 0
            || !TryParseId(body[..colon], out var from)
            || !TryParseId(body[(colon + 1)..], out var to))
            return new RawPanelMessage.Unknown(line);

        return new RawPanelMessage.Map(from, to);
    }

    // _<key>=<value>, split on the first '=' (topology values contain '=').
    private static RawPanelMessage ParseReply(string text, string line)
    {
        var equals = text.IndexOf('=');
        if (equals <= 1)
            return new RawPanelMessage.Unknown(line);

        return new RawPanelMessage.Reply(text[1..equals], text[(equals + 1)..]);
    }

    private static bool TryParseId(ReadOnlySpan<char> s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryParseSigned(ReadOnlySpan<char> s, out int value) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
