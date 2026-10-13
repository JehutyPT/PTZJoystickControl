using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PtzJoystickControl.Gui.Views
{
    public partial class InstanceRunningWindow : Window
    {
        public InstanceRunningWindow()
        {
            InitializeComponent();
        }

        // A click handler rather than a binding to Close: Window.Close has overloads, which method
        // bindings do not resolve reliably.
        private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
    }
}
