using System.Runtime.InteropServices;

namespace ProximityLock;

public class LockService
{
    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

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
    private short? _lastRssi;

    public DistanceState CurrentState => _state;
    public short? LastRssi => _lastRssi;
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

            if (!_scanner.Devices.TryGetValue(address, out var device))
            {
                _lastRssi = null;
                _lowRssiCount++;
                _highRssiCount = 0;
                CheckLock();
                return;
            }

            _lastRssi = device.Rssi;

            if (device.Rssi <= _config.LockThreshold)
            {
                _lowRssiCount++;
                _highRssiCount = 0;
                CheckLock();
            }
            else if (device.Rssi >= _config.WakeThreshold)
            {
                _highRssiCount++;
                _lowRssiCount = 0;
                CheckWake();
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

        if (_state != DistanceState.Near)
        {
            _state = DistanceState.Near;
            StateChanged?.Invoke(this, new StateChangedEventArgs(_state, _lastRssi));
            Log?.Invoke(this, new LogEventArgs($"状态变化: 靠近 (RSSI={_lastRssi})"));
        }

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

    private void DoWake()
    {
        if ((DateTime.Now - _lastLockTime).TotalSeconds < _config.LockCooldownSeconds * 2) return;

        try
        {
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki = new KEYBDINPUT { wVk = VK_SPACE };

            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].U.ki = new KEYBDINPUT { wVk = VK_SPACE, dwFlags = KEYEVENTF_KEYUP };

            SendInput(2, inputs, INPUT.Size);
            Log?.Invoke(this, new LogEventArgs("🌅 已模拟按键唤醒屏幕"));
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, new LogEventArgs($"唤醒失败: {ex.Message}"));
        }
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
