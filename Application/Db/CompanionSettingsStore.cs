using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PtzJoystickControl.Core.Model;

namespace PtzJoystickControl.Application.Db;

/// <summary>
/// Reads the optional, hand-edited <c>Companion.json</c>. The app never writes it. A missing file
/// or value means the default; an unreadable file or an invalid value falls back to the default
/// with a WARN, so a bad edit can never stop the app starting.
/// </summary>
public class CompanionSettingsStore
{
    // The file is edited by hand, often in Notepad: tolerate comments and a trailing comma.
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _filePath;

    public CompanionSettingsStore()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            _filePath = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
                ".PTZJoystickControl/Companion.json");
        else
            _filePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PTZJoystickControl/Companion.json");
    }

    public CompanionSettingsStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>Loads the settings, falling back to the defaults for anything missing or invalid. Never throws.</summary>
    public CompanionSettings Load()
    {
        var settings = new CompanionSettings();
        try
        {
            if (!File.Exists(_filePath))
                return settings;

            using var document = JsonDocument.Parse(File.ReadAllText(_filePath), ParseOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Trace.TraceWarning($"Companion settings in {_filePath} are not a JSON object; using the defaults");
                return settings;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (Is(property, nameof(CompanionSettings.Host)))
                {
                    if (property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString() is { } host
                        && IsValidHost(host))
                        settings.Host = host;
                    else
                        WarnInvalid(property, CompanionSettings.DefaultHost);
                }
                else if (Is(property, nameof(CompanionSettings.Port)))
                {
                    if (TryGetPort(property.Value, out var port))
                        settings.Port = port;
                    else
                        WarnInvalid(property, CompanionSettings.DefaultPort);
                }
                else if (Is(property, nameof(CompanionSettings.ListenerPort)))
                {
                    if (TryGetPort(property.Value, out var port))
                        settings.ListenerPort = port;
                    else
                        WarnInvalid(property, CompanionSettings.DefaultListenerPort);
                }
                else
                {
                    // Most likely a typo, which would otherwise silently leave a default in place.
                    Trace.TraceWarning($"Companion settings in {_filePath}: ignoring unknown key {property.Name}");
                }
            }
            return settings;
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"Failed to load Companion settings from {_filePath}, using the defaults: {e}");
            return new CompanionSettings();
        }
    }

    private static bool Is(JsonProperty property, string name) =>
        string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase);

    // Also builds the URI the client will use, so a host the URI parser rejects can never
    // reach CompanionSettings.OutboundBaseAddress at startup.
    private static bool IsValidHost(string host)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            return false;
        try
        {
            _ = new CompanionSettings { Host = host }.OutboundBaseAddress;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetPort(JsonElement value, out int port)
    {
        port = 0;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out port)
            && port is >= 1 and <= 65535;
    }

    private void WarnInvalid(JsonProperty property, object fallback) =>
        Trace.TraceWarning($"Companion settings in {_filePath}: invalid {property.Name} {property.Value.GetRawText()}; using the default {fallback}");
}
