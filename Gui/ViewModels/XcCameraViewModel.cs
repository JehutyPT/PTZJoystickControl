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

/// <summary>
/// The settings panel of an XC camera: name, address, port, login, reachability, failure reason and
/// Companion number. The login typed in the panel is the camera's only once Log in applies it.
/// </summary>
public class XcCameraViewModel : ViewModelBase, INotifyPropertyChanged
{
    private readonly XcCameraBase _camera;
    private readonly IEnumerable<ICamera> _allCameras;
    // The login typed in the panel, which only LogIn applies; the camera's login until something is
    // typed. A rejected username is not kept: _usernameRejected says the field holds one.
    private string _username;
    private string _password;
    private bool _usernameRejected;

    /// <param name="allCameras">Every camera, to reject a Companion number another camera already has.</param>
    public XcCameraViewModel(XcCameraBase camera, IEnumerable<ICamera> allCameras)
    {
        _camera = camera;
        _allCameras = allCameras;
        _username = camera.Username;
        _password = camera.Password;
        WeakEventHandlerManager.Subscribe<INotifyPropertyChanged, PropertyChangedEventArgs, XcCameraViewModel>(camera, nameof(camera.PropertyChanged), OnCameraPropertyChanged);
    }

    // Connected and FailureReason changes are raised on the camera's send loop; bindings need the UI
    // thread. A login change also changes whether Log in has anything to apply.
    private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var propertyName = e?.PropertyName ?? "";
        UiThread.Post(() =>
        {
            NotifyPropertyChanged(propertyName);
            if (propertyName is nameof(XcCameraBase.Username) or nameof(XcCameraBase.Password))
                NotifyPropertyChanged(nameof(CanLogIn));
        });
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

    // Empty for guest control. Typing changes only the panel: LogIn applies it. Checked by the camera's
    // rule as it is typed; unlike the address, a rejected value is not logged: it is part of the login.
    public string Username
    {
        get => _username;
        set
        {
            value ??= "";
            _usernameRejected = !XcCameraBase.IsUsername(value);
            if (!_usernameRejected)
                _username = value;
            NotifyPropertyChanged(nameof(CanLogIn));
            if (_usernameRejected)
                throw new DataValidationException("No control characters");
        }
    }

    // Any text; never logged. Typing changes only the panel: LogIn applies it.
    public string Password
    {
        get => _password;
        set
        {
            _password = value ?? "";
            NotifyPropertyChanged(nameof(CanLogIn));
        }
    }

    // Whether LogIn would change the camera's login: the typed one differs from it, and its username is
    // valid.
    public bool CanLogIn { get => !_usernameRejected && (_username != _camera.Username || _password != _camera.Password); }

    // Applies the typed username and password together as the camera's login, which is saved. The
    // camera's next request carries it; the result shows in Connected and FailureReason.
    public void LogIn()
    {
        if (CanLogIn)
            _camera.SetLogin(_username, _password);
    }

    public bool Connected { get => _camera.Connected; }

    // Why the camera's requests fail, in the words of the log's warning; null while they succeed.
    public string? FailureReason { get => _camera.FailureReason; }

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
