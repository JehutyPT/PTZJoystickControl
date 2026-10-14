using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;

namespace PtzJoystickControl.Core.Devices;

/// <summary>
/// The XC camera type (Canon's HTTP control protocol): its saved settings, name, Companion number,
/// address, port and login. <c>XcCamera</c> sends the requests.
/// </summary>
public abstract class XcCameraBase : ICamera
{
    public const int DefaultPort = 80;

    private string _name = null!;
    private int? _companionNumber;
    private string _address = "";
    private int _port = DefaultPort;
    private string _username = "";
    private string _password = "";

    protected XcCameraBase(string name)
    {
        Name = name;
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;

            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Name cannot be empty", nameof(value));

            _name = value;
            NotifyPropertyChanged();
            NotifyPersistentPropertyChanged();
        }
    }

    /// <summary>
    /// The number Companion uses to select this camera (<c>?SelectCamera=N</c>, or the legacy
    /// <c>?SetCameraPreview=N</c>). Null only until <c>CamerasService</c> assigns one; never below 1.
    /// </summary>
    public int? CompanionNumber
    {
        get => _companionNumber;
        set
        {
            if (_companionNumber == value)
                return;

            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(value), value, "A Companion number must be 1 or more.");

            _companionNumber = value;
            NotifyPropertyChanged();
            NotifyPersistentPropertyChanged();
        }
    }

    /// <summary>The camera's IPv4 address or host name; empty when it has none, and then it sends nothing.</summary>
    public string Address
    {
        get => _address;
        set
        {
            value ??= "";
            if (_address == value)
                return;

            if (!IsAddress(value))
                throw new ArgumentException("An address must be an IPv4 address (like 192.168.1.20) or a host name.", nameof(value));

            _address = value;
            OnEndpointChanged();
            NotifyPropertyChanged();
            NotifyPersistentPropertyChanged();
        }
    }

    /// <summary>The camera's HTTP port, 1 to 65535.</summary>
    public int Port
    {
        get => _port;
        set
        {
            if (_port == value)
                return;

            if (value < 1 || value > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(value), value, "A port must be 1 to 65535.");

            _port = value;
            OnEndpointChanged();
            NotifyPropertyChanged();
            NotifyPersistentPropertyChanged();
        }
    }

    /// <summary>
    /// The username the camera's login asks for; empty for none, and then requests carry no
    /// credentials (guest control). Saved in plaintext.
    /// </summary>
    public string Username
    {
        get => _username;
        set => SetLogin(value, _password);
    }

    /// <summary>The password for <see cref="Username"/>; any text. Saved in plaintext, never logged.</summary>
    public string Password
    {
        get => _password;
        set => SetLogin(_username, value);
    }

    /// <summary>
    /// Sets <see cref="Username"/> and <see cref="Password"/> together, as one login change: the camera
    /// reacts once, each field that changed is raised, and one persistent change, named for the first
    /// field that changed, saves both. The login the camera already has changes nothing.
    /// </summary>
    public void SetLogin(string username, string password)
    {
        username ??= "";
        password ??= "";
        var usernameChanged = _username != username;
        var passwordChanged = _password != password;
        if (!usernameChanged && !passwordChanged)
            return;

        // The message leaves the value out: it may end up in the log.
        if (!IsUsername(username))
            throw new ArgumentException("A username cannot contain control characters.", nameof(username));

        _username = username;
        _password = password;
        OnLoginChanged();
        if (usernameChanged)
            NotifyPropertyChanged(nameof(Username));
        if (passwordChanged)
            NotifyPropertyChanged(nameof(Password));
        NotifyPersistentPropertyChanged(usernameChanged ? nameof(Username) : nameof(Password));
    }

    /// <summary>Whether <paramref name="value"/> can be a username: any text without control characters.</summary>
    public static bool IsUsername(string value) => !value.Any(char.IsControl);

    // "" (no address), a dotted-quad IPv4 address, or a DNS host name. Digits and dots alone must be a
    // full dotted quad without leading zeros, which other parsers would read as a different address
    // (12 as 0.0.0.12, 010 as octal 8). IPv6 is not supported.
    private static bool IsAddress(string value)
    {
        if (value.Length == 0)
            return true;

        if (value.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            var parts = value.Split('.');
            return parts.Length == 4 && parts.All(p =>
                p.Length is >= 1 and <= 3
                && (p.Length == 1 || p[0] != '0')
                && int.Parse(p, CultureInfo.InvariantCulture) <= 255);
        }

        return Uri.CheckHostName(value) == UriHostNameType.Dns;
    }

    /// <summary>Called after <see cref="Address"/> or <see cref="Port"/> changed, before the change is raised.</summary>
    protected abstract void OnEndpointChanged();

    /// <summary>Called after <see cref="Username"/> or <see cref="Password"/> changed, before the change is raised.</summary>
    protected abstract void OnLoginChanged();

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public event PropertyChangedEventHandler? PersistentPropertyChanged;

    protected void NotifyPersistentPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PersistentPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public abstract bool Connected { get; }

    /// <summary>
    /// Why the camera's requests fail, in the words of the failure's log line; null while they succeed.
    /// Its changes are raised as <see cref="Connected"/>'s are.
    /// </summary>
    public abstract string? FailureReason { get; }

    public abstract bool IsMoving { get; }

    // Movement speeds are speed fractions: 0..1 of the axis's full range, whatever the camera
    // type. A move at fraction 0 still moves (at the slowest speed); only a Stop direction stops.
    public abstract void Pan(double panSpeed, PanDir panDir);
    public abstract void Tilt(double tiltSpeed, TiltDir tiltDir);
    public abstract void Zoom(double zoomSpeed, ZoomDir zoomDir);
    public abstract void Focus(double focusSpeed, FocusDir focusDir);
    public abstract void Stop();
    public abstract void FocusMode(FocusMode focusMode);
    public abstract void FocusLock(FocusLock focusLock);
    public abstract void Power(Power power);
    public abstract void Preset(Preset preset, byte presetNumber);
    public abstract void PresetRecallSpeed(byte speed);
}
