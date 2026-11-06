using System.Windows;
using System.Windows.Threading;

namespace ProximityLock;

public partial class App : System.Windows.Application
{
    public static AppConfig Config { get; private set; } = null!;
    public static BluetoothScanner Scanner { get; private set; } = null!;
    public static LockService LockService { get; private set; } = null!;
    public static MainWindow? MainForm { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Config = AppConfig.Load();
        Scanner = new BluetoothScanner();
        LockService = new LockService(Config, Scanner);

        Scanner.Start();
        // LockService timer 在构造函数中自动启动

        MainForm = new MainWindow();
        MainForm.Hide();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LockService.Stop();
        Scanner.Stop();
        Scanner.Dispose();
        Config.Save();
        base.OnExit(e);
    }
}
