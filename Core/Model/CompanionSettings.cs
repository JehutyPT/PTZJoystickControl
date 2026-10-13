namespace PtzJoystickControl.Core.Model;

/// <summary>
/// Where the app reaches Bitfocus Companion and where it listens for Companion's requests.
/// Read from <c>Companion.json</c> at startup; the defaults are the values the app used
/// before they were configurable.
/// </summary>
public class CompanionSettings
{
    public const string DefaultHost = "localhost";
    public const int DefaultPort = 8000;
    public const int DefaultListenerPort = 12345;

    /// <summary>Host name or IP address of the Companion instance (no scheme, no port).</summary>
    public string Host { get; set; } = DefaultHost;

    /// <summary>Companion's HTTP port: app → Companion.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>The app's HTTP listener port on localhost: Companion → app.</summary>
    public int ListenerPort { get; set; } = DefaultListenerPort;

    public Uri OutboundBaseAddress => new UriBuilder(Uri.UriSchemeHttp, Host, Port, "/").Uri;
}
