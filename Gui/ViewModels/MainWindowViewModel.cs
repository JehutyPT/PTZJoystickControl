using Avalonia.Layout;
using Avalonia.Threading;
using PtzJoystickControl.Core.Services;
using PtzJoystickControl.Gui.Views;
using System;
using System.Runtime.InteropServices;

namespace PtzJoystickControl.Gui.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public GamepadsViewModel GamepadsViewModel { get; }
    public CamerasViewModel CamerasViewModel { get; }

    private readonly IBitfocusCompanionService _companion;
    private string? _companionInboundWarning;

    public MainWindowViewModel(GamepadsViewModel gamepadsViewModel, CamerasViewModel camerasViewModel, MainWindow window, IBitfocusCompanionService companion)
    {
        _companion = companion;
        // Raised on a thread-pool thread when a listener start retry succeeds; bindings need the UI thread.
        // Subscribed before the first read, so a retry that succeeds in between is not missed.
        companion.InboundListenerErrorChanged += (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                UpdateCompanionInboundWarning();
            else
                Dispatcher.UIThread.Post(UpdateCompanionInboundWarning);
        };
        _companionInboundWarning = WarningFor(companion.InboundListenerError);
        GamepadsViewModel = gamepadsViewModel;
        CamerasViewModel = camerasViewModel;
        AcrylicEnabled = window?.ActualTransparencyLevel == Avalonia.Controls.WindowTransparencyLevel.AcrylicBlur
            || window?.ActualTransparencyLevel == Avalonia.Controls.WindowTransparencyLevel.Blur;
    }

    public bool AcrylicEnabled { get; }

    /// <summary>Shown in the main window while Companion requests cannot reach the app; null otherwise.</summary>
    public string? CompanionInboundWarning
    {
        get => _companionInboundWarning;
        private set => RaiseAndSetIfChanged(ref _companionInboundWarning, value);
    }

    private void UpdateCompanionInboundWarning() => CompanionInboundWarning = WarningFor(_companion.InboundListenerError);

    private static string? WarningFor(string? companionInboundError) => companionInboundError == null
        ? null
        : $"Companion → app control is off, retrying automatically. {companionInboundError}";

    public HorizontalAlignment TitleHorizontalAlignment { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
        ? HorizontalAlignment.Left 
        : HorizontalAlignment.Center;
}
