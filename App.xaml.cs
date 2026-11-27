using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace LocalDeck;

public partial class App : Application
{
    public static bool DemoMode { get; private set; }
    public static string? CaptureDirectory { get; private set; }

    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UnblockCurrentExecutable();

        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var ok = SelfTest.Run();
            Shutdown(ok ? 0 : 1);
            return;
        }

        DemoMode = e.Args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));
        RefreshTaskbarIdentity();

        var captureIndex = Array.FindIndex(e.Args,
            a => string.Equals(a, "--capture", StringComparison.OrdinalIgnoreCase));
        if (captureIndex >= 0)
        {
            CaptureDirectory = captureIndex + 1 < e.Args.Length
                ? Path.GetFullPath(e.Args[captureIndex + 1])
                : Path.Combine(Environment.CurrentDirectory, "shots");
        }

        // Only one visible dashboard. Re-clicking the exe brings the existing window
        // forward instead of creating another background process.
        // Skip the single-instance gate during --capture so screenshots can be rendered
        // without interfering with a live instance running in the tray.
        if (CaptureDirectory is null)
        {
            try
            {
                _instanceMutex = new Mutex(initiallyOwned: true,
                    name: @"Local\LocalDeck.SingleInstance", createdNew: out var createdNew);
                _ownsMutex = createdNew;
                if (!createdNew)
                {
                    BringExistingWindowForward();
                    Shutdown(0);
                    return;
                }
            }
            catch
            {
                // A mutex failure must not prevent the app from starting.
            }
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var window = new MainWindow(CaptureDirectory);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ReportError("启动窗口", ex);
            if (CaptureDirectory is null)
            {
                MessageBox.Show(
                    "桌仪启动失败。\n\n" + shortMessage(ex) + "\n\n错误日志：\n" + ErrorLogPath,
                    "桌仪 LocalDeck", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportError("界面线程", e.Exception);
        if (CaptureDirectory is null)
        {
            MessageBox.Show(
                "桌仪遇到错误，已退出。\n\n" + shortMessage(e.Exception) + "\n\n错误日志：\n" + ErrorLogPath,
                "桌仪 LocalDeck", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        Shutdown(1);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) ReportError("未处理异常", ex);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportError("后台任务", e.Exception);
        e.SetObserved();
    }

    private static string shortMessage(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Length > 600 ? message[..600] : message;
    }

    public static string ErrorLogPath { get; } = ResolveErrorLogPath();

    private static string ResolveErrorLogPath()
    {
        try
        {
            var besideExe = Path.Combine(AppContext.BaseDirectory, "LocalDeck-error.log");
            var probe = besideExe + ".probe";
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return besideExe;
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "LocalDeck-error.log");
        }
    }

    private static void ReportError(string context, Exception exception)
    {
        try
        {
            var text =
                $"{DateTimeOffset.Now:O}  {context}{Environment.NewLine}" +
                $"App {typeof(App).Assembly.GetName().Version}  OS {Environment.OSVersion}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{new string('-', 72)}{Environment.NewLine}";
            File.AppendAllText(ErrorLogPath, text);
        }
        catch
        {
            // Error reporting must never throw.
        }
    }

    private static void BringExistingWindowForward()
    {
        var hwnd = FindWindowW(null, "桌仪");
        if (hwnd == IntPtr.Zero)
        {
            MessageBox.Show(
                "桌仪已经在运行，但没有找到窗口。\n\n请在任务管理器中结束 LocalDeck.exe 后再试。",
                "桌仪 LocalDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowWindow(hwnd, 9); // SW_RESTORE
        SetForegroundWindow(hwnd);
    }

    private static void UnblockCurrentExecutable()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            DeleteFileW(exe + ":Zone.Identifier");
        }
        catch
        {
            // Removing Mark-of-the-Web is best-effort; unsigned first runs may
            // still show SmartScreen before this code can execute.
        }
    }

    private static void RefreshTaskbarIdentity()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID("LocalDeck.Desktop.77");
        }
        catch
        {
            // Cosmetic only; never block startup.
        }

        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LocalDeck.exe");
            SHChangeNotify(0x00002000, 0x0005, exe, IntPtr.Zero); // SHCNE_UPDATEITEM, SHCNF_PATHW
        }
        catch
        {
            // Shell icon cache refresh is best-effort.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string fileName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
