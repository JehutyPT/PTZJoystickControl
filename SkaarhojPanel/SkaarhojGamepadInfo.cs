using PtzJoystickControl.Core.Devices;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>The panel's entry in the controller list. Connected while the link is up and the panel is awake.</summary>
internal sealed class SkaarhojGamepadInfo : IGamepadInfo
{
    private bool isActivated;
    private bool isConnected;

    public SkaarhojGamepadInfo(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
