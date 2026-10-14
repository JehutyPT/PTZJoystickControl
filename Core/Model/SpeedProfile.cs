namespace PtzJoystickControl.Core.Model;

/// <summary>
/// A named set of whole percentages (1–100) for pan, tilt, zoom and focus. While it is the active
/// speed profile, every move on an axis, on every camera, runs at that percentage of the speed the
/// input asks for (docs/adr/0002-global-speed-profiles.md).
/// </summary>
public sealed record SpeedProfile(string Name, int Pan, int Tilt, int Zoom, int Focus)
{
    public const int MinPercent = 1;
    public const int MaxPercent = 100;
}
