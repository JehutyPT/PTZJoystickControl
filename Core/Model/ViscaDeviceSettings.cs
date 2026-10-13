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
    public bool SingleCommand { get; set; }

    public int? OnSelectedBitfocusButtonNumber { get; set; }
    public int? OnSelectedBitfocusPageNumber { get; set; }

    // Absent in files saved before Companion numbers existed; CamerasService assigns one on load.
    public int? CompanionNumber { get; set; }

    public ViscaDeviceSettings()
    {
    }

    public ViscaDeviceSettings(ViscaDeviceBase viscaDevice)
    {
        OnSelectedBitfocusPageNumber = viscaDevice.OnSelectedBitfocusPageNumber;
        OnSelectedBitfocusButtonNumber = viscaDevice.OnSelectedBitfocusButtonNumber;
        CompanionNumber = viscaDevice.CompanionNumber;
        IViscaDeveiceTypeAssemblyQualifiedName = viscaDevice.GetType().AssemblyQualifiedName!;
        Name = viscaDevice.Name;
        if(viscaDevice is ViscaIPDeviceBase viscaIPDevice)
        {
            IpAddress = viscaIPDevice.IPAddress;
            Port = viscaIPDevice.Port;
            Protocol = viscaIPDevice.Protocol;
            SendWaitTime = viscaIPDevice.SendWaitTime;
            UseHeader = viscaIPDevice.UseHeader;
            SingleCommand = viscaIPDevice.SingleCommand;
        }
    }
}