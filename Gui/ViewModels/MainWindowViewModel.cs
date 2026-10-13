using Avalonia.Layout;
using PtzJoystickControl.Gui.Views;
using System.Runtime.InteropServices;

namespace PtzJoystickControl.Gui.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public GamepadsViewModel GamepadsViewModel { get; }
    public CamerasViewModel CamerasViewModel { get; }

    public MainWindowViewModel(GamepadsViewModel gamepadsViewModel, CamerasViewModel camerasViewModel, MainWindow window, string? companionInboundError)
    {
        CompanionInboundWarning = companionInboundError == null
            ? null
            : $"Companion → app control is off. {companionInboundError}";
        GamepadsViewModel = gamepadsViewModel;
        CamerasViewModel = camerasViewModel;
        AcrylicEnabled = window?.ActualTransparencyLevel == Avalonia.Controls.WindowTransparencyLevel.AcrylicBlur
            || window?.ActualTransparencyLevel == Avalonia.Controls.WindowTransparencyLevel.Blur;
    }

    public bool AcrylicEnabled { get; }

    /// <summary>Shown in the main window while Companion requests cannot reach the app; null otherwise.</summary>
    public string? CompanionInboundWarning { get; }

    public HorizontalAlignment TitleHorizontalAlignment { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
        ? HorizontalAlignment.Left 
        : HorizontalAlignment.Center;
}
