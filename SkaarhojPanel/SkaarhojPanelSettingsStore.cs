using System.Diagnostics;
using System.Text.Json;
using PtzJoystickControl.Application.Db;

namespace PtzJoystickControl.SkaarhojPanel;

/// <summary>
/// Loads and saves <see cref="SkaarhojPanelSettings"/>. Never throws: a missing file gets an
/// editable template, a broken one is logged and treated as "no panel" and left as it is.
/// </summary>
public class SkaarhojPanelSettingsStore
{
    public const string FileName = "SkaarhojPanel.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    public SkaarhojPanelSettingsStore(string filePath)
    {
        _filePath = filePath;
    }

    public SkaarhojPanelSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                var defaults = new SkaarhojPanelSettings { Address = "" };
                // A template to fill in by hand. Quiet: an app without a panel logs nothing new.
                Save(defaults);
                return defaults;
            }

            var settings = JsonSerializer.Deserialize<SkaarhojPanelSettings>(File.ReadAllText(_filePath));
            if (settings != null)
                return settings;

            Trace.TraceError($"SKAARHOJ panel settings in {_filePath} are empty (null); the panel stays off until the file is fixed.");
        }
        catch (Exception e)
        {
            Trace.TraceError($"Failed to load SKAARHOJ panel settings from {_filePath}; the panel stays off until the file is fixed: {e}");
        }
        return new SkaarhojPanelSettings();
    }

    public bool Save(SkaarhojPanelSettings settings)
    {
        try
        {
            AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(settings, WriteOptions));
            return true;
        }
        catch (Exception e)
        {
            Trace.TraceError($"Failed to save SKAARHOJ panel settings to {_filePath}: {e}");
            return false;
        }
    }
}
