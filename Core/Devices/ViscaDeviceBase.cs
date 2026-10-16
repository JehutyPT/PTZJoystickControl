using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PtzJoystickControl.Core.Devices;

public abstract class ViscaDeviceBase : ICamera
{
    protected string name = null!;
    protected byte address = 0x01;
    protected int sendWaitTime = 30;
    protected byte[] sendBuffer = new byte[128];
    protected byte[] receiveBuffer = new byte[128];
    protected int sendBuffIndex = 0;
    protected int receiveBuffIndex = 0;
    protected DateTime lastSendTime = DateTime.UtcNow;
    protected DateTime lastReceiveTime = DateTime.MinValue;

    protected byte powerCmd;

    protected byte panSpeed = 0x04;
    protected byte tiltSpeed = 0x04;
    protected PanDir panDir = PanDir.Stop;
    protected TiltDir tiltDir = TiltDir.Stop;

    protected byte zoomCmd = 0x00;

    protected byte focusCmd;
    protected byte focusModeCmd;
    protected byte focusLockCmd;

    protected byte presetCmd;
    protected byte presetCmdNumber;
    protected byte presetRecallSpeed = 0x04;
    protected int? _companionNumber;

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

    public ViscaDeviceBase(string name)
    {
        Name = name;
    }
    
    protected internal Action<ViscaDeviceBase, byte[], int>? InquiryReplyParser { get; set; }
    protected internal bool Acked { get; internal set; }
    protected internal bool Completed { get; internal set; }

    public string Name
    {
        get { return name; }
        set
        {
            if (name == value)
                return;

            if(string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Name cannot be empty", nameof(value));

                name = value;
                NotifyPropertyChanged();
                NotifyPersistentPropertyChanged();
            
        }
    }

    public byte ViscaAddress
    {
        get => address;
        set => address = 0 < value && value < 128 
            ? value
            : throw new ArgumentOutOfRangeException($"ViscaAddress must be between 1 and 127. {value} was given.");
    }

    protected DateTime LastSendTime
    {
        get => lastSendTime;
        set {
            var tempConnected = Connected;
            lastSendTime = value;
            if (tempConnected != Connected)
                NotifyPropertyChanged(nameof(Connected));
        }
    }

    protected internal DateTime LastReceiveTime {
        get => lastReceiveTime;
        set
        {
            var tempConnected = Connected;
            lastReceiveTime = value;
            if (tempConnected != Connected)
                NotifyPropertyChanged(nameof(Connected));
        }
    }

    public virtual bool Connected { 
        get {
            var t = lastSendTime - lastReceiveTime;
            return t <= TimeSpan.FromMilliseconds(500) && t >= TimeSpan.FromMilliseconds(-500); 
        } 
    }
    
    public Power? PowerState { get; protected internal set; }

    public abstract void Power(Power byteVal);

    // Movement speeds are speed fractions: 0..1 of the axis's full range, whatever the camera
    // type. Each camera type maps them onto its own protocol. A move at fraction 0 still moves
    // (at the slowest speed); only a Stop direction stops.
    public abstract void Pan(double panSpeed, PanDir panDir);
    public abstract void Tilt(double tiltSpeed, TiltDir tiltDir);
    public abstract void PanTilt(double panSpeed, double tiltSpeed, PanDir panDir, TiltDir tiltDir);
    public abstract void Zoom(double zoomSpeed, ZoomDir zoomDir);
    public abstract void Focus(double focusSpeed, FocusDir focusDir);
    public abstract void FocusMode(FocusMode focusMode);
    public abstract void FocusLock(FocusLock focusMode);
    public abstract void Preset(Preset preset, byte presetNumber);
    public abstract void PresetRecallSpeed(byte value);

    /// <summary>
    /// Stops pan, tilt, zoom and focus, sending each stop repeatedly, even when the camera is
    /// already believed to be stopped (its real state may differ if packets were lost).
    /// </summary>
    public abstract void Stop();

    /// <summary>
    /// A focus step (<see cref="ICamera.FocusStep"/>): VISCA has no focus position the app nudges, so
    /// it sends nothing.
    /// </summary>
    public abstract void FocusStep(double focusShare, FocusDir focusDir);

    /// <summary>
    /// True when the last pan/tilt, zoom or focus command the app sent was a move rather than a
    /// stop, i.e. the app believes it is driving the camera. Preset recalls are not app-driven
    /// motion and do not count.
    /// </summary>
    public bool IsMoving =>
        panDir != PanDir.Stop
        || tiltDir != TiltDir.Stop
        || (zoomCmd & 0xf0) != (byte)ZoomDir.Stop
        || (focusCmd & 0xf0) != (byte)FocusDir.Stop;
}
