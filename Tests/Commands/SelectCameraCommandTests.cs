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

// SelectCameraCommand keeps its camera list in a static field (#5). Keep every test that
// constructs it in this class so xUnit runs them sequentially, and give each test its own
// gamepad and camera collection.
public class SelectCameraCommandTests
{
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
