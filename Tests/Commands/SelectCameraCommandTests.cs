using System.Collections.ObjectModel;
using System.ComponentModel;
using PtzJoystickControl.Application.Commands;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Commands;

public class SelectCameraCommandTests
{
    [Fact]
    public void EachCommand_OffersOnlyItsOwnGamepadsCameras()
    {
        var gamepadA = new TestGamepad(new ObservableCollection<ViscaDeviceBase> { new ViscaIpDevice("Cam A") });
        var gamepadB = new TestGamepad(new ObservableCollection<ViscaDeviceBase> { new ViscaIpDevice("Cam B1"), new ViscaIpDevice("Cam B2") });
        var commandA = new SelectCameraCommand(gamepadA, new NullCompanionService());
        var commandB = new SelectCameraCommand(gamepadB, new NullCompanionService());

        Assert.Equal(new[] { "Cam A" }, commandA.Options.Select(o => o.Name));
        Assert.Equal(new[] { "Cam B1", "Cam B2" }, commandB.Options.Select(o => o.Name));
    }

    [Fact]
    public void RemovingTheSelectedCamera_AfterDispose_OnlyClearsTheLiveGamepadsSelection()
    {
        var camera = new ViscaIpDevice("Cam 1");
        var cameras = new ObservableCollection<ViscaDeviceBase> { camera, new ViscaIpDevice("Cam 2") };
        var removedGamepad = new TestGamepad(cameras) { SelectedCamera = camera };
        var liveGamepad = new TestGamepad(cameras) { SelectedCamera = camera };
        var removedCommand = new SelectCameraCommand(removedGamepad, new NullCompanionService());
        _ = new SelectCameraCommand(liveGamepad, new NullCompanionService());
        var removedCommandCollectionChanges = 0;
        removedCommand.CollectionChanged += (_, _) => removedCommandCollectionChanges++;

        removedCommand.Dispose();
        cameras.Remove(camera);

        Assert.Same(camera, removedGamepad.SelectedCamera);
        Assert.Null(liveGamepad.SelectedCamera);
        Assert.Equal(0, removedCommandCollectionChanges);
    }

    [Fact]
    public void AfterDispose_CameraRenamesAndAddedCamerasAreIgnored()
    {
        var camera = new ViscaIpDevice("Cam 1");
        var cameras = new ObservableCollection<ViscaDeviceBase> { camera };
        var command = new SelectCameraCommand(new TestGamepad(cameras), new NullCompanionService());
        var notifications = 0;
        command.PropertyChanged += (_, _) => notifications++;
        command.CollectionChanged += (_, _) => notifications++;

        command.Dispose();
        camera.Name = "Renamed";
        var added = new ViscaIpDevice("Cam 2");
        cameras.Add(added);
        added.Name = "Renamed too";

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void AfterDispose_SelectingACamera_DoesNotThrow()
    {
        var camera = new ViscaIpDevice("Cam 1");
        var gamepad = new TestGamepad(new ObservableCollection<ViscaDeviceBase> { camera });
        var command = new SelectCameraCommand(gamepad, new NullCompanionService());

        command.Dispose();

        Assert.Null(Record.Exception(() => gamepad.SelectedCamera = camera));
        Assert.Same(camera, gamepad.SelectedCamera);
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        var command = new SelectCameraCommand(
            new TestGamepad(new ObservableCollection<ViscaDeviceBase> { new ViscaIpDevice("Cam 1") }),
            new NullCompanionService());

        command.Dispose();

        Assert.Null(Record.Exception(command.Dispose));
    }

    [Fact]
    public void SelectingAndDeselectingACamera_DoesNotThrow()
    {
        var camera = new ViscaIpDevice("Cam 1");
        var gamepad = new TestGamepad(new ObservableCollection<ViscaDeviceBase> { camera });
        _ = new SelectCameraCommand(gamepad, new NullCompanionService());

        Assert.Null(Record.Exception(() => gamepad.SelectedCamera = camera));
        Assert.Null(Record.Exception(() => gamepad.SelectedCamera = null));
    }

    private sealed class NullCompanionService : IBitfocusCompanionService
    {
        public event EventHandler<BitfocusCompanionEvent>? ReceivedBitfocusCommand { add { } remove { } }

        public string? InboundListenerError => null;

        public event EventHandler? InboundListenerErrorChanged { add { } remove { } }
    }

    private sealed class TestGamepad : IGamepad
    {
        private ViscaDeviceBase? _selectedCamera;

        public TestGamepad(ObservableCollection<ViscaDeviceBase> cameras) => Cameras = cameras;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }

        public string Id => "test-gamepad";
        public string Name => "Test gamepad";
        public bool IsActivated { get; set; } = true;
        public bool IsConnected { get; set; } = true;
        public ObservableCollection<ViscaDeviceBase>? Cameras { get; set; }
        public IReadOnlyList<ICommand> Commands => Array.Empty<ICommand>();
        public bool DetectInput { get; set; }
        public IEnumerable<IInput> Inputs => Array.Empty<IInput>();

        public ViscaDeviceBase? SelectedCamera
        {
            get => _selectedCamera;
            set
            {
                _selectedCamera = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCamera)));
            }
        }

        public void Acquire() { }
        public void Unacquire() { }
        public void Update() { }
    }
}
