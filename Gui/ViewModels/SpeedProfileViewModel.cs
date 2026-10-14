using Avalonia.Data;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;
using System.Diagnostics;

namespace PtzJoystickControl.Gui.ViewModels;

/// <summary>
/// One row of the speed profiles section: the profile at one position. Every edit goes through the speed
/// profiles service; the row shows the result once the service announces it (see <see cref="Show"/>).
/// </summary>
public class SpeedProfileViewModel : ViewModelBase
{
    private readonly ISpeedProfilesService _speedProfiles;
    private SpeedProfile _profile = null!;
    private int _position;
    private string _name = "";
    private int _pan;
    private int _tilt;
    private int _zoom;
    private int _focus;
    private bool _isActive;
    private bool _canMoveUp;
    private bool _canMoveDown;
    private bool _canRemove;

    public SpeedProfileViewModel(ISpeedProfilesService speedProfiles)
    {
        _speedProfiles = speedProfiles;
    }

    public string Name
    {
        get => _name;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Trace.TraceInformation($"Speed profile {Name}: invalid name '{value}'");
                throw new DataValidationException("Needs a name");
            }

            _speedProfiles.Edit(_position, _profile with { Name = value });
        }
    }

    public int Pan
    {
        get => _pan;
        set => _speedProfiles.Edit(_position, _profile with { Pan = Percent("pan", value) });
    }

    public int Tilt
    {
        get => _tilt;
        set => _speedProfiles.Edit(_position, _profile with { Tilt = Percent("tilt", value) });
    }

    public int Zoom
    {
        get => _zoom;
        set => _speedProfiles.Edit(_position, _profile with { Zoom = Percent("zoom", value) });
    }

    public int Focus
    {
        get => _focus;
        set => _speedProfiles.Edit(_position, _profile with { Focus = Percent("focus", value) });
    }

    // Choosing a row makes its profile active. A row is unchosen only by choosing another, so false is ignored.
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (value)
                _speedProfiles.SetActive(_position);
        }
    }

    public bool CanMoveUp
    {
        get => _canMoveUp;
        private set => RaiseAndSetIfChanged(ref _canMoveUp, value);
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        private set => RaiseAndSetIfChanged(ref _canMoveDown, value);
    }

    // The list always keeps at least one speed profile.
    public bool CanRemove
    {
        get => _canRemove;
        private set => RaiseAndSetIfChanged(ref _canRemove, value);
    }

    public void MoveUp() => _speedProfiles.MoveUp(_position);

    public void MoveDown() => _speedProfiles.MoveDown(_position);

    public void Remove() => _speedProfiles.Remove(_position);

    /// <summary>
    /// Shows the profile at 1-based <paramref name="position"/> of <paramref name="profiles"/>, raising only
    /// what changed, so a field being typed in keeps its text and caret.
    /// </summary>
    internal void Show(SpeedProfileList profiles, int position)
    {
        _profile = profiles.Profiles[position - 1];
        _position = position;
        RaiseAndSetIfChanged(ref _name, _profile.Name, nameof(Name));
        RaiseAndSetIfChanged(ref _pan, _profile.Pan, nameof(Pan));
        RaiseAndSetIfChanged(ref _tilt, _profile.Tilt, nameof(Tilt));
        RaiseAndSetIfChanged(ref _zoom, _profile.Zoom, nameof(Zoom));
        RaiseAndSetIfChanged(ref _focus, _profile.Focus, nameof(Focus));
        RaiseAndSetIfChanged(ref _isActive, position == profiles.ActivePosition, nameof(IsActive));
        CanMoveUp = position > 1;
        CanMoveDown = position < profiles.Count;
        CanRemove = profiles.Count > 1;
    }

    private int Percent(string axis, int value)
    {
        if (value >= SpeedProfile.MinPercent && value <= SpeedProfile.MaxPercent)
            return value;

        Trace.TraceInformation($"Speed profile {Name}: invalid {axis} percentage '{value}'");
        throw new DataValidationException($"{SpeedProfile.MinPercent}-{SpeedProfile.MaxPercent}");
    }
}
