using PtzJoystickControl.Core.Devices;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// The panel's entry in the controller list. Connected while the link is up; asleep while the link is
/// up and the panel has reported <c>_isSleeping=1</c>. Both are set on the input loop and announced
/// through <c>notifyOnUiThread</c>, as the controller list binds them (live-safety §4).
/// </summary>
internal sealed class SkaarhojGamepadInfo : IGamepadInfo
{
    private readonly Action<Action> notifyOnUiThread;
    private bool isActivated;
    private volatile bool isConnected;
    private volatile bool isAsleep;

    public SkaarhojGamepadInfo(string id, string name, Action<Action> notifyOnUiThread)
    {
        Id = id;
        Name = name;
        this.notifyOnUiThread = notifyOnUiThread;
    }

    public string Id { get; }
    public string Name { get; }

    // Raised at once: the controller list and the controller page act on it straight away.
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
            NotifyPropertyChangedOnUiThread();
        }
    }

    public bool IsAsleep
    {
        get => isAsleep;
        set
        {
            isAsleep = value;
            NotifyPropertyChangedOnUiThread();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void NotifyPropertyChangedOnUiThread([CallerMemberName] string propertyName = "") =>
        notifyOnUiThread(() => NotifyPropertyChanged(propertyName));
}
