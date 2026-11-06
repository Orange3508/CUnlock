using System.IO;
using System.Text.Json;

namespace ProximityLock;

public class AppConfig
{
    public string TargetDeviceAddress { get; set; } = "";
    public string TargetDeviceName { get; set; } = "";
    public int LockThreshold { get; set; } = -75;
    public int WakeThreshold { get; set; } = -65;
    public int LockTriggerCount { get; set; } = 3;
    public int WakeTriggerCount { get; set; } = 2;
    public int ScanIntervalMs { get; set; } = 1500;
    public bool AutoLockEnabled { get; set; } = true;
    public bool AutoWakeEnabled { get; set; } = true;
    public bool AutoStart { get; set; } = false;
    public int LockCooldownSeconds { get; set; } = 30;

    private static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "ProximityLock", "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }
}
