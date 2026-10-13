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
            if (!mutex.WaitOne(0, false))
            {
                appBuilder.StartWithClassicDesktopLifetime(new string[] { "-r" }, Avalonia.Controls.ShutdownMode.OnMainWindowClose);
                return;
            }

            // Only the instance that owns the mutex writes the log file.
            var logFile = new RollingLogFile(settingsDirectory);
            log.AttachFile(logFile);
            Trace.TraceInformation($"PTZJoystickControl {Assembly.GetExecutingAssembly().GetName().Version} started; log file {logFile.Path}");

            RegisterServices(log);

            appBuilder.StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            Trace.TraceInformation("PTZJoystickControl stopped");
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

    private static void RegisterServices(AppLogListener log)
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

        services.RegisterConstant<IBitfocusCompanionService>(new BitfocusCompanionService(8000, 12345));
        services.RegisterConstant<ICameraSettingsStore>(new CameraSettingsStore());
        services.RegisterConstant<IGamepadSettingsStore>(new GamepadSettingsStore());
        
        services.RegisterConstant<ICommandsService>(new CommandsService(
            resolver.GetServiceOrThrow<IBitfocusCompanionService>()));

        services.RegisterConstant<ICamerasService>(new CamerasService(
            resolver.GetServiceOrThrow<ICameraSettingsStore>()));

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
