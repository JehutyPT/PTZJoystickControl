using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Services;
using PtzJoystickControl.SdlGamepads.Models;
using SDL2;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PtzJoystickControl.SdlGamepads.Devices;

public class SdlGamepad : IGamepad
{
    private static readonly string[] AxisNames = new string[] {
        "Axis X",
        "Axis Y",
        "Axis Z",
        "Axis 1",
        "Axis 2",
        "Axis 3",
        "Axis 4",
        "Axis 5",
    };
    public const string ButtonNameFormatString = "Button {0}";
    private List<ICommand> commands;
    private bool isActivated;
    private bool isConnected;

    public string Id { get; }
    public string Name { get; }
    internal IntPtr sdlJoystickPointer;
    internal int DeviceIndex { get; set; }
    internal int InstanceId{ get; set; }
    public bool DetectInput { get; set; } = false;

    private readonly Dictionary<string, IInput> inputs = new();
    private ViscaDeviceBase? selectedCamera;
    private ObservableCollection<ViscaDeviceBase>? cameras;

    public bool IsActivated { 
        get => isActivated;
        set {
            if (isActivated != value)
            {
                isActivated = value;
                NotifyPersistentPropertyChanged();
            }
            NotifyPropertyChanged();
        }
    }

    public bool IsConnected { 
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

    public IEnumerable<IInput> Inputs => inputs.Values;

    internal SdlGamepad(SdlGamepadInfo gamepadInfo, ICommandsService commandsService, ObservableCollection<ViscaDeviceBase> cameras)
    {
        Name = gamepadInfo.Name;
        Cameras = cameras;

        commands = commandsService.GetCommandsForGamepad(this).ToList();

        Id = gamepadInfo.Id;
        DeviceIndex = gamepadInfo.DeviceIndex;
        InstanceId = gamepadInfo.InstanceId;
        sdlJoystickPointer = SDL.SDL_JoystickOpen(DeviceIndex);
        
        if (sdlJoystickPointer == IntPtr.Zero)
            throw new Exception("Failed to open joystick");

        //Get Axis inputs
        var numAxis = SDL.SDL_JoystickNumAxes(sdlJoystickPointer);
        for (int i = 0; i < numAxis; i++)
        {
            IInput newInput = new Input(AxisNames[i], AxisNames[i], InputType.Axis, commands.AsReadOnly());
            newInput.PersistentPropertyChanged += (sender, e) => NotifyPersistentPropertyChanged("");
            inputs.Add(AxisNames[i], newInput);
        }

        //Get Button inputs
        var numButtons = SDL.SDL_JoystickNumButtons(sdlJoystickPointer);
        for (int i = 0; i < numButtons; i++)
        {
            var id = string.Format(ButtonNameFormatString, i + 1);
            var name = string.Format(ButtonNameFormatString, i);
            IInput newInput = new Input(id, name, InputType.Button, commands.AsReadOnly());
            newInput.PersistentPropertyChanged += (sender, e) => NotifyPersistentPropertyChanged("");
            inputs.Add(name, newInput);
        }

        //for (int i = 0; i < Capabilities.PovCount; i++)
        //{
        //    JoystickOffset offset = Enum.Parse<JoystickOffset>("PointOfViewControllers" + i);
        //    try
        //    {
        //        DeviceObjectInstance inputObj = GetObjectInfoByName(offset.ToString());
        //        IInput newInput = new Input(inputObj.Name, InputType.Button, (int)offset, commands.AsReadOnly());
        //        newInput.PersistentPropertyChanged += (sender, e) => NotifyPersistentPropertyChanged("");
        //        inputs.Add((int)offset, newInput);
        //    }
        //    catch { }
        //}

        //SDL.SDL_JoystickNumAxes(j)
        //SDL.SDL_JoystickNumButtons(j)
        //SDL.SDL_JoystickNumHats(j)
        //SDL.SDL_JoystickNumBalls(j)
    }

    public void AddInput(string id, IInput input) => inputs.Add(id, input);

    public void Acquire()
    {
    }

    public void Unacquire()
    {
        SDL.SDL_JoystickClose(sdlJoystickPointer);

        // A gamepad is never reused after Unacquire (reconnecting or reactivating loads a new
        // one), so detach its commands from shared state such as the camera list. Unacquire runs
        // on the disconnect path, so a failure here is logged rather than thrown.
        foreach (var command in commands)
        {
            try
            {
                (command as IDisposable)?.Dispose();
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Gamepad '{Name}': failed to dispose command '{command.CommandName}': {e}");
            }
        }
    }

    public void Update()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public event PropertyChangedEventHandler? PersistentPropertyChanged;
    private void NotifyPersistentPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PersistentPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal void OnButtonEvent(SDL.SDL_JoyButtonEvent jbutton)
    {
        if (inputs.TryGetValue(string.Format(ButtonNameFormatString, jbutton.button), out var input))
            input.InputValue = jbutton.state;
    }

    internal void OnAxisEvent(SDL.SDL_JoyAxisEvent jaxis)
    {
        if (inputs.TryGetValue(AxisNames[jaxis.axis], out var input))
            input.InputValue = Util.Map(jaxis.axisValue, short.MinValue, short.MaxValue, -1, 1);
    }
}
