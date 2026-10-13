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
        var commandA = new SelectCameraCommand(gamepadA, new RecordingCompanionService());
        var commandB = new SelectCameraCommand(gamepadB, new RecordingCompanionService());

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
        var removedCommand = new SelectCameraCommand(removedGamepad, new RecordingCompanionService());
        _ = new SelectCameraCommand(liveGamepad, new RecordingCompanionService());
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
        var command = new SelectCameraCommand(new TestGamepad(cameras), new RecordingCompanionService());
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
    public void AfterDispose_SelectingACamera_DoesNotCallCompanion()
    {
        var camera = new ViscaIpDevice("Cam 1") { OnSelectedBitfocusPageNumber = 2, OnSelectedBitfocusButtonNumber = 5 };
        var gamepad = new TestGamepad(new ObservableCollection<ViscaDeviceBase> { camera });
        var companion = new RecordingCompanionService();
        var command = new SelectCameraCommand(gamepad, companion);

        command.Dispose();
        gamepad.SelectedCamera = camera;

        Assert.Empty(companion.Presses);
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        var command = new SelectCameraCommand(
            new TestGamepad(new ObservableCollection<ViscaDeviceBase> { new ViscaIpDevice("Cam 1") }),
            new RecordingCompanionService());

        command.Dispose();

        Assert.Null(Record.Exception(command.Dispose));
    }

    [Fact]
    public void SelectingACamera_PressesItsCompanionButton()
    {
        var camera = new ViscaIpDevice("Cam 1") { OnSelectedBitfocusPageNumber = 2, OnSelectedBitfocusButtonNumber = 5 };
        var gamepad = new TestGamepad(new ObservableCollection<ViscaDeviceBase> { camera });
        var companion = new RecordingCompanionService();
        _ = new SelectCameraCommand(gamepad, companion);

        gamepad.SelectedCamera = camera;

        Assert.Equal(new[] { ((int?)2, (int?)5) }, companion.Presses);
    }

    [Fact]
    public void DeselectingTheCamera_DoesNotThrowOrCallCompanion()
    {
        var camera = new ViscaIpDevice("Cam 1") { OnSelectedBitfocusPageNumber = 2, OnSelectedBitfocusButtonNumber = 5 };
        var gamepad = new TestGamepad(new ObservableCollection<ViscaDeviceBase> { camera });
        var companion = new RecordingCompanionService();
        _ = new SelectCameraCommand(gamepad, companion);
        gamepad.SelectedCamera = camera;

        var exception = Record.Exception(() => gamepad.SelectedCamera = null);

        Assert.Null(exception);
        Assert.Equal(new[] { ((int?)2, (int?)5) }, companion.Presses);
    }

    private sealed class RecordingCompanionService : IBitfocusCompanionService
    {
        public List<(int? Page, int? Button)> Presses { get; } = new();

        public event EventHandler<BitfocusCompanionEvent>? ReceivedBitfocusCommand { add { } remove { } }

        public void PressButton(int? pageNumber, int? buttonNumber) => Presses.Add((pageNumber, buttonNumber));

        public void SetCustomVariable(string variableName, string value) { }

        public List<BitfocusCompanionEvent> SupportedEvents => new();

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
