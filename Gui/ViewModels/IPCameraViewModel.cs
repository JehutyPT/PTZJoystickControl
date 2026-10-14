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

public class IPCameraViewModel : ViewModelBase, INotifyPropertyChanged
{
    private readonly ViscaIPDeviceBase _camera;
    private readonly IEnumerable<ViscaDeviceBase> _allCameras;

    public static IEnumerable<Protocol> Protocols { get; } = Enum.GetValues<Protocol>();

    /// <param name="allCameras">Every camera, to reject a Companion number another camera already has.</param>
    public IPCameraViewModel(ViscaDeviceBase camera, IEnumerable<ViscaDeviceBase> allCameras)
    {
        _camera = (ViscaIPDeviceBase)camera;
        _allCameras = allCameras;
        WeakEventHandlerManager.Subscribe<INotifyPropertyChanged, PropertyChangedEventArgs, IPCameraViewModel>(camera, nameof(camera.PropertyChanged), OnCameraPropertyCahnged);
    }

    private void OnCameraPropertyCahnged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyPropertyChanged(e?.PropertyName ?? "");
    }

    public ViscaDeviceBase Camera { get => _camera; }

    public string Name
    {
        get => _camera.Name;
        set => _camera.Name = value;
    }


    public bool UseHeader
    {
        get => _camera.UseHeader;
        set => _camera.UseHeader = value;
    }

    public Protocol Protocol
    {
        get => _camera.Protocol;
        set => _camera.Protocol = value;
    }

    public int SendWaitTime
    {
        get => _camera.SendWaitTime;
        set
        {
            try
            {
                _camera.SendWaitTime = value;
            }
            catch (Exception e)
            {
                Trace.TraceInformation($"Camera {Name}: invalid SendWaitTime '{value}': {e.Message}");
                throw new DataValidationException(e.Message);
            }
        }
    }

    public string IPAddress
    {
        get => _camera.IPAddress;
        set
        {
            try
            {
                _camera.IPAddress = value;
            }
            catch (Exception e)
            {
                Trace.TraceInformation($"Camera {Name}: invalid IPAddress '{value}': {e.Message}");
                throw new DataValidationException("Invalid IP");
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
