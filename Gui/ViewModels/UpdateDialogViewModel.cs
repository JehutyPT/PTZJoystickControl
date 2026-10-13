using Avalonia.Controls;
using Avalonia.Layout;
using PtzJoystickControl.Core.Model;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PtzJoystickControl.Gui.ViewModels;

public class UpdateDialogViewModel : ViewModelBase
{
    private readonly Update _update;
    public UpdateDialogViewModel(Update upate, Window? window)
    {
        _update = upate;
        AcrylicEnabled = window?.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
            || window?.ActualTransparencyLevel == WindowTransparencyLevel.Blur;
    }

    public bool AcrylicEnabled { get; }
    public HorizontalAlignment TitleHorizontalAlignment { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? HorizontalAlignment.Left
        : HorizontalAlignment.Center;

    public bool IsAvailable { get => _update.Available; }
    public string? LatestVersion { get => _update.Version; }
    public bool HasLatestVersion { get => !string.IsNullOrEmpty(LatestVersion); }
    public bool Error { get => _update.Error; }
    public string? ErrorMessage { get => _update.ErrorMessage; }
    public bool NotAvailableOrError { get => !(IsAvailable || Error); }
    public void Download()
    {
        if (_update.Uri != null) Process.Start(new ProcessStartInfo
        {
            FileName = _update.Uri.ToString(),
            UseShellExecute = true
        });
    }
}
