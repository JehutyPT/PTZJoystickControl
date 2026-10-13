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
    // Keys that only configured the app's calls to Companion, removed in #15. Files written
    // before then may still have them: they are ignored with an INFO, not warned about as typos.
    private static readonly string[] RetiredKeys = { "Host", "Port" };

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

            var retired = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (Is(property, nameof(CompanionSettings.ListenerPort)))
                {
                    if (TryGetPort(property.Value, out var port))
                        settings.ListenerPort = port;
                    else
                        WarnInvalid(property, CompanionSettings.DefaultListenerPort);
                }
                else if (RetiredKeys.Any(key => Is(property, key)))
                {
                    retired.Add(property.Name);
                }
                else
                {
                    // Most likely a typo, which would otherwise silently leave a default in place.
                    Trace.TraceWarning($"Companion settings in {_filePath}: ignoring unknown key {property.Name}");
                }
            }

            if (retired.Count > 0)
                Trace.TraceInformation($"Companion settings in {_filePath}: {string.Join(" and ", retired)} {(retired.Count == 1 ? "is" : "are")} no longer used (the app makes no calls to Companion) and ignored");
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
