namespace PtzJoystickControl.Core.Model;

/// <summary>
/// Where the app listens for Bitfocus Companion's requests. Read from <c>Companion.json</c> at
/// startup; the default is the value the app used before it was configurable.
/// </summary>
public class CompanionSettings
{
    public const int DefaultListenerPort = 12345;

    /// <summary>The app's HTTP listener port on localhost: Companion → app.</summary>
    public int ListenerPort { get; set; } = DefaultListenerPort;
}
