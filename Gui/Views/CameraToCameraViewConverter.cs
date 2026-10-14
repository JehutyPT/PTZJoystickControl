using Avalonia.Data.Converters;
using PtzJoystickControl.Gui.ViewModels;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Services;
using Splat;
using System;
using System.Linq;
using System.Globalization;

namespace PtzJoystickControl.Gui.Views;

public class CameraToCameraViewConverter : IValueConverter
{

    private static readonly CameraToCameraViewConverter Instance = new();

    public CameraToCameraViewConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ViscaIPDeviceBase camera)
            return new IPCameraViewModel(camera,
                Locator.Current.GetService<ICamerasService>()?.Cameras ?? Enumerable.Empty<ICamera>());

        if (value is XcCameraBase xcCamera)
            return new XcCameraViewModel(xcCamera,
                Locator.Current.GetService<ICamerasService>()?.Cameras ?? Enumerable.Empty<ICamera>());

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
