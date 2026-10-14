using PtzJoystickControl.Core.Devices;

namespace PtzJoystickControl.Core.Model;

public class ViscaDeviceSettings
{
    public string IViscaDeveiceTypeAssemblyQualifiedName { get; set; } = null!;
    public string IpAddress { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int Port { get; set; }
    public Protocol Protocol { get; set; }
    public int SendWaitTime { get; set; }
    public bool UseHeader { get; set; }

    // Files saved before #15 also hold OnSelectedBitfocusPageNumber / OnSelectedBitfocusButtonNumber
    // (the Companion button pressed on selection). Deserialization skips them, so those files still load.
    // Files saved before #50 also hold SingleCommand (an unused option to pack several VISCA commands
    // into one packet; every packet carries one command). Skipped the same way, and not written back.

    // Absent in files saved before Companion numbers existed; CamerasService assigns one on load.
    public int? CompanionNumber { get; set; }

    public ViscaDeviceSettings()
    {
    }

    public ViscaDeviceSettings(ICamera camera)
    {
        CompanionNumber = camera.CompanionNumber;
        IViscaDeveiceTypeAssemblyQualifiedName = camera.GetType().AssemblyQualifiedName!;
        Name = camera.Name;
        if(camera is ViscaIPDeviceBase viscaIPDevice)
        {
            IpAddress = viscaIPDevice.IPAddress;
            Port = viscaIPDevice.Port;
            Protocol = viscaIPDevice.Protocol;
            SendWaitTime = viscaIPDevice.SendWaitTime;
            UseHeader = viscaIPDevice.UseHeader;
        }
        else if (camera is XcCameraBase xcCamera)
        {
            // The same keys as a VISCA camera's connection; the VISCA-only values keep their defaults.
            IpAddress = xcCamera.Address;
            Port = xcCamera.Port;
        }
    }
}