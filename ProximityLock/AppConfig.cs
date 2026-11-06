using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProximityLock;

public class AppConfig
{
    // 距离估算参数：1 米处参考信号强度 -59 dBm，路径损耗指数 2.0（室内空旷环境经验值）
    private const double RefRssiAt1m = -59;
    private const double PathLossExponent = 2.0;

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

    /// <summary>远离距离（米），内部换算成 RSSI 阈值</summary>
    [JsonIgnore]
    public double LockDistanceMeters
    {
        get => Math.Round(RssiToMeters(LockThreshold), 1);
        set => LockThreshold = (int)Math.Round(MetersToRssi(value));
    }

    /// <summary>靠近距离（米），内部换算成 RSSI 阈值</summary>
    [JsonIgnore]
    public double WakeDistanceMeters
    {
        get => Math.Round(RssiToMeters(WakeThreshold), 1);
        set => WakeThreshold = (int)Math.Round(MetersToRssi(value));
    }

    public static double MetersToRssi(double meters)
    {
        meters = Math.Clamp(meters, 0.2, 30.0);
        return RefRssiAt1m - 10.0 * PathLossExponent * Math.Log10(meters);
    }

    public static double RssiToMeters(double rssi)
    {
        return Math.Pow(10, (RefRssiAt1m - rssi) / (10.0 * PathLossExponent));
    }

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
