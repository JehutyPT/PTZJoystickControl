using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;
using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Gui.ViewModels;
using System.Collections.Generic;
using System.ComponentModel;

namespace PtzJoystickControl.Gui.Views;

public class CommandPropsTemplateSelector : IDataTemplate
{
    [Content]
    public Dictionary<string, IDataTemplate> Templates { get; } = new();

    public bool SupportsRecycling => false;

    public IControl Build(object param)
    {
        IDataTemplate? returnTemplate = null;

        var inputViewModel = (InputViewModel)param;
        if (inputViewModel.SelectedCommand != null)
        {
            if (inputViewModel.InputType == InputType.Axis)
            {
                if (inputViewModel.SelectedCommand is IDynamicCommand)
                    returnTemplate = Template("AxisWithDynamicCommand");

                else if (inputViewModel.SelectedCommand is IStaticCommand)
                    returnTemplate = Template("AxisWithStaticCommand");
            }

            else if (inputViewModel.InputType == InputType.Button)
            {
                if (inputViewModel.SelectedCommand is IDynamicCommand)
                    returnTemplate = Template("ButtonWithDynamicCommand");

                else if (inputViewModel.SelectedCommand is IStaticCommand)
                    returnTemplate = Template("ButtonWithStaticCommand");
            }

            // Relative inputs only take static commands (Input refuses dynamic ones).
            else if (inputViewModel.InputType == InputType.Relative && inputViewModel.SelectedCommand is IStaticCommand)
                returnTemplate = Template("RelativeWithStaticCommand");
        }
        else
            returnTemplate = Template("NoCommandSelected");

        // Any other input type or command (Unsupported, a future type): a placeholder, never a null control.
        returnTemplate ??= Template("NoSettings");

        var control = returnTemplate?.Build(param) ?? new TextBlock { Text = "No settings" };
        void lambda(object? sender, PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName == nameof(inputViewModel.SelectedCommand))
            {
                inputViewModel.PropertyChanged -= lambda;
                var parent = control.Parent;
                var tmp = control.Parent?.DataContext;
                control.Parent!.DataContext = null;
                parent!.DataContext = tmp;
            }
        }

        inputViewModel.PropertyChanged += lambda;

        return control;
    }

    private IDataTemplate? Template(string key) => Templates.TryGetValue(key, out var template) ? template : null;

    public bool Match(object data)
    {
        return data is InputViewModel;
    }
}
