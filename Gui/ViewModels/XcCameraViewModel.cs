using Avalonia.Data;
using Avalonia.Utilities;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Devices;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PtzJoystickControl.Gui.ViewModels;

/// <summary>The settings panel of an XC camera: name, address, port, reachability and Companion number.</summary>
public class XcCameraViewModel : ViewModelBase, INotifyPropertyChanged
{
    private readonly XcCameraBase _camera;
    private readonly IEnumerable<ICamera> _allCameras;

    /// <param name="allCameras">Every camera, to reject a Companion number another camera already has.</param>
    public XcCameraViewModel(XcCameraBase camera, IEnumerable<ICamera> allCameras)
    {
        _camera = camera;
        _allCameras = allCameras;
        WeakEventHandlerManager.Subscribe<INotifyPropertyChanged, PropertyChangedEventArgs, XcCameraViewModel>(camera, nameof(camera.PropertyChanged), OnCameraPropertyChanged);
    }

    // Connected changes are raised on the camera's send loop; bindings need the UI thread.
    private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var propertyName = e?.PropertyName ?? "";
        UiThread.Post(() => NotifyPropertyChanged(propertyName));
    }

    public ICamera Camera { get => _camera; }

    public string Name
    {
        get => _camera.Name;
        set => _camera.Name = value;
    }

    public string Address
    {
        get => _camera.Address;
        set
        {
            try
            {
                _camera.Address = value;
            }
            catch (Exception e)
            {
                Trace.TraceInformation($"Camera {Name}: invalid address '{value}': {e.Message}");
                throw new DataValidationException("Invalid address");
            }
        }
    }

    public int Port
    {
        get => _camera.Port;
        set
        {
            try
            {
                _camera.Port = value;
            }
            catch (Exception e)
            {
                Trace.TraceInformation($"Camera {Name}: invalid Port '{value}': {e.Message}");
                throw new DataValidationException("1-65535");
            }
        }
    }

    public bool Connected { get => _camera.Connected; }

    // Validated against every camera so Companion never sees two cameras with one number.
    public int? CompanionNumber
    {
        get => _camera.CompanionNumber;
        set
        {
            if (value == null || value < 1)
            {
                Trace.TraceInformation($"Camera {Name}: invalid Companion number '{value}'");
                throw new DataValidationException("1 or more");
            }

            var other = CompanionNumbers.UsedByAnotherCamera(_allCameras, _camera, value.Value);
            if (other != null)
            {
                Trace.TraceInformation($"Camera {Name}: Companion number {value} rejected, already used by {other.Name}");
                throw new DataValidationException($"Used by {other.Name}");
            }

            _camera.CompanionNumber = value;
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public override string ToString()
    {
        return Name;
    }
}
