using System.Runtime.InteropServices;

namespace ProximityLock;

public class LockService
{
    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
        public static int Size => Marshal.SizeOf(typeof(INPUT));
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_SPACE = 0x20;

    public enum DistanceState { Near, Far, Unknown }

    private readonly AppConfig _config;
    private readonly BluetoothScanner _scanner;
    private readonly System.Threading.Timer _timer;

    private int _lowRssiCount;
    private int _highRssiCount;
    private DistanceState _state = DistanceState.Unknown;
    private DateTime _lastLockTime = DateTime.MinValue;
    private DateTime _lastWakeTime = DateTime.MinValue;
    private short? _lastRssi;
    private string _source = "";
    private bool _pairedEverConnected;
    private ushort _targetCompanyId;
    private DateTime _targetLastSeen = DateTime.MinValue;

    public DistanceState CurrentState => _state;
    public short? LastRssi => _lastRssi;
    public string Source => _source;
    public event EventHandler<StateChangedEventArgs>? StateChanged;
    public event EventHandler<LogEventArgs>? Log;

    public LockService(AppConfig config, BluetoothScanner scanner)
    {
        _config = config;
        _scanner = scanner;

        _timer = new System.Threading.Timer(MonitorLoop, null,
            TimeSpan.FromMilliseconds(config.ScanIntervalMs),
            TimeSpan.FromMilliseconds(config.ScanIntervalMs));
    }

    public void Stop() { _timer.Dispose(); }

    private void MonitorLoop(object? state)
    {
        if (_config.TargetDeviceAddress == "") return;

        try
        {
            var address = BluetoothScanner.ParseAddress(_config.TargetDeviceAddress);
            _scanner.TargetAddress = address;

            // BLE 信号超过 8 秒没刷新就算失效，避免旧的 RSSI 一直留在列表里
            var fresh = _scanner.GetFreshDevice(address, TimeSpan.FromSeconds(8));

            // iPhone 等 Apple 设备会定期轮换随机蓝牙地址，这里自动跟随同厂商最强信号
            if (fresh == null && _targetCompanyId != 0)
            {
                var candidate = _scanner.FindStrongestByCompany(_targetCompanyId, TimeSpan.FromSeconds(6));
                if (candidate != null && candidate.Address != address)
                {
                    var oldDev = _scanner.GetFreshDevice(address, TimeSpan.FromDays(1));
                    var oldRssi = oldDev?.Rssi ?? -60;

                    // 只有信号强度接近才接管，避免误切到附近的其它同厂商设备
                    if (Math.Abs(candidate.Rssi - oldRssi) <= 25)
                    {
                        _scanner.TargetAddress = candidate.Address;
                        _config.TargetDeviceAddress = candidate.AddressHex;
                        _config.Save();

                        address = candidate.Address;
                        fresh = candidate;
                        Log?.Invoke(this, new LogEventArgs($"🔄 目标地址已轮换，自动跟随到 {candidate.AddressHex}"));
                    }
                }
            }

            if (fresh != null)
            {
                _lastRssi = fresh.Rssi;
                _source = "BLE 信号";
                if (fresh.CompanyId != 0) _targetCompanyId = fresh.CompanyId;

                if (fresh.Rssi <= _config.LockThreshold)
                {
                    _lowRssiCount++;
                    _highRssiCount = 0;
                    CheckLock();
                }
                else if (fresh.Rssi >= _config.WakeThreshold)
                {
                    _highRssiCount++;
                    _lowRssiCount = 0;
                    CheckWake();
                }
                return;
            }

            // BLE 扫不到：如果以前扫到过，说明确实走远了
            if (_scanner.WasSeenBefore(address))
            {
                _lastRssi = null;
                _source = "BLE 信号消失";
                _lowRssiCount++;
                _highRssiCount = 0;
                CheckLock();
                return;
            }

            // 从未在 BLE 出现：用已配对设备的经典蓝牙连接状态判断
            _scanner.RequestPairedStatus(address);
            var connected = _scanner.PairedConnected;

            if (connected == null) return; // 未知，不做任何判断，避免误锁
            if (connected == true) _pairedEverConnected = true;

            // 从未连接过就一直是断开状态的，多半是 LE 配对设备（如 iPhone），不能用它判断远近
            if (connected == false && !_pairedEverConnected) return;

            _lastRssi = null;
            _source = connected == true ? "已配对设备连接中" : "已配对设备已断开";

            if (connected == true)
            {
                _highRssiCount++;
                _lowRssiCount = 0;
                CheckWake();
            }
            else
            {
                _lowRssiCount++;
                _highRssiCount = 0;
                CheckLock();
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, new LogEventArgs($"监控异常: {ex.Message}"));
        }
    }

