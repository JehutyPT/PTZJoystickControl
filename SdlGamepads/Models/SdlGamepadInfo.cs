using PtzJoystickControl.Core.Devices;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PtzJoystickControl.SdlGamepads.Models;

internal class SdlGamepadInfo : IGamepadInfo
{
    private bool isConnected;
    private bool isActivated;

    public string Id { get; internal set; } = null!;
    public string Name { get; internal set;  } = null!;

    public int DeviceIndex { get; internal set; } = default;
    public int InstanceId { get; internal set; } = default;

    public bool IsActivated
    {
        get => isActivated;
        set
        {
            isActivated = value;
            NotifyPropertyChanged();
        }
    }

    public bool IsConnected
    {
        get => isConnected;
        set
        {
            isConnected = value;
            NotifyPropertyChanged();
        }
    }

    // Never asleep; public so the controller list's binding finds it on every entry.
    public bool IsAsleep => false;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
