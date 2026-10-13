using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using PtzJoystickControl.Application.Logging;

namespace PtzJoystickControl.Gui.ViewModels
{
    /// <summary>
    /// Shows the entries of the app's <see cref="AppLogListener"/>: the history since
    /// startup, then every new entry. Entries arrive on whichever thread logged them and
    /// are posted to the UI thread; the shown text is bounded to the newest
    /// <see cref="AppLogListener.DefaultCapacity"/> lines.
    /// </summary>
    internal class LogWindowViewModel : ViewModelBase, INotifyPropertyChanged
    {
        private readonly Queue<string> _lines = new();
        private string _log = string.Empty;

        internal LogWindowViewModel(AppLogListener logListener)
        {
            // Created on the UI thread, so the history can be applied directly.
            foreach (var line in logListener.Subscribe(OnEntryLogged))
                Enqueue(line);
            Log = string.Join('\n', _lines);
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
            _lines.Clear();
            Log = string.Empty;
        }

        // Runs on the logging thread inside the listener's lock: only hand the line off.
        private void OnEntryLogged(string line) => Dispatcher.UIThread.Post(() =>
        {
            Enqueue(line);
            Log = string.Join('\n', _lines);
        });

        private void Enqueue(string line)
        {
            if (_lines.Count >= AppLogListener.DefaultCapacity)
                _lines.Dequeue();
            _lines.Enqueue(line);
        }

        public new event PropertyChangedEventHandler? PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
