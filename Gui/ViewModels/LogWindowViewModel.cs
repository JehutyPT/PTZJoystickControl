using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Threading;
using PtzJoystickControl.Application.Logging;

namespace PtzJoystickControl.Gui.ViewModels
{
    /// <summary>
    /// Shows the recent entries of the app's <see cref="AppLogListener"/>, which owns the
    /// bounded history. New entries only schedule a refresh on the UI thread; at most one
    /// refresh is pending at a time, and it shows everything logged until it runs, so a
    /// burst of entries costs one rebuild, not one per entry.
    /// </summary>
    internal class LogWindowViewModel : ViewModelBase, INotifyPropertyChanged
    {
        private readonly AppLogListener _logListener;
        private int _refreshPending;
        // UI thread only: true while Log is being set, so entries that setting it logs
        // (e.g. Avalonia binding warnings from the Log TextBox) cannot schedule another
        // refresh and loop. They still show on the next refresh.
        private bool _refreshing;
        private string _log = string.Empty;

        internal LogWindowViewModel(AppLogListener logListener)
        {
            _logListener = logListener;
            // Created on the UI thread, so the history can be applied directly.
            var history = logListener.Subscribe(OnEntryLogged);
            SetLog(string.Join('\n', history));
        }

        public string Log
        {
            get => _log;
            private set
            {
                _log = value;
                NotifyPropertyChanged();
            }
        }

        public void ClearLog()
        {
            _logListener.ClearRecent();
            SetLog(string.Empty);
        }

        // Runs on the logging thread inside the listener's lock: only schedule a refresh.
        private void OnEntryLogged(string line)
        {
            if (Dispatcher.UIThread.CheckAccess() && _refreshing)
                return;
            if (Interlocked.CompareExchange(ref _refreshPending, 1, 0) != 0)
                return;
            try
            {
                Dispatcher.UIThread.Post(Refresh);
            }
            catch
            {
                // Not posted: let the next entry try again.
                Interlocked.Exchange(ref _refreshPending, 0);
            }
        }

        private void Refresh()
        {
            try
            {
                // Cleared first, so an entry logged from here on schedules a new refresh.
                Interlocked.Exchange(ref _refreshPending, 0);
                SetLog(string.Join('\n', _logListener.Snapshot()));
            }
            catch
            {
                // Never let the Log window take down the UI thread. Do not log here:
                // that would feed this window and could loop.
            }
        }

        private void SetLog(string text)
        {
            _refreshing = true;
            try
            {
                Log = text;
            }
            finally
            {
                _refreshing = false;
            }
        }

        public new event PropertyChangedEventHandler? PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
