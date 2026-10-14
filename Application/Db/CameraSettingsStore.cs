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

            List<ViscaDeviceSettings?> viscaDeviceSettingsList;
            try
            {
                string serializedViscaDeviceSettings = File.ReadAllText(_cameratFilePath);
                viscaDeviceSettingsList = JsonSerializer.Deserialize<List<ViscaDeviceSettings?>>(serializedViscaDeviceSettings) ?? new();
            }
            catch (Exception e)
            {
                Trace.TraceError($"Failed to load camera settings from {_cameratFilePath}, starting with none: {e}");
                return new List<ICamera>();
            }

            // One camera that cannot be loaded (e.g. a camera type from a newer build) must not lose the others.
            var cameras = new List<ICamera>();
            for (var i = 0; i < viscaDeviceSettingsList.Count; i++)
            {
                var viscaDeviceSettings = viscaDeviceSettingsList[i];
                try
                {
                    cameras.Add(LoadCamera(viscaDeviceSettings));
                }
                catch (Exception e)
                {
                    Trace.TraceWarning($"Skipping camera '{viscaDeviceSettings?.Name}' (entry {i + 1}, type {viscaDeviceSettings?.IViscaDeveiceTypeAssemblyQualifiedName}) in {_cameratFilePath}: it cannot be loaded: {e.GetBaseException().Message}");
                }
            }
            return cameras;
        }

        private ICamera LoadCamera(ViscaDeviceSettings? viscaDeviceSettings)
        {
            if (viscaDeviceSettings == null)
                throw new InvalidDataException("the entry is empty");

            var type = Type.GetType(viscaDeviceSettings.IViscaDeveiceTypeAssemblyQualifiedName ?? "")
                ?? throw new InvalidDataException("its camera type is not known to this build");
            var camera = (ICamera)Activator.CreateInstance(type, viscaDeviceSettings.Name)!;
            if (camera is ViscaIPDeviceBase viscaIPDevice)
            {
                viscaIPDevice.IPAddress = viscaDeviceSettings.IpAddress;
                viscaIPDevice.Port = viscaDeviceSettings.Port;
                viscaIPDevice.Protocol = viscaDeviceSettings.Protocol;
                viscaIPDevice.SendWaitTime = viscaDeviceSettings.SendWaitTime;
                viscaIPDevice.UseHeader = viscaDeviceSettings.UseHeader;
            }
            else if (camera is XcCameraBase xcCamera)
            {
                // Port first: setting the address starts the camera's requests.
                xcCamera.Port = viscaDeviceSettings.Port;
                xcCamera.Address = viscaDeviceSettings.IpAddress;
            }

            // A hand-edited invalid number must not fail the whole camera load: CamerasService assigns a new one.
            if (viscaDeviceSettings.CompanionNumber >= 1)
                camera.CompanionNumber = viscaDeviceSettings.CompanionNumber;
            else if (viscaDeviceSettings.CompanionNumber != null)
                Trace.TraceWarning($"Camera {camera.Name}: ignoring invalid Companion number {viscaDeviceSettings.CompanionNumber} in {_cameratFilePath}; a new one is assigned");
            return camera;
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
