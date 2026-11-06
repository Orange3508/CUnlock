using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ProximityLock;
using WF = System.Windows.Forms;

namespace ProximityLock;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly WF.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _listTimer;
    private int _listTick;
    private bool _slidersReady;

    public LockService.DistanceState CurrentState => App.LockService.CurrentState;
    public short? CurrentRssi => App.LockService.LastRssi;

    public string StatusText
    {
        get
        {
            return App.LockService.CurrentState switch
            {
                LockService.DistanceState.Near => "设备在附近",
                LockService.DistanceState.Far => "设备已远离",
                _ => "等待扫描目标设备"
            };
        }
    }

    public string CurrentStateLabel
    {
        get
        {
            return App.LockService.CurrentState switch
            {
                LockService.DistanceState.Near => "在范围内",
                LockService.DistanceState.Far => "超出范围",
                _ => "待机"
            };
        }
    }

    public string RssiText
    {
        get
        {
            var rssi = App.LockService.LastRssi;
            var src = App.LockService.Source;
            if (rssi.HasValue)
                return $"RSSI {rssi.Value} dBm · 约 {AppConfig.RssiToMeters(rssi.Value):0.0} m   来源: {src}";
            if (!string.IsNullOrEmpty(src)) return $"信号来源: {src}";
            return "未检测到信号";
        }
    }

    public string SelectedDeviceInfo
    {
        get
        {
            var cfg = App.Config;
            if (cfg.TargetDeviceAddress == "") return "未选择目标设备";
            return $"当前绑定: {cfg.TargetDeviceName} ({cfg.TargetDeviceAddress})";
        }
    }

    public bool AutoLockEnabled
    {
        get => App.Config.AutoLockEnabled;
        set { App.Config.AutoLockEnabled = value; App.Config.Save(); OnPropertyChanged(); }
    }

    public bool AutoWakeEnabled
    {
        get => App.Config.AutoWakeEnabled;
        set { App.Config.AutoWakeEnabled = value; App.Config.Save(); OnPropertyChanged(); }
    }

    public int LockThreshold
    {
        get => App.Config.LockThreshold;
        set { App.Config.LockThreshold = value; App.Config.Save(); OnPropertyChanged(); }
    }

    public int WakeThreshold
    {
        get => App.Config.WakeThreshold;
        set { App.Config.WakeThreshold = value; App.Config.Save(); OnPropertyChanged(); }
    }

    public double LockDistanceMeters
    {
        get => App.Config.LockDistanceMeters;
        set
        {
            App.Config.LockDistanceMeters = value;
            App.Config.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(LockThreshold));
        }
    }

    public double WakeDistanceMeters
    {
        get => App.Config.WakeDistanceMeters;
        set
        {
            App.Config.WakeDistanceMeters = value;
            App.Config.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(WakeThreshold));
        }
    }

    public int LockTriggerCount
    {
        get => App.Config.LockTriggerCount;
        set { App.Config.LockTriggerCount = value; App.Config.Save(); OnPropertyChanged(); }
    }

    public int WakeTriggerCount
    {
        get => App.Config.WakeTriggerCount;
        set { App.Config.WakeTriggerCount = value; App.Config.Save(); OnPropertyChanged(); }
    }

    public int ScanIntervalMs
    {
        get => App.Config.ScanIntervalMs;
        set { App.Config.ScanIntervalMs = value; App.Config.Save(); OnPropertyChanged(); }
    }

    public int LockCooldownSeconds
    {
        get => App.Config.LockCooldownSeconds;
        set { App.Config.LockCooldownSeconds = value; App.Config.Save(); OnPropertyChanged(); }
    }

    public bool AutoStart
    {
        get => App.Config.AutoStart;
        set { App.Config.AutoStart = value; SetAutoStart(value); OnPropertyChanged(); }
    }

    public bool MonitorOffMode
    {
        get => App.Config.MonitorOffMode;
        set { App.Config.MonitorOffMode = value; App.Config.Save(); OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private Icon _nearIcon = null!;
    private Icon _farIcon = null!;

    private static Icon CreateTrayIconIcon(System.Drawing.Color color)
    {
        using var bmp = new System.Drawing.Bitmap(16, 16);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 12, 12);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        App.LockService.StateChanged += OnStateChanged;
        App.LockService.Log += OnLog;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _uiTimer.Tick += (s, e) => RefreshBindings();
        _uiTimer.Start();

        // 蓝牙列表自动刷新（窗口可见时每 2 秒重建一次，名称会随扫描补齐）
        _listTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _listTimer.Tick += (s, e) =>
        {
            if (!IsVisible) return;

            _listTick++;
            // 每 30 秒重新枚举一次已配对设备，配对状态变化能及时反映
            if (_listTick % 15 == 0) App.Scanner.RefreshPairedDevices();

            RefreshScanList();
        };
        _listTimer.Start();

        _nearIcon = CreateTrayIconIcon(System.Drawing.Color.LimeGreen);
        _farIcon = CreateTrayIconIcon(System.Drawing.Color.OrangeRed);

        _trayIcon = new WF.NotifyIcon
        {
            Icon = _nearIcon,
            Visible = true,
            Text = "ProximityLock"
        };
        _trayIcon.DoubleClick += (s, e) => ShowWindow();
        _trayIcon.MouseClick += OnTrayClick;

        Loaded += (s, e) =>
        {
            InitDistanceSliders();
            RefreshScanList();
        };
    }

    /// <summary>把配置里的距离写入滑块（避免双向绑定在初始化时把 0 写回配置）</summary>
    private void InitDistanceSliders()
    {
        _slidersReady = false;

        // 旧版本存的是 RSSI 阈值，换算后可能落在滑块范围外，这种情况下回到默认值
        var lockD = App.Config.LockDistanceMeters;
        if (lockD < LockSlider.Minimum || lockD > LockSlider.Maximum) lockD = 4.0;
        else lockD = Math.Round(lockD * 2) / 2; // 对齐到 0.5 刻度

        var wakeD = App.Config.WakeDistanceMeters;
        if (wakeD < WakeSlider.Minimum || wakeD > WakeSlider.Maximum) wakeD = 2.0;
        else wakeD = Math.Round(wakeD * 2) / 2;

        LockSlider.Value = lockD;
        WakeSlider.Value = wakeD;

        App.Config.LockDistanceMeters = lockD;
        App.Config.WakeDistanceMeters = wakeD;
        App.Config.Save();

        _slidersReady = true;
        OnPropertyChanged(nameof(LockDistanceMeters));
        OnPropertyChanged(nameof(WakeDistanceMeters));
    }

    private void DistanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_slidersReady) return;

        if (ReferenceEquals(sender, LockSlider))
        {
            App.Config.LockDistanceMeters = e.NewValue;
            OnPropertyChanged(nameof(LockDistanceMeters));
        }
        else
        {
            App.Config.WakeDistanceMeters = e.NewValue;
            OnPropertyChanged(nameof(WakeDistanceMeters));
        }

        App.Config.Save();
    }

    private void RefreshBindings()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RssiText));
        OnPropertyChanged(nameof(CurrentState));
        OnPropertyChanged(nameof(CurrentStateLabel));
        OnPropertyChanged(nameof(SelectedDeviceInfo));
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshBindings();
            _trayIcon.Icon = e.State == LockService.DistanceState.Far ? _farIcon : _nearIcon;
            _trayIcon.Text = e.State switch
            {
                LockService.DistanceState.Near => "ProximityLock - 设备附近",
                LockService.DistanceState.Far => "ProximityLock - 设备远离",
                _ => "ProximityLock - 等待扫描"
            };
        });
    }

    private void OnLog(object? sender, LogEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var item = $"[{e.Time:HH:mm:ss}] {e.Message}";
            LogList.Items.Insert(0, item);
            while (LogList.Items.Count > 200) LogList.Items.RemoveAt(LogList.Items.Count - 1);
        });
    }

    private void RefreshScanList()
    {
        // 保留当前选中项，避免刷新后丢失
        var keepAddress = (DeviceCombo.SelectedItem as DeviceItem)?.AddressHex;
        if (string.IsNullOrEmpty(keepAddress) && !string.IsNullOrEmpty(App.Config.TargetDeviceAddress))
        {
            keepAddress = App.Config.TargetDeviceAddress;
        }

        var merged = new Dictionary<ulong, DeviceItem>();

        // 1. 已配对设备优先（系统里有真实名称）
        foreach (var p in App.Scanner.PairedDevices)
        {
            merged[p.Address] = new DeviceItem(p.Address, p.DisplayName, 0, p.AddressHex, true, p.Name);
        }

        // 2. BLE 扫描到的设备（8 秒内有效），同名地址合并
        foreach (var kv in App.Scanner.Devices)
        {
            var d = kv.Value;
            if ((DateTimeOffset.Now - d.LastSeen).TotalSeconds > 8) continue;

            if (merged.TryGetValue(d.Address, out var exist))
            {
                exist.Rssi = d.Rssi;
                if (!string.IsNullOrWhiteSpace(d.Name)) exist.Name = d.Name;
            }
            else
            {
                merged[d.Address] = new DeviceItem(
                    d.Address, d.DisplayName, d.Rssi, d.AddressHex,
                    false, d.DisplayName);
            }
        }

        DeviceCombo.Items.Clear();
        foreach (var item in merged.Values
                     .OrderByDescending(d => d.IsPaired)
                     .ThenByDescending(d => d.Rssi)
                     .ThenBy(d => d.Name))
        {
            DeviceCombo.Items.Add(item);
        }

        if (!string.IsNullOrEmpty(keepAddress))
        {
            var match = DeviceCombo.Items.Cast<DeviceItem>()
                .FirstOrDefault(d => d.AddressHex.Equals(keepAddress, StringComparison.OrdinalIgnoreCase));
            if (match != null) DeviceCombo.SelectedItem = match;
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not DeviceItem item) return;

        // 刷新列表会重复触发，地址没变就不重复保存/打日志
        if (string.Equals(App.Config.TargetDeviceAddress, item.AddressHex, StringComparison.OrdinalIgnoreCase))
            return;

        App.Config.TargetDeviceAddress = item.AddressHex;
        App.Config.TargetDeviceName = item.Name;
        App.Config.Save();
        OnPropertyChanged(nameof(SelectedDeviceInfo));
        OnLog(this, new LogEventArgs($"🎯 目标设备: {item.Name} ({item.AddressHex})"));
    }

    private void RefreshScan_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Scanner.IsScanning) App.Scanner.Start();
        App.Scanner.RefreshPairedDevices();
        RefreshScanList();
        OnLog(this, new LogEventArgs("🔄 刷新蓝牙扫描列表"));
    }

    /// <summary>绑定当前信号最强的设备（站到电脑前点一下，适合 iPhone 这种无名称设备）</summary>
    private void BindStrongest_Click(object sender, RoutedEventArgs e)
    {
        var best = App.Scanner.Devices.Values
            .Where(d => (DateTimeOffset.Now - d.LastSeen).TotalSeconds <= 8)
            .OrderByDescending(d => d.Rssi)
            .FirstOrDefault();

        if (best == null)
        {
            OnLog(this, new LogEventArgs("⚠️ 没扫到任何设备，请先点「刷新扫描」"));
            return;
        }

        App.Config.TargetDeviceAddress = best.AddressHex;
        App.Config.TargetDeviceName = best.DisplayName;
        App.Config.Save();

        RefreshScanList();
        var match = DeviceCombo.Items.Cast<DeviceItem>()
            .FirstOrDefault(d => d.AddressHex.Equals(best.AddressHex, StringComparison.OrdinalIgnoreCase));
        if (match != null) DeviceCombo.SelectedItem = match;

        OnLog(this, new LogEventArgs($"🎯 已绑定最强信号: {best.DisplayName} ({best.Rssi} dBm)"));
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        App.Config.Save();
        OnLog(this, new LogEventArgs("💾 设置已保存"));
        System.Windows.MessageBox.Show(this, "设置已保存", "ProximityLock",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _trayIcon.Visible = false;
        App.Config.Save();
        System.Windows.Application.Current.Shutdown();
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnTrayClick(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button == WF.MouseButtons.Right) ShowTrayMenu();
    }

    private void ShowTrayMenu()
    {
        var menu = new WF.ContextMenuStrip();

        var itemShow = new WF.ToolStripMenuItem("显示主窗口");
        itemShow.Click += (s, e) => ShowWindow();

        var header = App.Config.AutoLockEnabled ? "⏸ 暂停自动锁屏" : "▶ 启用自动锁屏";
        var itemToggle = new WF.ToolStripMenuItem(header);
        itemToggle.Click += (s, e) =>
        {
            App.Config.AutoLockEnabled = !App.Config.AutoLockEnabled;
            App.Config.Save();
            RefreshBindings();
        };

        var itemExit = new WF.ToolStripMenuItem("退出");
        itemExit.Click += (s, e) =>
        {
            _trayIcon.Visible = false;
            App.Config.Save();
            System.Windows.Application.Current.Shutdown();
        };

        menu.Items.Add(itemShow);
        menu.Items.Add(itemToggle);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(itemExit);

        _trayIcon.ContextMenuStrip = menu;
    }

    private void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (enable && exePath != null)
                key.SetValue("ProximityLock", $"\"{exePath}\"");
            else
                key.DeleteValue("ProximityLock", false);
        }
        catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_trayIcon.Visible)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon.Dispose();
        _nearIcon.Dispose();
        _farIcon.Dispose();
        base.OnClosed(e);
    }
}

public class DeviceItem
{
    public ulong Address { get; }
    public string Name { get; set; }
    public short Rssi { get; set; }
    public string AddressHex { get; }
    public bool IsPaired { get; }
    public bool NameUnknown { get; }

    public string DisplayText
    {
        get
        {
            if (IsPaired) return $"[已配对] {Name}   {AddressHex}";
            if (NameUnknown) return $"(无名称) {AddressHex}   {Rssi} dBm";
            return $"{Name}   {Rssi} dBm";
        }
    }

    public DeviceItem(ulong address, string displayName, short rssi, string addressHex,
                      bool isPaired, string rawName, bool nameUnknown = false)
    {
        Address = address;
        Name = string.IsNullOrWhiteSpace(rawName) ? displayName : rawName;
        Rssi = rssi;
        AddressHex = addressHex;
        IsPaired = isPaired;
        NameUnknown = nameUnknown;
    }

    public override string ToString() => DisplayText;
}
