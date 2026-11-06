using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace ProximityLock;

/// <summary>扫描到的 BLE 设备</summary>
public sealed class BleDevice
{
    public ulong Address { get; }
    public string Name { get; set; } = "";
    public short Rssi { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string AddressHex => BluetoothScanner.FormatAddress(Address);

    public BleDevice(ulong address) { Address = address; }

    /// <summary>界面显示名：有名字用名字，否则用厂商/地址兜底</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? $"未知设备 ({AddressHex})" : Name;
}

/// <summary>
/// 蓝牙扫描服务：扫描周围 BLE 广播包，获取指定设备的 RSSI 与名称
/// </summary>
public class BluetoothScanner
{
    private BluetoothLEAdvertisementWatcher? _watcher;
    private readonly Dictionary<ulong, BleDevice> _devices = new();
    private readonly HashSet<ulong> _nameLookupStarted = new();
    private readonly object _lock = new();

    public event EventHandler<DeviceFoundEventArgs>? DeviceFound;
    public event EventHandler<RssiUpdatedEventArgs>? RssiUpdated;

    public ulong? TargetAddress { get; set; }
    public bool IsScanning { get; private set; }

    public IReadOnlyDictionary<ulong, BleDevice> Devices
    {
        get { lock (_lock) { return new Dictionary<ulong, BleDevice>(_devices); } }
    }

    public void Start()
    {
        if (IsScanning) return;

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            // Active 模式会主动请求扫描响应包，能拿到更多带名称的设备
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

        // 系统里已配对/已登记的设备有名称，后台同步一次
        SyncSystemDeviceNames();
    }

    /// <summary>
    /// 从 Windows 设备列表中读取已知设备名称（配对过的手机/手表通常能拿到）
    /// </summary>
    private async void SyncSystemDeviceNames()
    {
        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelector();
            var list = await DeviceInformation.FindAllAsync(selector);

            var map = new Dictionary<ulong, string>();
            foreach (var info in list)
            {
                var raw = info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var v)
                    ? v?.ToString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(raw)) continue;

                ulong addr;
                try { addr = ParseAddress(raw); }
                catch { continue; }

                var name = info.Name;
                if (!string.IsNullOrWhiteSpace(name)) map[addr] = name;
            }

            if (map.Count == 0) return;

            lock (_lock)
            {
                foreach (var kv in map)
                {
                    var dev = _devices.TryGetValue(kv.Key, out var d) ? d : null;
                    if (dev != null && string.IsNullOrWhiteSpace(dev.Name))
                    {
                        dev.Name = kv.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取系统设备名失败: {ex.Message}");
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
        var address = args.BluetoothAddress;
        var advName = args.Advertisement.LocalName ?? "";
        var rssi = args.RawSignalStrengthInDBm;

        string nameForEvent;
        lock (_lock)
        {
            var dev = _devices.TryGetValue(address, out var existing)
                ? existing
                : new BleDevice(address);

            // 关键修复：只在拿到非空名称时更新，避免被后续空包覆盖
            if (!string.IsNullOrWhiteSpace(advName))
            {
                dev.Name = advName;
            }

            dev.Rssi = rssi;
            dev.LastSeen = args.Timestamp;
            _devices[address] = dev;

            nameForEvent = dev.Name;
        }

        DeviceFound?.Invoke(this, new DeviceFoundEventArgs(address, nameForEvent, rssi));

        if (TargetAddress.HasValue && address == TargetAddress.Value)
        {
            RssiUpdated?.Invoke(this, new RssiUpdatedEventArgs(rssi));
        }

        // 仍然没名字的设备，尝试通过 GATT 读取一次设备名
        if (string.IsNullOrWhiteSpace(nameForEvent))
        {
            TryResolveNameViaGatt(address);
        }
    }

    /// <summary>
    /// 兜底方案：连接后读 GAP 服务里的设备名称特征值 (0x2A00)
    /// Apple 设备会拒绝此连接，属正常情况
    /// </summary>
    private async void TryResolveNameViaGatt(ulong address)
    {
        bool shouldStart;
        lock (_lock)
        {
            shouldStart = _nameLookupStarted.Add(address);
        }
        if (!shouldStart) return;

        try
        {
            var devTask = BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask();
            var timeout = Task.Delay(TimeSpan.FromSeconds(6));

            var completed = await Task.WhenAny(devTask, timeout);
            if (completed != devTask) return;

            var device = await devTask;
            if (device == null) return;

            var name = device.Name;
            if (string.IsNullOrWhiteSpace(name) && device.DeviceInformation != null)
            {
                name = device.DeviceInformation.Name;
            }
            device.Dispose();

            if (string.IsNullOrWhiteSpace(name)) return;

            lock (_lock)
            {
                if (_devices.TryGetValue(address, out var d) && string.IsNullOrWhiteSpace(d.Name))
                {
                    d.Name = name;
                }
            }
        }
        catch
        {
            // 忽略：多数设备不允许未配对连接
        }
    }

    public static ulong ParseAddress(string mac)
    {
        // 兼容 "AA:BB:CC:DD:EE:FF" 与 "Bluetooth#BluetoothAA:BB:..." 等形式，取最后 12 位十六进制
        var clean = mac.Replace(":", "").Replace("-", "").Replace(" ", "");
        if (clean.Length > 12)
        {
            clean = clean.Substring(clean.Length - 12);
        }
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