    private void CheckLock()
    {
        if (_lowRssiCount < _config.LockTriggerCount) return;

        var now = DateTime.Now;
        if ((now - _lastLockTime).TotalSeconds < _config.LockCooldownSeconds) return;

        if (_state != DistanceState.Far)
        {
            _state = DistanceState.Far;
            StateChanged?.Invoke(this, new StateChangedEventArgs(_state, _lastRssi));
            Log?.Invoke(this, new LogEventArgs($"状态变化: 远离 (RSSI={_lastRssi})"));
        }

        if (_config.AutoLockEnabled) DoLock();
    }

    private void CheckWake()
    {
        if (_highRssiCount < _config.WakeTriggerCount) return;

        bool wasNear = _state == DistanceState.Near;

        if (!wasNear)
        {
            _state = DistanceState.Near;
            StateChanged?.Invoke(this, new StateChangedEventArgs(_state, _lastRssi));
            Log?.Invoke(this, new LogEventArgs($"状态变化: 靠近 (RSSI={_lastRssi})"));
        }

        // 只在「远离/待机 → 靠近」的转换瞬间唤醒一次，避免持续在附近时反复发送按键
        if (wasNear) return;
        if ((DateTime.Now - _lastWakeTime).TotalSeconds < 10) return;

        _highRssiCount = 0;
        if (_config.AutoWakeEnabled) DoWake();
    }

    private void DoLock()
    {
        _lastLockTime = DateTime.Now;
        _lowRssiCount = 0;
        _highRssiCount = 0;

        try
        {
            LockWorkStation();
            Log?.Invoke(this, new LogEventArgs("🔒 已锁屏"));
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, new LogEventArgs($"锁屏失败: {ex.Message}"));
        }
    }

    private async void DoWake()
    {
        _lastWakeTime = DateTime.Now;

        try
        {
            // 锁屏界面在独立的 secure desktop 上，模拟按键无法送达；
            // 改用系统电源请求点亮显示器（不会解除锁屏，PIN 仍需手动输入）
            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

            if (IsWorkstationLocked())
                Log?.Invoke(this, new LogEventArgs("🌅 已点亮屏幕（Windows 要求输入 PIN 解锁）"));
            else
                Log?.Invoke(this, new LogEventArgs("🌅 已点亮屏幕"));

            await Task.Delay(3000);
            SetThreadExecutionState(ES_CONTINUOUS);
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, new LogEventArgs($"点亮屏幕失败: {ex.Message}"));
        }
    }

    /// <summary>当前是否处于锁屏状态</summary>
    private static bool IsWorkstationLocked()
    {
        try
        {
            var hdesk = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
            if (hdesk == IntPtr.Zero) return true; // 打不开输入桌面说明在 secure desktop
            CloseDesktop(hdesk);
            return false;
        }
        catch { return false; }
    }
}

public class StateChangedEventArgs : EventArgs
{
    public LockService.DistanceState State { get; }
    public short? Rssi { get; }
    public StateChangedEventArgs(LockService.DistanceState state, short? rssi) { State = state; Rssi = rssi; }
}

public class LogEventArgs : EventArgs
{
    public string Message { get; }
    public DateTime Time { get; } = DateTime.Now;
    public LogEventArgs(string msg) { Message = msg; }
}
