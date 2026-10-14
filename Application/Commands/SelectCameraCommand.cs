using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Application.Commands;

public class SelectCameraCommand : IStaticCommand, INotifyPropertyChanged, INotifyCollectionChanged, IDisposable
{
    public SelectCameraCommand(IGamepad gamepad, IBitfocusCompanionService companionService) : base(gamepad, companionService)
    {
        Cameras = gamepad.Cameras ?? new ObservableCollection<ICamera>();
        gamepad.PropertyChanged += Gamepad_PropertyChanged;
    }

    private void Gamepad_PropertyChanged(object? sender, PropertyChangedEventArgs? e)
    {
        if (e?.PropertyName == nameof(Gamepad.Cameras))
            Cameras = Gamepad.Cameras ?? new ObservableCollection<ICamera>();
    }

    private ObservableCollection<ICamera> cameras = null!;
    // The cameras whose renames this command follows, one entry per subscription. A clear raises
    // Reset with no OldItems, so this is the only record of the cameras the clear removed. Locked:
    // the camera list is edited on the UI thread, but a controller going away disposes this
    // command on the input loop.
    private readonly List<ICamera> subscribedCameras = new();
    private bool disposed;

    private ObservableCollection<ICamera> Cameras
    {
        get { return cameras; }
        set
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            UnsubscribeFromCameras();

            cameras = value;
            
            cameras.CollectionChanged += OnCamerasChanged;
            foreach (var camera in cameras)
                Subscribe(camera);
            NotifyPropertyChanged();
        }
    }

    public void OnCamerasChanged(object? o, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Move && (e.OldItems?.Contains(Gamepad.SelectedCamera) ?? false))
            Gamepad.SelectedCamera = null;

        if (e != null)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                UnsubscribeFromRenames();

            if (e.OldStartingIndex >= 0)
                foreach (ICamera device in e.OldItems!) Unsubscribe(device);

            if (e.NewStartingIndex >= 0)
                foreach (ICamera device in e.NewItems!) Subscribe(device);
        }

        NotifyCollectionChanged(e!);
    }

    private void OnDeviceChange(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ICamera.Name))
            NotifyPropertyChanged(nameof(Options));
    }

    private void Subscribe(ICamera camera)
    {
        camera.PropertyChanged += OnDeviceChange;
        lock (subscribedCameras)
            subscribedCameras.Add(camera);
    }

    private void Unsubscribe(ICamera camera)
    {
        camera.PropertyChanged -= OnDeviceChange;
        lock (subscribedCameras)
            subscribedCameras.Remove(camera);
    }

    private void UnsubscribeFromRenames()
    {
        ICamera[] toUnsubscribe;
        lock (subscribedCameras)
        {
            toUnsubscribe = subscribedCameras.ToArray();
            subscribedCameras.Clear();
        }

        foreach (var camera in toUnsubscribe)
            camera.PropertyChanged -= OnDeviceChange;
    }

    private void UnsubscribeFromCameras()
    {
        if (cameras == null)
            return;

        cameras.CollectionChanged -= OnCamerasChanged;
        UnsubscribeFromRenames();
    }

    // Called when the gamepad goes away (unplugged or deactivated). Detaches from the shared
    // camera list and the gamepad so a dead gamepad's command stops reacting to changes.
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        Gamepad.PropertyChanged -= Gamepad_PropertyChanged;
        UnsubscribeFromCameras();
    }

    public override string CommandName => "Select camera";

    public override string AxisParameterName => "Camera";

    public override string ButtonParameterName => "Camera";

    public override IEnumerable<CommandValueOption> Options => Cameras
        .Select((val, i) => new CommandValueOption(val.Name, i));


    public override void Execute(int value)
    {
        Gamepad.SelectedCamera = 0 <= value && value < Cameras.Count() ? Cameras[value] : throw new ArgumentOutOfRangeException(
            $"Value out of range for Camera ObservableCollection. Count is {Cameras.Count()}, value was {value}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected void NotifyCollectionChanged(NotifyCollectionChangedEventArgs collectionChangeEventArgs)
    {
        CollectionChanged?.Invoke(this, collectionChangeEventArgs);
    }
}
