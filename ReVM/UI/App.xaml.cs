using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ReVM;

public partial class App : Application
{
    private static string? _logPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (OfficialEmulatorNetworkIsolation.TryHandleElevatedWorker(e.Args, out var workerExitCode))
        {
            Shutdown(workerExitCode);
            return;
        }

        Directory.CreateDirectory(RevmPaths.RuntimeDir);
        Directory.CreateDirectory(RevmPaths.LogsDir);
        _logPath = Path.Combine(RevmPaths.LogsDir, "revm-crash.log");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
            MessageBox.Show(args.Exception.Message, "ReVM Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_logPath)) return;
            File.AppendAllText(_logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}\r\n{ex}\r\n\r\n");
        }
        catch
        {
            // Do not crash while trying to log a crash.
        }
    }
}
