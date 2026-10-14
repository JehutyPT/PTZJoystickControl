using Avalonia.Threading;
using PtzJoystickControl.Core.Services;
using System.Collections.ObjectModel;

namespace PtzJoystickControl.Gui.ViewModels;

/// <summary>
/// The speed profiles section of the main window. It changes the list only through the speed profiles
/// service and shows every change, including those made on another thread (Companion, #46), on the UI thread.
/// </summary>
public class SpeedProfilesViewModel : ViewModelBase
{
    private readonly ISpeedProfilesService _speedProfiles;

    public SpeedProfilesViewModel(ISpeedProfilesService speedProfiles)
    {
        _speedProfiles = speedProfiles;
        // Raised on the thread that made the change; bindings need the UI thread.
        // Subscribed before the first read, so a change made in between is not missed.
        speedProfiles.ProfilesChanged += (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                ShowProfiles();
            else
                Dispatcher.UIThread.Post(ShowProfiles);
        };
        ShowProfiles();
    }

    /// <summary>
    /// One row per position. Rows are kept and updated in place rather than rebuilt, so a field being
    /// typed in keeps its focus while each keystroke is applied.
    /// </summary>
    public ObservableCollection<SpeedProfileViewModel> Profiles { get; } = new();

    public void Add() => _speedProfiles.Add();

    // Shows the latest list: a change announced later than this one shows the same list again.
    private void ShowProfiles()
    {
        var profiles = _speedProfiles.Profiles;
        while (Profiles.Count > profiles.Count)
            Profiles.RemoveAt(Profiles.Count - 1);

        for (var position = 1; position <= profiles.Count; position++)
        {
            if (position <= Profiles.Count)
            {
                Profiles[position - 1].Show(profiles, position);
                continue;
            }

            var row = new SpeedProfileViewModel(_speedProfiles);
            row.Show(profiles, position);
            Profiles.Add(row);
        }
    }
}
