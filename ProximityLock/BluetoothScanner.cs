using System.IO;
using Windows.Devices.Bluetooth.Advertisement;

namespace ProximityLock;

/// <summary>
/// 蓝牙扫描服务：扫描周围 BLE 广播包，获取指定设备的 RSSI
/// </summary>
public class BluetoothScanner
{
    private BluetoothLEAdvertisementWatcher? _watcher;
    private readonly Dictionary<ulong, (string Name, short Rssi, DateTimeOffset LastSeen)> _devices = new();
    private readonly object _lock = new();

    public event EventHandler<DeviceFoundEventArgs>? DeviceFound;
    public event EventHandler<RssiUpdatedEventArgs>? RssiUpdated;

    public ulong? TargetAddress { get; set; }
    public bool IsScanning { get; private set; }

    public IReadOnlyDictionary<ulong, (string Name, short Rssi, DateTimeOffset LastSeen)> Devices
    {
        get { lock (_lock) { return new Dictionary<ulong, (string, short, DateTimeOffset)>(_devices); } }
    }

    public void Start()
    {
        if (IsScanning) return;

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += (s, e) => IsScanning = false;

        try
        {
            _watcher.Start();
            IsScanning = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BLE 扫描启动失败: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_watcher != null && IsScanning)
        {
            try { _watcher.Stop(); } catch { }
            IsScanning = false;
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender,
                                         BluetoothLEAdvertisementReceivedEventArgs args)
    {
        lock (_lock)
        {
            var address = args.BluetoothAddress;
            var name = args.Advertisement.LocalName ?? "";
            var rssi = args.RawSignalStrengthInDBm; // short

            _devices[address] = (name, rssi, args.Timestamp);

            DeviceFound?.Invoke(this, new DeviceFoundEventArgs(address, name, rssi));

            if (TargetAddress.HasValue && address == TargetAddress.Value)
            {
                RssiUpdated?.Invoke(this, new RssiUpdatedEventArgs(rssi));
            }
        }
    }

    public static ulong ParseAddress(string mac)
    {
        var clean = mac.Replace(":", "").Replace("-", "").Replace(" ", "");
        if (clean.Length != 12) throw new ArgumentException("无效的 MAC 地址");
        return Convert.ToUInt64(clean, 16);
    }

    public static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        var hex = BitConverter.ToString(bytes, 2, 6);
        return hex.Replace("-", ":");
    }

    public void Dispose()
    {
        Stop();
    }
}

public class DeviceFoundEventArgs : EventArgs
{
    public ulong Address { get; }
    public string Name { get; }
    public short Rssi { get; }
    public string AddressHex => BluetoothScanner.FormatAddress(Address);

    public DeviceFoundEventArgs(ulong address, string name, short rssi)
    {
        Address = address; Name = name; Rssi = rssi;
    }
}

public class RssiUpdatedEventArgs : EventArgs
{
    public short Rssi { get; }
    public RssiUpdatedEventArgs(short rssi) { Rssi = rssi; }
}
