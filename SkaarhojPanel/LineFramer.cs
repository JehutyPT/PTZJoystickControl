using System.Text;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// Splits a TCP byte stream into lines ending in <c>\n</c>, across arbitrary read boundaries.
/// The <c>\n</c> is removed; a <c>\r</c> before it is left for <see cref="RawPanelParser"/> to strip.
/// Not thread-safe: one framer per connection, fed from its read loop.
/// </summary>
internal sealed class LineFramer
{
    // PanelTopology? replies run to tens of kB; anything far beyond is garbage, not a line.
    internal const int DefaultMaxLineLength = 1024 * 1024;

    private readonly int _maxLineLength;
    private readonly List<byte> _pending = new();
    private bool _discarding;

    public LineFramer(int maxLineLength = DefaultMaxLineLength)
    {
        _maxLineLength = maxLineLength;
    }

    /// <summary>Lines dropped because they exceeded the maximum length.</summary>
    public int DroppedLines { get; private set; }

    /// <summary>Adds <paramref name="count"/> bytes from <paramref name="buffer"/> and returns the lines they complete.</summary>
    public IReadOnlyList<string> Append(byte[] buffer, int offset, int count)
    {
        var lines = new List<string>();
        for (var i = offset; i < offset + count; i++)
        {
            var b = buffer[i];
            if (b == (byte)'\n')
            {
                if (_discarding)
                    _discarding = false;
                else
                    lines.Add(Encoding.ASCII.GetString(_pending.ToArray()));
                _pending.Clear();
                continue;
            }

            if (_discarding)
                continue;

            if (_pending.Count >= _maxLineLength)
            {
                // Drop the over-long line up to the next newline instead of growing without bound.
                _pending.Clear();
                _discarding = true;
                DroppedLines++;
                continue;
            }

            _pending.Add(b);
        }
        return lines;
    }
}
