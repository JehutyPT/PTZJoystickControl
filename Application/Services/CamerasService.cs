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
        var loaded = _camerasDb.GetAllCameras()?.ToList();
        // Iterate a snapshot: TakeOut changes Cameras. A camera the store hands back as the same
        // instance is added back below, so it is not disposed.
        foreach (var camera in Cameras.ToList())
        {
            if (TakeOut(camera) && !(loaded?.Any(stored => ReferenceEquals(stored, camera)) ?? false))
                DisposeRemoved(camera);
        }

        if (loaded != null)
        {
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

    /// <summary>
    /// Takes the camera out of the list, then disposes it if it is <see cref="IDisposable"/>.
    /// Leaving the list clears it from every controller's selection, which stops it if a
    /// controller is driving it (<c>DrivenCameras</c>), so that stop comes before the dispose.
    /// </summary>
    public void RemoveCamera(ICamera camera)
    {
        if (TakeOut(camera))
            DisposeRemoved(camera);
    }

    private bool TakeOut(ICamera camera)
    {
        var removed = Cameras.Remove(camera);
        camera.PersistentPropertyChanged -= CameraPersistentPropertyChanged;
        return removed;
    }

    // Through the camera abstraction (ADR 0001), as the controllers dispose their commands.
    // Runs on the removal path, so a failure is logged, not thrown.
    private static void DisposeRemoved(ICamera camera)
    {
        try
        {
            (camera as IDisposable)?.Dispose();
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"Camera {camera.Name}: failed to dispose it after removing it: {e}");
        }
    }

    private void CameraPersistentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is ICamera)
            _camerasDb.SaveCameras(Cameras);
    }

    private void CamerasCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (sender == Cameras)
            _camerasDb.SaveCameras(Cameras);
    }
}
