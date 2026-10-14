using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace PtzJoystickControl.Application.Services;

public class CamerasService : ICamerasService
{
    private readonly ICameraSettingsStore _camerasDb;
    public ObservableCollection<ICamera> Cameras { get; } = new();

    public CamerasService(ICameraSettingsStore camerasDb)
    {
        _camerasDb = camerasDb;
        Cameras.CollectionChanged += CamerasCollectionChanged;
        LoadCamerasFromDb();
    }

    public void LoadCamerasFromDb()
    {
        var storedCameras = _camerasDb.GetAllCameras();
        // Iterate a snapshot: RemoveCamera changes Cameras.
        foreach (var camera in Cameras.ToList())
            RemoveCamera(camera);

        if (storedCameras != null)
        {
            var loaded = storedCameras.ToList();
            // Before adding, so a camera without a number cannot take one a later camera already has.
            CompanionNumbers.AssignMissing(loaded);
            foreach (var duplicate in CompanionNumbers.Duplicates(loaded))
                Trace.TraceWarning($"Companion number {duplicate.Key} is used by more than one camera ({string.Join(", ", duplicate.Select(c => c.Name))}); Companion cannot select them by it until it is changed");

            foreach (var camera in loaded)
                AddCamera(camera);
        }
    }

    public void AddCamera(ICamera camera)
    {
        camera.CompanionNumber ??= CompanionNumbers.NextFree(Cameras, preferred: Cameras.Count + 1);
        Cameras.Add(camera);
        camera.PersistentPropertyChanged += CameraPersistentPropertyChanged;
    }

    public void RemoveCamera(ICamera camera)
    {
        Cameras.Remove(camera);
        camera.PersistentPropertyChanged -= CameraPersistentPropertyChanged;
    }

    private void CameraPersistentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is ViscaDeviceBase)
            _camerasDb.SaveCameras(Cameras);
    }

    private void CamerasCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (sender == Cameras)
            _camerasDb.SaveCameras(Cameras);
    }
}
