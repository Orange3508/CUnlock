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
    public bool IsPaired { get; set; }
    public ushort CompanyId { get; set; }
    public bool IsRandomAddress { get; set; }

    public string AddressHex => BluetoothScanner.FormatAddress(Address);

    /// <summary>Apple 设备的厂商 ID</summary>
    public const ushort AppleCompanyId = 0x004C;

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            if (CompanyId == AppleCompanyId) return $"Apple 设备 ({AddressHex})";
            return $"未知设备 ({AddressHex})";
        }
    }

    public BleDevice(ulong address, bool paired = false)
    {
        Address = address;
        IsPaired = paired;
    }
}

/// <summary>
/// 蓝牙扫描服务：BLE 广播扫描 + Windows 已配对设备枚举
/// </summary>
public class BluetoothScanner
{
    private const string AepAddressKey = "System.Devices.Aep.DeviceAddress";

    private BluetoothLEAdvertisementWatcher? _watcher;
    private readonly Dictionary<ulong, BleDevice> _devices = new();
    private readonly HashSet<ulong> _nameLookupStarted = new();
    private readonly List<BleDevice> _pairedDevices = new();
    private readonly object _lock = new();

    private DateTime _lastPairedPoll = DateTime.MinValue;
    private ulong _pairedPollAddress;
    private bool _pairedBusy;

    public event EventHandler<DeviceFoundEventArgs>? DeviceFound;
    public event EventHandler<RssiUpdatedEventArgs>? RssiUpdated;

    public ulong? TargetAddress { get; set; }
    public bool IsScanning { get; private set; }

    /// <summary>已配对设备（系统枚举结果，含名称）</summary>
    public IReadOnlyList<BleDevice> PairedDevices
    {
        get { lock (_lock) { return _pairedDevices.ToList(); } }
    }

    /// <summary>已配对设备的连接状态：true=已连接（在附近），false=断开（已远离），null=未知</summary>
    public bool? PairedConnected { get; private set; }

    public IReadOnlyDictionary<ulong, BleDevice> Devices
    {
        get { lock (_lock) { return new Dictionary<ulong, BleDevice>(_devices); } }
    }

    public void Start()
    {
        if (!IsScanning)
        {
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
        }

        RefreshPairedDevices();
    }

    /// <summary>枚举 Windows 中已配对/已连接的蓝牙设备（能拿到真实名称）</summary>
    public async void RefreshPairedDevices()
    {
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var list = await DeviceInformation.FindAllAsync(selector);

            var result = new List<BleDevice>();
            foreach (var info in list)
            {
                // 经典蓝牙配对设备的 Aep.DeviceAddress 通常为空，MAC 藏在 Id 里
                var raw = info.Properties.TryGetValue(AepAddressKey, out var v)
                    ? v?.ToString() ?? ""
                    : "";

                if (string.IsNullOrWhiteSpace(raw)) raw = info.Id ?? "";
                if (string.IsNullOrWhiteSpace(raw)) continue;

                ulong addr;
                try { addr = ParseAddress(raw); }
                catch { continue; }

                var dev = new BleDevice(addr, paired: true)
                {
                    Name = info.Name ?? "",
                    LastSeen = DateTimeOffset.Now
                };

                if (!string.IsNullOrWhiteSpace(dev.Name)) result.Add(dev);
            }

            lock (_lock)
            {
                _pairedDevices.Clear();
                _pairedDevices.AddRange(result);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"枚举已配对设备失败: {ex.Message}");
        }
    }

    /// <summary>查询已配对设备的经典蓝牙连接状态（手机离开范围会断开）</summary>
    public async void RequestPairedStatus(ulong address)
    {
        lock (_lock)
        {
            if (_pairedBusy) return;
            if (address == _pairedPollAddress &&
                (DateTime.Now - _lastPairedPoll).TotalSeconds < 2) return;
            _pairedBusy = true;
            _pairedPollAddress = address;
            _lastPairedPoll = DateTime.Now;
        }

        try
        {
            var task = BluetoothDevice.FromBluetoothAddressAsync(address).AsTask();
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(4)));
            if (done != task) return;

            var device = await task;
            if (device == null)
            {
                PairedConnected = null;
                return;
            }

            PairedConnected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
            device.Dispose();
        }
        catch
        {
            PairedConnected = null;
        }
        finally
        {
            lock (_lock) { _pairedBusy = false; }
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

    /// <summary>取在指定时间内仍有效的设备记录（过期数据不参与判断）</summary>
    public BleDevice? GetFreshDevice(ulong address, TimeSpan maxAge)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(address, out var dev)) return null;
            return (DateTimeOffset.Now - dev.LastSeen) <= maxAge ? dev : null;
        }
    }

    /// <summary>该地址是否曾经在 BLE 中出现过</summary>
    public bool WasSeenBefore(ulong address)
    {
        lock (_lock) { return _devices.ContainsKey(address); }
    }

    /// <summary>在指定时间内有效的设备中，找同厂商里信号最强的一个（用于跟随随机地址轮换）</summary>
    public BleDevice? FindStrongestByCompany(ushort companyId, TimeSpan maxAge)
    {
        lock (_lock)
        {
            return _devices.Values
                .Where(d => d.CompanyId == companyId)
                .Where(d => (DateTimeOffset.Now - d.LastSeen) <= maxAge)
                .OrderByDescending(d => d.Rssi)
                .FirstOrDefault();
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender,
                                         BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var address = args.BluetoothAddress;
        var advName = args.Advertisement.LocalName ?? "";
        var rssi = args.RawSignalStrengthInDBm;

        ushort companyId = 0;
        foreach (var md in args.Advertisement.ManufacturerData)
        {
            companyId = (ushort)md.CompanyId;
            break;
        }

        string nameForEvent;
        lock (_lock)
        {
            var dev = _devices.TryGetValue(address, out var existing)
                ? existing
                : new BleDevice(address);

            if (!string.IsNullOrWhiteSpace(advName))
            {
                dev.Name = advName;
            }

            if (companyId != 0) dev.CompanyId = companyId;

            try { dev.IsRandomAddress = args.BluetoothAddressType != BluetoothAddressType.Public; }
            catch { }

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

        if (string.IsNullOrWhiteSpace(nameForEvent))
        {
            TryResolveNameViaGatt(address);
        }
    }

    /// <summary>
    /// 兜底：连接后读 GAP 设备名特征值 (0x2A00)。Apple 设备会拒绝，属正常
    /// </summary>
    private async void TryResolveNameViaGatt(ulong address)
    {
        bool shouldStart;
        lock (_lock) { shouldStart = _nameLookupStarted.Add(address); }
        if (!shouldStart) return;

        try
        {
            var devTask = BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask();
            var timeout = Task.Delay(TimeSpan.FromSeconds(6));

            if (await Task.WhenAny(devTask, timeout) != devTask) return;

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
        catch { }
    }

    public static ulong ParseAddress(string mac)
    {
        // 兼容 "AA:BB:CC:DD:EE:FF" 与 "Bluetooth#BluetoothAA:BB:..." 等形式，取末尾 12 位十六进制
        var clean = mac.Replace(":", "").Replace("-", "").Replace(" ", "");
        if (clean.Length > 12) clean = clean.Substring(clean.Length - 12);
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
