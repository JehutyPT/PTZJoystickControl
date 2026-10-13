using Avalonia;
using Avalonia.Platform;
using Avalonia.ReactiveUI;
using PtzJoystickControl.Gui.ViewModels;
using PtzJoystickControl.Gui.TrayIcon;
using PtzJoystickControl.Application;
using PtzJoystickControl.Application.Db;
using PtzJoystickControl.Application.Logging;
using PtzJoystickControl.Application.Services;
using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Services;
using PtzJoystickControl.SdlGamepads.Services;
using PtzJoystickControl.SkaarhojPanel;
using Splat;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Octokit;
using System.Reflection;
using PtzJoystickControl.Core.Model;

namespace PtzJoystickControl.Gui;

internal class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // First, so that anything failing from here on is logged (see docs/development.md#logs).
        var log = new AppLogListener();
        Trace.Listeners.Add(log);
        AppDomain.CurrentDomain.UnhandledException += log.OnUnhandledException;
        TaskScheduler.UnobservedTaskException += log.OnUnobservedTaskException;

        string settingsDirectory = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".PTZJoystickControl/")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PTZJoystickControl/");
        Directory.CreateDirectory(settingsDirectory);
        Debug.AutoFlush = true;

        var appBuilder = BuildAvaloniaApp();

        // Mutex to ensure only one instance runs; a second launch shows the "already running" window.
        var mutex = SingleInstance.CreateMutex();
        try
        {
            if (!SingleInstance.TryAcquire(mutex, out bool abandoned))
            {
                appBuilder.StartWithClassicDesktopLifetime(new string[] { "-r" }, Avalonia.Controls.ShutdownMode.OnMainWindowClose);
                return;
            }

            // Only the instance that owns the mutex writes the log file.
            var logFile = new RollingLogFile(settingsDirectory);
            log.AttachFile(logFile);
            Trace.TraceInformation($"PTZJoystickControl {Assembly.GetExecutingAssembly().GetName().Version} started; log file {logFile.Path}");
            // Logged after AttachFile so the warning reaches the log file.
            if (abandoned)
                Trace.TraceWarning("The previous PTZJoystickControl instance did not exit cleanly (crashed or was killed); starting normally");

            var companion = RegisterServices(log);
            // Null, and silent, when SkaarhojPanel.json names no panel.
            var panel = SkaarhojPanelLink.StartIfConfigured(Path.Combine(settingsDirectory, SkaarhojPanelSettingsStore.FileName));

            appBuilder.StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            // Also cancels a pending listener start retry.
            companion.StopListening();
            panel?.Dispose();
            Trace.TraceInformation("PTZJoystickControl stopped");
            // Release on this (owning) thread so a clean quit is not seen as abandoned by the next
            // launch while another process still has the mutex open. Not in finally: a crash
            // during startup should still read as abandoned.
            mutex.ReleaseMutex();
        }
        finally
        {
            mutex?.Close();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI();

    private static BitfocusCompanionService RegisterServices(AppLogListener log)
    {
        var services = Locator.CurrentMutable;
        var resolver = Locator.Current;
        var avaloniaLocator = AvaloniaLocator.Current;

        services.Register<IGitHubClient>(() => new GitHubClient(new ProductHeaderValue("PTZJoystickControl-UpdateChecker")));
        services.Register<IUpdateService>(() => new UpdateService(
            resolver.GetServiceOrThrow<IGitHubClient>(),
            "JehutyPT",
            "PTZJoystickControl",
            Assembly.GetExecutingAssembly().GetName().Version!));

        services.RegisterConstant<ICameraSettingsStore>(new CameraSettingsStore());
        services.RegisterConstant<IGamepadSettingsStore>(new GamepadSettingsStore());

        // Before the Companion service: an inbound request selects a camera by its Companion number.
        services.RegisterConstant<ICamerasService>(new CamerasService(
            resolver.GetServiceOrThrow<ICameraSettingsStore>()));

        // Read once: a change to Companion.json takes effect on the next start.
        var companionSettings = new CompanionSettingsStore().Load();
        Trace.TraceInformation($"Companion: sending to {companionSettings.OutboundBaseAddress}, listening on http://localhost:{companionSettings.ListenerPort}/");
        var companion = new BitfocusCompanionService(companionSettings, resolver.GetServiceOrThrow<ICamerasService>());
        services.RegisterConstant<IBitfocusCompanionService>(companion);

        services.RegisterConstant<ICommandsService>(new CommandsService(
            resolver.GetServiceOrThrow<IBitfocusCompanionService>()));

        services.RegisterConstant<IGamepadsService>(new SdlGamepadsService(
            resolver.GetServiceOrThrow<IGamepadSettingsStore>(),
            resolver.GetServiceOrThrow<ICamerasService>(),
            resolver.GetServiceOrThrow<ICommandsService>(),
            resolver.GetServiceOrThrow<IBitfocusCompanionService>()));

        services.RegisterLazySingleton(() => new GamepadsViewModel(
            resolver.GetServiceOrThrow<IGamepadsService>()));
        services.Register(() => new CamerasViewModel(
            resolver.GetServiceOrThrow<ICamerasService>(),
            resolver.GetServiceOrThrow<GamepadsViewModel>()));
        services.RegisterConstant(log);
        services.RegisterLazySingleton(() => new LogWindowViewModel(
            resolver.GetServiceOrThrow<AppLogListener>()));
        services.RegisterLazySingleton(() => new TrayIconHandler(
            avaloniaLocator.GetServiceOrThrow<IAssetLoader>()));

        return companion;
    }
}

internal static class ResolverExtension
{
    internal static T GetServiceOrThrow<T>(this IReadonlyDependencyResolver resolver)
    {
        return resolver.GetService<T>()
            ?? throw new Exception("Resolved dependency cannot be null");
    }

    internal static T GetServiceOrThrow<T>(this IAvaloniaDependencyResolver resolver)
    {
        return resolver.GetService<T>()
            ?? throw new Exception("Resolved dependency cannot be null");
    }
}
