using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PtzJoystickControl.Gui.ViewModels;
using PtzJoystickControl.Gui.Views;
using PtzJoystickControl.Gui.TrayIcon;
using Splat;
using System;
using System.ComponentModel;
using System.Linq;
using PtzJoystickControl.Core.Services;

namespace PtzJoystickControl.Gui
{
    public partial class App : Avalonia.Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            UiThread.MarkReady();

            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            if (desktop.Args?.Contains("-r") == true)
            {
                desktop.MainWindow = new InstanceRunningWindow();
                return;
            }

            var camerasViewModel = Locator.Current.GetServiceOrThrow<CamerasViewModel>();

            var mainWindow = new MainWindow();
            var mainWindowViewModel = new MainWindowViewModel(
                Locator.Current.GetServiceOrThrow<GamepadsViewModel>(),
                camerasViewModel,
                Locator.Current.GetServiceOrThrow<SpeedProfilesViewModel>(),
                mainWindow,
                Locator.Current.GetServiceOrThrow<IBitfocusCompanionService>());
            mainWindow.DataContext = mainWindowViewModel;

            mainWindow.Closing += (object? s, WindowClosingEventArgs e) =>
            {   // Prevent closing to keep application running in background
                if (IsAppOrOsShutdown(e))
                    return;
                mainWindow.Hide();
                e.Cancel = true;
            };

            var trayIconHandler = Locator.Current.GetServiceOrThrow<TrayIconHandler>();
            trayIconHandler.OnShowClicked += (object? s, EventArgs e) => ShowMainWindow();
            trayIconHandler.OnUpdateCheckClicked += (object? s, EventArgs e) => CheckForUpdate(true);
            trayIconHandler.OnShowLogClicked += (object? s, EventArgs e) => ShowLogWindow();
            trayIconHandler.OnQuitClicked += (object? s, EventArgs e) => desktop.Shutdown();

            camerasViewModel.PropertyChanged += (object? s, PropertyChangedEventArgs e) =>
            {
                if (e.PropertyName == nameof(CamerasViewModel.SelectedCamera))
                {
                    var updateIcon = () => trayIconHandler.UpdateIcon(camerasViewModel.Cameras.IndexOf(camerasViewModel.SelectedCamera!) + 1);
                    if (Dispatcher.UIThread.CheckAccess())
                        updateIcon();
                    else
                        Dispatcher.UIThread.InvokeAsync(updateIcon);
                }
            };

            void onStartup(object? s, ControlledApplicationLifetimeStartupEventArgs e)
            {
                if (e.Args.Contains("-m"))
                    mainWindow.WindowState = WindowState.Minimized;

                CheckForUpdate();

                desktop.Startup -= onStartup;
            };

            void onExit(object? s, ControlledApplicationLifetimeExitEventArgs e)
            {
                trayIconHandler.Exit(s, e);
            }

            desktop.Startup += onStartup;
            desktop.Exit += onExit;

            desktop.MainWindow = mainWindow;

            base.OnFrameworkInitializationCompleted();
        }

        private void ShowMainWindow()
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            var mainWindow = desktop.MainWindow;
            if (mainWindow == null)
                return;

            mainWindow.Show();
            if (mainWindow.WindowState == WindowState.Minimized)
                mainWindow.WindowState = WindowState.Normal;
            else
            {   // Force mainWindow to be on top.
                mainWindow.Topmost = true;
                mainWindow.Topmost = false;
            }
        }

        // Since Avalonia 11, quitting (tray menu → Quit) and OS shutdown raise Closing on every
        // window too; only a user closing the window hides it instead.
        private static bool IsAppOrOsShutdown(WindowClosingEventArgs e) =>
            e.CloseReason == WindowCloseReason.ApplicationShutdown
            || e.CloseReason == WindowCloseReason.OSShutdown;

        private LogWindow? _logWindow;

        private void ShowLogWindow()
        {
            if (_logWindow == null)
            {
                _logWindow = new LogWindow
                {
                    DataContext = Locator.Current.GetServiceOrThrow<LogWindowViewModel>()
                };
                var logWindow = _logWindow;
                logWindow.Closing += (object? s, WindowClosingEventArgs e) =>
                {   // Hide instead of closing so the window keeps its log and can be reopened.
                    if (IsAppOrOsShutdown(e))
                        return;
                    logWindow.Hide();
                    e.Cancel = true;
                };
            }

            _logWindow.Show();
            if (_logWindow.WindowState == WindowState.Minimized)
                _logWindow.WindowState = WindowState.Normal;
            _logWindow.Activate();
        }

        private void CheckForUpdate(bool showModalWhenNotAvailable = false)
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            var updateTask = Locator.Current.GetServiceOrThrow<IUpdateService>()
                .CheckForUpdate()
                .ContinueWith((task) =>
                {
                    var update = task.Result;
                    if (!showModalWhenNotAvailable && !update.Available)
                        return;

                    var showUpdateModal = () =>
                    {
                        var dialog = new UpdateDialogWindow();
                        dialog.DataContext = new UpdateDialogViewModel(update, dialog);
                        ShowMainWindow();
                        if (desktop.MainWindow != null)
                            dialog.ShowDialog(desktop.MainWindow);
                        else
                            dialog.Show();
                    };
                    if (Dispatcher.UIThread.CheckAccess())
                        showUpdateModal();
                    else
                        Dispatcher.UIThread.InvokeAsync(showUpdateModal);
                });
        }
    }
}
