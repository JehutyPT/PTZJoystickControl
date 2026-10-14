using System.ComponentModel;

namespace PtzJoystickControl.Core.Devices;

public interface IGamepadInfo : INotifyPropertyChanged
{
    public string Id { get; }
    public string Name { get; }
    bool IsActivated { get; set; }
    bool IsConnected { get; set; }

    /// <summary>
    /// Connected but asleep, so its input does nothing until it is woken (the SKAARHOJ panel).
    /// The controller list binds it, so a source that can sleep implements it as a public property.
    /// </summary>
    bool IsAsleep => false;
}
