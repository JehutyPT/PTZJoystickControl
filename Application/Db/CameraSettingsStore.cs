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

            List<JsonElement> entries;
            try
            {
                string serializedViscaDeviceSettings = File.ReadAllText(_cameratFilePath);
                entries = JsonSerializer.Deserialize<List<JsonElement>>(serializedViscaDeviceSettings) ?? new();
            }
            catch (Exception e)
            {
                Trace.TraceError($"Failed to load camera settings from {_cameratFilePath}, starting with none: {e}");
                return new List<ICamera>();
            }

            // One camera that cannot be loaded (e.g. a camera type from a newer build, or a value of the
            // wrong type) must not lose the others, so each entry is read on its own.
            var cameras = new List<ICamera>();
            for (var i = 0; i < entries.Count; i++)
            {
                try
                {
                    cameras.Add(LoadCamera(entries[i].Deserialize<ViscaDeviceSettings>()));
                }
                catch (Exception e)
                {
                    Trace.TraceWarning($"Skipping camera '{Text(entries[i], nameof(ViscaDeviceSettings.Name))}' (entry {i + 1}, type {Text(entries[i], nameof(ViscaDeviceSettings.IViscaDeveiceTypeAssemblyQualifiedName))}) in {_cameratFilePath}: it cannot be loaded: {e.GetBaseException().Message}");
                }
            }
            return cameras;
        }

        // The entry's text value for the key, to name an entry that cannot be loaded; null when it has none.
        private static string? Text(JsonElement entry, string key) =>
            entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private ICamera LoadCamera(ViscaDeviceSettings? viscaDeviceSettings)
        {
            if (viscaDeviceSettings == null)
                throw new InvalidDataException("the entry is empty");

            var type = Type.GetType(viscaDeviceSettings.IViscaDeveiceTypeAssemblyQualifiedName ?? "")
                ?? throw new InvalidDataException("its camera type is not known to this build");
            // Checked before constructing: the file names the type, so nothing but a camera is constructed from it.
            if (!typeof(ICamera).IsAssignableFrom(type) || Activator.CreateInstance(type, viscaDeviceSettings.Name) is not ICamera camera)
                throw new InvalidDataException("its type is not a camera");
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
