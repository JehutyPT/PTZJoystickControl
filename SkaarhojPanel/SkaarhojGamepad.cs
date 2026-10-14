using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// The SKAARHOJ panel's joystick and zoom rocker as a controller: four <see cref="InputType.Axis"/>
/// inputs fed from <c>HWC#&lt;id&gt;=Speed:&lt;n&gt;</c> messages (docs/skaarhoj-integration.md, "HWC map").
/// Every other HWC is ignored: buttons, encoders and lights belong to the Companion surface plugin.
/// <para>
/// One instance lives from activation (or link up, or wake) to deactivation (or link down, or sleep):
/// <see cref="DrivenCameras"/> is terminal once stopped, so a new instance is made each time, with
/// every axis at 0. <see cref="OnSpeed"/> runs on the input loop only.
/// </para>
/// </summary>
public sealed class SkaarhojGamepad : IGamepad
{
    public const string IdPrefix = "skaarhoj:";

    /// <summary>Full deflection in a <c>Speed</c> message.</summary>
    public const int FullSpeed = 500;

    // Display order: stick left/right, up/down, twist, then the zoom rocker.
    private static readonly (int Hwc, string Name)[] Axes =
    {
        (60, "LR"),
        (61, "UD"),
        (62, "Rotate"),
        (58, "Zoom"),
    };

    private readonly List<ICommand> commands;
    private readonly Dictionary<int, IInput> inputsByHwc = new();
    private readonly List<IInput> inputs = new();
    private bool isActivated;
    private bool isConnected = true;
    private volatile bool unacquired;
    private ViscaDeviceBase? selectedCamera;
    private ObservableCollection<ViscaDeviceBase>? cameras;

    internal SkaarhojGamepad(string id, string name, ICommandsService commandsService, ObservableCollection<ViscaDeviceBase> cameras)
    {
        Id = id;
        Name = name;
        Cameras = cameras;
        commands = commandsService.GetCommandsForGamepad(this).ToList();
        DrivenCameras = new DrivenCameras(this);

        foreach (var (hwc, axisName) in Axes)
        {
            IInput input = new Input($"HWC{hwc}", axisName, InputType.Axis, commands.AsReadOnly());
            input.PersistentPropertyChanged += (_, _) => NotifyPersistentPropertyChanged("");
            inputsByHwc.Add(hwc, input);
            inputs.Add(input);
        }
    }

    /// <summary>The controller id for a panel at <paramref name="address"/>.</summary>
    public static string IdFor(string address) => IdPrefix + address;

    /// <summary>
    /// Maps a <c>Speed</c> value to -1..1. Values outside -500..500 are clamped:
    /// <see cref="Input.InputValue"/> throws outside -1..1.
    /// </summary>
    public static float Scale(int speed) => Math.Clamp(speed / (float)FullSpeed, -1f, 1f);

    public string Id { get; }
    public string Name { get; }
    public bool DetectInput { get; set; }

    public bool IsActivated
    {
        get => isActivated;
        set
        {
            if (isActivated != value)
            {
                isActivated = value;
                NotifyPersistentPropertyChanged();
            }
            NotifyPropertyChanged();
        }
    }

    public bool IsConnected
    {
        get => isConnected;
        set
        {
            isConnected = value;
            NotifyPropertyChanged();
        }
    }

    public ObservableCollection<ViscaDeviceBase>? Cameras
    {
        get => cameras;
        set
        {
            cameras = value;
            NotifyPropertyChanged();
        }
    }

    public ViscaDeviceBase? SelectedCamera
    {
        get => selectedCamera;
        set
        {
            if (selectedCamera != value)
            {
                selectedCamera = value;
                NotifyPropertyChanged();
            }
        }
    }

    public IReadOnlyList<ICommand> Commands => commands.AsReadOnly();

    public IEnumerable<IInput> Inputs => inputs;

    /// <summary>Cameras this gamepad has had selected; stopped when the panel goes away or it is deactivated.</summary>
    internal DrivenCameras DrivenCameras { get; }

    /// <summary>
    /// Applies <c>HWC#<paramref name="hwc"/>=Speed:<paramref name="speed"/></c>. Any other HWC, and
    /// anything after <see cref="Unacquire"/>, is ignored. Input loop only.
    /// </summary>
    internal void OnSpeed(int hwc, int speed)
    {
        if (unacquired)
            return;
        if (inputsByHwc.TryGetValue(hwc, out var input))
            input.InputValue = Scale(speed);
    }

    public void Acquire()
    {
    }

    public void Unacquire()
    {
        unacquired = true;

        // Never reused after this (see the class summary): detach the commands from shared state
        // such as the camera list. Runs on the disconnect path, so a failure is logged, not thrown.
        foreach (var command in commands)
        {
            try
            {
                (command as IDisposable)?.Dispose();
            }
            catch (Exception e)
            {
                Trace.TraceWarning($"Gamepad '{Name}': failed to dispose command '{command.CommandName}': {e}");
            }
        }
    }

    public void Update()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PersistentPropertyChanged;
    private void NotifyPersistentPropertyChanged([CallerMemberName] string propertyName = "") =>
        PersistentPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
