using System.Text.Json.Serialization;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// Where to reach the panel's Raw Panel server. Stored in <c>SkaarhojPanel.json</c> next to the
/// other settings files and edited by hand for now; an empty address means no panel.
/// </summary>
public class SkaarhojPanelSettings
{
    public const int DefaultPort = 9923;

    public string? Address { get; set; }

    public int Port { get; set; } = DefaultPort;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Address);
}
