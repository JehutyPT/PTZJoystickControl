using System.Text.Json;
using PtzJoystickControl.Core.Devices;
using System.Diagnostics;
using PtzJoystickControl.Core.Db;
using PtzJoystickControl.Core.Model;
using System.Runtime.InteropServices;

namespace PtzJoystickControl.Application.Db
{
    public class CameraSettingsStore : ICameraSettingsStore
    {
        private readonly string _cameratFilePath;

        public CameraSettingsStore()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                _cameratFilePath = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                    ".PTZJoystickControl/Cameras.json");
            else
                _cameratFilePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PTZJoystickControl/Cameras.json");

        }

        public CameraSettingsStore(string filePath)
        {
            _cameratFilePath = filePath;
        }

        public IEnumerable<ICamera> GetAllCameras()
        {
            if (!File.Exists(_cameratFilePath))
                return new List<ICamera>();

            try
            {
                string serializedViscaDeviceSettings = File.ReadAllText(_cameratFilePath);
                var viscaDeviceSettingsList = JsonSerializer.Deserialize<IEnumerable<ViscaDeviceSettings>>(serializedViscaDeviceSettings) ?? Enumerable.Empty<ViscaDeviceSettings>();
                return viscaDeviceSettingsList
                    .Select(viscaDeviceSettings =>
                    {
                        var camera = (ICamera?)Activator.CreateInstance(
                            Type.GetType(viscaDeviceSettings.IViscaDeveiceTypeAssemblyQualifiedName)!,
                            viscaDeviceSettings.Name);
                        if (camera is ViscaIPDeviceBase viscaIPDevice)
                        {
                            viscaIPDevice.IPAddress = viscaDeviceSettings.IpAddress;
                            viscaIPDevice.Port = viscaDeviceSettings.Port;
                            viscaIPDevice.Protocol = viscaDeviceSettings.Protocol;
                            viscaIPDevice.SendWaitTime = viscaDeviceSettings.SendWaitTime;
                            viscaIPDevice.UseHeader = viscaDeviceSettings.UseHeader;
                        }
                        if (camera != null)
                        {
                            // A hand-edited invalid number must not fail the whole camera load: CamerasService assigns a new one.
                            if (viscaDeviceSettings.CompanionNumber >= 1)
                                camera.CompanionNumber = viscaDeviceSettings.CompanionNumber;
                            else if (viscaDeviceSettings.CompanionNumber != null)
                                Trace.TraceWarning($"Camera {camera.Name}: ignoring invalid Companion number {viscaDeviceSettings.CompanionNumber} in {_cameratFilePath}; a new one is assigned");
                        }
                        return camera;
                    })
                    .Where(camera => camera != null)
                    .ToList()!;
            }
            catch (Exception e)
            {
                Trace.TraceError($"Failed to load camera settings from {_cameratFilePath}, starting with none: {e}");
                return new List<ICamera>();
            }
        }

        public bool SaveCameras(IEnumerable<ICamera> cameras)
        {
            try
            {
                var viscaDeviceSettings = cameras.Select(camera => new ViscaDeviceSettings(camera));
                string serializedViscaDeviceSettings = JsonSerializer.Serialize(viscaDeviceSettings);
                AtomicFile.WriteAllText(_cameratFilePath, serializedViscaDeviceSettings);
                return true;
            }
            catch (Exception e)
            {
                Trace.TraceError($"Failed to save camera settings to {_cameratFilePath}: {e}");
                return false;
            }
        }
    }
}
