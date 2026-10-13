using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// The SKAARHOJ panel as a controller source: one entry in the controller list, backed by a
/// <see cref="SkaarhojPanelLink"/>. The panel is available while the link is up and the panel is
/// awake; while it is available and activated, one <see cref="SkaarhojGamepad"/> is active.
/// <para>
/// Read-only: nothing here writes to the panel. The link's events only enqueue onto the shared
/// <see cref="InputQueue"/>; the input loop applies them, so panel input never runs on the link's
/// thread (live-safety §4). Losing the link or the panel falling asleep stops every camera the
/// gamepad drove and retires it; the next one starts with every axis at 0.
/// </para>
/// <para>
/// Mappings are kept in their own settings file (<see cref="IGamepadSettingsStore"/>), not
/// <c>Gamepads.json</c>, whose entries all become USB joysticks.
/// </para>
/// </summary>
public sealed class SkaarhojGamepadsService : IGamepadsService, IDisposable
{
    /// <summary>The panel's mappings, next to <c>Gamepads.json</c>.</summary>
    public const string SettingsFileName = "SkaarhojGamepads.json";

    private readonly IGamepadSettingsStore _settings;
    private readonly ICamerasService _camerasService;
    private readonly ICommandsService _commandsService;
    private readonly IBitfocusCompanionService _companion;
    private readonly InputQueue _inputQueue;
    private readonly SkaarhojGamepadInfo _info;
    private SkaarhojPanelLink? _link;

    // The controllers lock, shared with every other source (InputQueue.ControllersLock). Guards the
    // state below and every change to the collections: link events arrive through the input loop,
    // activation from the UI thread.
    private readonly object _sync;
    private bool _linkUp;
    private bool _asleep;
    private SkaarhojGamepad? _gamepad;

    public SkaarhojGamepadsService(
        SkaarhojPanelLink link,
        IGamepadSettingsStore settings,
        ICamerasService camerasService,
        ICommandsService commandsService,
        IBitfocusCompanionService companion,
        InputQueue inputQueue)
        : this(link.Host, settings, camerasService, commandsService, companion, inputQueue)
    {
        // Subscribe before the link starts, or its first LinkUp is missed.
        _link = link;
        link.LinkUp += OnLinkUp;
        link.LinkDown += OnLinkDown;
        link.MessageReceived += OnMessage;
    }

    internal SkaarhojGamepadsService(
        string address,
        IGamepadSettingsStore settings,
        ICamerasService camerasService,
        ICommandsService commandsService,
        IBitfocusCompanionService companion,
        InputQueue inputQueue)
    {
        _settings = settings;
        _camerasService = camerasService;
        _commandsService = commandsService;
        _companion = companion;
        _inputQueue = inputQueue;
        _sync = inputQueue.ControllersLock;

        var id = SkaarhojGamepad.IdFor(address);
        _info = new SkaarhojGamepadInfo(id, $"SKAARHOJ panel ({address})")
        {
            IsActivated = _settings.GetGamepadSettingsById(id)?.IsActivated ?? false,
        };
        Gamepads.Add(_info);

        _companion.ReceivedBitfocusCommand += OnCompanionCommand;
    }

    public ObservableCollection<IGamepadInfo> Gamepads { get; } = new();
    public ObservableCollection<IGamepad> ActiveGamepads { get; } = new();

    private bool IsAvailable => _linkUp && !_asleep;

    public void ActivateGamepad(IGamepadInfo gamepadInfo)
    {
        if (!ReferenceEquals(gamepadInfo, _info))
            return;

        lock (_sync)
        {
            if (_gamepad != null)
                return;
            if (IsAvailable)
                Connect();
            else
                SaveActivated(true);
            _info.IsActivated = true;
        }
    }

    public void DeactivateGamepad(IGamepadInfo gamepadInfo)
    {
        if (!ReferenceEquals(gamepadInfo, _info))
            return;

        lock (_sync)
        {
            // Stop first, save after.
            if (_gamepad != null)
                Disconnect(lost: false).IsActivated = false;
            SaveActivated(false);
            _info.IsActivated = false;
        }
    }

    public void Dispose()
    {
        _companion.ReceivedBitfocusCommand -= OnCompanionCommand;
        if (_link != null)
        {
            _link.LinkUp -= OnLinkUp;
            _link.LinkDown -= OnLinkDown;
            _link.MessageReceived -= OnMessage;
            _link = null;
        }
    }

    // ---- Link thread: enqueue only. ----

    internal void OnLinkUp() => _inputQueue.Enqueue(() => Update(linkUp: true, asleep: false));

