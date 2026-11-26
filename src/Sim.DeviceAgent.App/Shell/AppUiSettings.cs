using System.Text.Json;
using Sim.DeviceAgent.Configuration;

namespace Sim.DeviceAgent.App.Shell;

internal sealed class AppUiSettings
{
    public bool StartWithWindows { get; set; } = true;
}

internal static class AppUiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string ResolvePath(string configPath)
    {
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        return Path.Combine(
            string.IsNullOrWhiteSpace(configDirectory) ? AgentConfigLoader.ProgramDirectory : configDirectory,
            "ui-settings.json");
    }

    public static AppUiSettings Load(string settingsPath)
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                return JsonSerializer.Deserialize<AppUiSettings>(File.ReadAllText(settingsPath), Options)
                       ?? new AppUiSettings();
            }
        }
        catch
        {
            // A damaged settings file must not block agent startup.
        }

        return new AppUiSettings();
    }

    public static void Save(string settingsPath, AppUiSettings settings)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = settingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, Options));
        File.Move(tempPath, settingsPath, overwrite: true);
    }
}
