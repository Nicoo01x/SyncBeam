using System.IO;
using System.Text.Json;

namespace SyncBeam.App;

/// <summary>
/// Application settings with JSON persistence.
/// Stored in ~/SyncBeam/settings.json
/// </summary>
public class AppSettings
{
    /// <summary>
    /// The port SyncBeam listens on for P2P connections.
    /// Default: 42420. Range: 1024-65535.
    /// </summary>
    public int ListenPort { get; set; } = 42420;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "SyncBeam", "settings.json");

    /// <summary>
    /// Loads settings from disk, or returns defaults if file doesn't exist.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    // Validate port range
                    if (settings.ListenPort < 1024 || settings.ListenPort > 65535)
                    {
                        settings.ListenPort = 42420;
                    }
                    return settings;
                }
            }
        }
        catch
        {
            // If anything fails, return defaults
        }

        return new AppSettings();
    }

    /// <summary>
    /// Saves settings to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Silently fail if we can't save
        }
    }
}