    internal void OnLinkDown(string reason) => _inputQueue.Enqueue(() => Update(linkUp: false, asleep: false));

    internal void OnMessage(RawPanelMessage message)
    {
        switch (message)
        {
            case RawPanelMessage.Speed speed:
                _inputQueue.Enqueue(() => ApplySpeed(speed.Hwc, speed.Value));
                break;
            case RawPanelMessage.Reply { Key: "isSleeping", Value: "1" }:
                _inputQueue.Enqueue(() => UpdateSleep(asleep: true));
                break;
            case RawPanelMessage.Reply { Key: "isSleeping", Value: "0" }:
                _inputQueue.Enqueue(() => UpdateSleep(asleep: false));
                break;
            // Buttons, encoders and everything else belong to the Companion surface plugin.
        }
    }

    private void OnLinkUp(object? sender, EventArgs e) => OnLinkUp();

    private void OnLinkDown(object? sender, string reason) => OnLinkDown(reason);

    private void OnMessage(object? sender, RawPanelMessage message) => OnMessage(message);

    // Companion's HTTP listener thread: enqueue only, like SdlGamepadsService does for USB joysticks.
    private void OnCompanionCommand(object? sender, BitfocusCompanionEvent e)
    {
        var camera = e.Camera;
        _inputQueue.Enqueue(() =>
        {
            lock (_sync)
                CompanionCameraSelection.SelectOnEveryController(ActiveGamepads.ToArray(), camera);
        });
    }

    // ---- Input loop. ----

    private void ApplySpeed(int hwc, int speed)
    {
        lock (_sync)
            _gamepad?.OnSpeed(hwc, speed);
    }

    private void UpdateSleep(bool asleep)
    {
        lock (_sync)
            Update(_linkUp, asleep);
    }

    private void Update(bool linkUp, bool asleep)
    {
        lock (_sync)
        {
            if (_linkUp && linkUp && _asleep != asleep)
                Trace.TraceInformation(asleep
                    ? $"SKAARHOJ panel {_info.Name}: asleep; its joystick is off and the cameras it drove are stopped"
                    : $"SKAARHOJ panel {_info.Name}: awake");

            _linkUp = linkUp;
            // The panel does not report its sleep state on connect; asleep, its joystick sends nothing.
            _asleep = linkUp && asleep;

            var available = IsAvailable;
            if (!available && _gamepad != null)
                Disconnect(lost: true);
            if (_info.IsConnected != available)
                _info.IsConnected = available;
            if (available && _gamepad == null && _info.IsActivated)
            {
                // Saved as active: only if no other controller is (one at a time).
                if (_inputQueue.MayActivate(_info))
                {
                    Connect();
                    // Raised after the gamepad is in ActiveGamepads, so the controller page picks it up.
                    _info.IsActivated = true;
                }
                else
                {
                    SaveActivated(false);
                    _info.IsActivated = false;
                }
            }
        }
    }

    // ---- Under _sync. ----

    private void Connect()
    {
        var gamepad = new SkaarhojGamepad(_info.Id, _info.Name, _commandsService, _camerasService.Cameras);
        GamepadInputSetup.Configure(gamepad, _settings.GetGamepadSettingsById(_info.Id));
        gamepad.PersistentPropertyChanged += OnGamepadPersistentPropertyChanged;
        gamepad.Acquire();
        gamepad.IsActivated = true;
        _gamepad = gamepad;
        ActiveGamepads.Add(gamepad);
    }

    // Stops first: nothing after it may keep a camera moving (live-safety §1). Returns the retired gamepad.
    private SkaarhojGamepad Disconnect(bool lost)
    {
        var gamepad = _gamepad!;
        _gamepad = null;
        try
        {
            gamepad.DrivenCameras.StopAll();
        }
        finally
        {
            ActiveGamepads.Remove(gamepad);
            gamepad.Unacquire();
            gamepad.PersistentPropertyChanged -= OnGamepadPersistentPropertyChanged;
            if (lost)
                gamepad.IsConnected = false;
        }
        return gamepad;
    }

    private void SaveActivated(bool activated)
    {
        var existing = _settings.GetGamepadSettingsById(_info.Id);
        if ((existing?.IsActivated ?? false) == activated)
            return;
        var settings = existing ?? new GamepadSettings { Id = _info.Id, Name = _info.Name };
        settings.IsActivated = activated;
        _settings.SaveGamepadSettings(settings);
    }

    private void OnGamepadPersistentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is IGamepad gamepad)
            _settings.SaveGamepadSettings(new GamepadSettings(gamepad));
    }
}
