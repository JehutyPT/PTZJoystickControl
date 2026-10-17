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
/// The SKAARHOJ panel's joystick, zoom rocker, Focus wheel and Iris wheel as a controller: four
/// <see cref="InputType.Axis"/> inputs fed from <c>HWC#&lt;id&gt;=Speed:&lt;n&gt;</c> messages, and two
/// <see cref="InputType.Relative"/> inputs fed from the wheels' steps, <c>HWC#59=Enc:&lt;±n&gt;</c> (Focus)
/// and <c>HWC#57=Enc:&lt;±n&gt;</c> (Iris) (docs/skaarhoj-integration.md, "HWC map"). Everything else is
/// ignored, the wheels' presses included: buttons, the other encoders and lights belong to the
/// Companion surface plugin.
/// <para>
/// One instance lives from activation (or link up, or wake) to deactivation (or link down, or sleep):
/// <see cref="DrivenCameras"/> is terminal once stopped, so a new instance is made each time, with
/// every axis at 0. <see cref="OnSpeed"/> and <see cref="OnEncoder"/> run on the input loop only.
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

    // Relative inputs, listed after the axes: the Focus wheel, then the Iris wheel.
    private static readonly (int Hwc, string Name)[] Encoders =
    {
        (59, "Focus"),
        (57, "Iris"),
    };

    private readonly List<ICommand> commands;
    private readonly Dictionary<int, IInput> axesByHwc = new();
    private readonly Dictionary<int, IInput> encodersByHwc = new();
    private readonly List<IInput> inputs = new();
    private bool isActivated;
    private bool isConnected = true;
    private volatile bool unacquired;
    private ICamera? selectedCamera;
    private ObservableCollection<ICamera>? cameras;

    internal SkaarhojGamepad(string id, string name, ICommandsService commandsService, ObservableCollection<ICamera> cameras)
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
            axesByHwc.Add(hwc, input);
            inputs.Add(input);
        }

        foreach (var (hwc, encoderName) in Encoders)
        {
            IInput input = new Input($"HWC{hwc}", encoderName, InputType.Relative, commands.AsReadOnly());
            input.PersistentPropertyChanged += (_, _) => NotifyPersistentPropertyChanged("");
            encodersByHwc.Add(hwc, input);
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

    public ObservableCollection<ICamera>? Cameras
    {
        get => cameras;
        set
        {
            cameras = value;
            NotifyPropertyChanged();
        }
    }

    public ICamera? SelectedCamera
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
        if (axesByHwc.TryGetValue(hwc, out var input))
            input.InputValue = Scale(speed);
    }

    /// <summary>
    /// Applies <c>HWC#<paramref name="hwc"/>=Enc:<paramref name="steps"/></c>: steps the Focus or Iris
    /// wheel's relative input. Any other HWC, and anything after <see cref="Unacquire"/>, is ignored. Input loop only.
    /// </summary>
    internal void OnEncoder(int hwc, int steps)
    {
        if (unacquired)
            return;
        if (encodersByHwc.TryGetValue(hwc, out var input))
            input.Step(steps);
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
