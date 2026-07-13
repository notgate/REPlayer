using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReVM;

public partial class AndroidToolsDialog : Window
{
    private readonly string _adbPath;
    private readonly string _serial;
    private Process? _logcatProcess;

    public ObservableCollection<AndroidToolRow> ToolRows { get; } = new();
    public ObservableCollection<AndroidPackageRow> PackageRows { get; } = new();
    public ObservableCollection<AndroidProcessRow> ProcessRows { get; } = new();
    public ObservableCollection<AndroidLogcatRow> LogRows { get; } = new();

    public AndroidToolsDialog() : this("adb", "emulator-5554") { }

    public AndroidToolsDialog(string adbPath, string serial)
    {
        InitializeComponent();
        _adbPath = string.IsNullOrWhiteSpace(adbPath) ? "adb" : adbPath;
        _serial = string.IsNullOrWhiteSpace(serial) ? "emulator-5554" : serial;
        SerialText.Text = _serial;
        DataContext = this;
        AddOutput("Android tools ready.");
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        StopLogcatProcess();
        base.OnClosed(e);
    }

    private async void RefreshOverview_Click(object sender, RoutedEventArgs e)
    {
        ToolRows.Clear();
        ToolRows.Add(new AndroidToolRow("adb", "binary", _adbPath, File.Exists(_adbPath) ? "bundled runtime adb.exe" : "missing adb.exe", File.Exists(_adbPath) ? "ok" : "fail"));
        ToolRows.Add(new AndroidToolRow("adb", "serial", _serial, "official emulator serial used by REPlayer tooling", "fixed"));
        await AddOverviewAsync("device", "get-state", "get-state");
        await AddOverviewAsync("device", "boot_completed", "shell getprop sys.boot_completed");
        await AddOverviewAsync("device", "model", "shell getprop ro.product.model");
        await AddOverviewAsync("device", "fingerprint", "shell getprop ro.build.fingerprint");
        await AddOverviewAsync("device", "sdk", "shell getprop ro.build.version.sdk");
        await AddOverviewAsync("debug", "ro.debuggable", "shell getprop ro.debuggable");
        await AddOverviewAsync("network", "http_proxy", "shell settings get global http_proxy");
        await AddOverviewAsync("network", "connectivity", "shell dumpsys connectivity | head -40");
        await AddOverviewAsync("packages", "third-party count", "shell pm list packages -3");
        StatusText.Text = "Device overview refreshed";
    }

    private void OpenShell_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{_adbPath}\" -s {_serial} shell") { UseShellExecute = true });
    }

    private void StartMitm_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new MitmproxyDialog(_adbPath) { Owner = this };
        dlg.ShowDialog();
    }

    private async void Bugreport_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(RevmPaths.BaseDir, "captures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"bugreport-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var result = await RunAdbAsync($"bugreport \"{path}\"", 120000);
        AddOutput(result.output);
        StatusText.Text = result.ok ? $"Bugreport saved: {path}" : "Bugreport failed";
    }

    private async void StartActivity_Click(object sender, RoutedEventArgs e)
    {
        var packageName = PackageBox.Text.Trim();
        var activity = ActivityBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(packageName)) { StatusText.Text = "Enter/select a package first."; return; }
        var args = string.IsNullOrWhiteSpace(activity)
            ? $"shell monkey -p {packageName} -c android.intent.category.LAUNCHER 1"
            : $"shell am start -n {NormalizeComponent(packageName, activity)}";
        AddOutput((await RunAdbAsync(args, 15000)).output);
    }

    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PackageBox.Text)) return;
        AddOutput((await RunAdbAsync($"shell pm clear {PackageBox.Text.Trim()}", 15000)).output);
    }

    private async void PullApk_Click(object sender, RoutedEventArgs e)
    {
        var pkg = PackageBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(pkg)) { StatusText.Text = "Enter/select a package first."; return; }

        var remoteApk = PackagesGrid.SelectedItem is AndroidPackageRow row && row.PackageName == pkg
            ? row.ApkPath
            : string.Empty;
        if (string.IsNullOrWhiteSpace(remoteApk))
        {
            var path = await RunAdbAsync($"shell pm path {pkg}", 10000);
            AddOutput(path.output);
            foreach (var line in path.output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
                {
                    remoteApk = line[8..].Trim();
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(remoteApk))
        {
            StatusText.Text = $"Could not resolve APK path for {pkg}";
            return;
        }

        var dir = Path.Combine(RevmPaths.BaseDir, "captures", "apks");
        Directory.CreateDirectory(dir);
        var localPath = Path.Combine(dir, SanitizeFileName(pkg) + ".apk");
        var pull = await RunAdbAsync($"pull \"{remoteApk}\" \"{localPath}\"", 60000);
        AddOutput(pull.output);
        StatusText.Text = pull.ok ? $"APK pulled: {localPath}" : $"APK pull failed for {pkg}";
    }

    private async void RunDumpsys_Click(object sender, RoutedEventArgs e)
    {
        var tag = (DumpsysCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "activity top";
        var arg = tag == "package" ? $"package {PackageBox.Text.Trim()}" : tag;
        AddOutput((await RunAdbAsync($"shell dumpsys {arg}", 20000)).output);
    }

    private async void RefreshPackages_Click(object sender, RoutedEventArgs e)
    {
        PackageRows.Clear();
        var result = await RunAdbAsync("shell pm list packages -3 -f", 20000);
        foreach (var line in result.output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("package:", StringComparison.OrdinalIgnoreCase)) continue;
            var body = trimmed[8..];
            var eq = body.LastIndexOf('=');
            if (eq > 0) PackageRows.Add(new AndroidPackageRow(body[(eq + 1)..], body[..eq], "user"));
        }
        StatusText.Text = $"Loaded {PackageRows.Count} packages";
    }

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        ProcessRows.Clear();
        var result = await RunAdbAsync("shell ps -A", 20000);
        foreach (var line in result.output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || parts[0].Equals("USER", StringComparison.OrdinalIgnoreCase)) continue;
            ProcessRows.Add(new AndroidProcessRow(parts[0], parts[1], parts.Length > 2 ? parts[2] : "", parts[^1]));
        }
        StatusText.Text = $"Loaded {ProcessRows.Count} processes";
    }

    private void PackagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackagesGrid.SelectedItem is AndroidPackageRow row)
            PackageBox.Text = row.PackageName;
    }

    private void StartLogcat_Click(object sender, RoutedEventArgs e)
    {
        StopLogcatProcess();
        var filter = LogcatFilterBox.Text.Trim();
        var args = $"-s {_serial} logcat -v threadtime";
        _logcatProcess = new Process
        {
            StartInfo = new ProcessStartInfo(_adbPath, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        _logcatProcess.OutputDataReceived += (_, ev) => Dispatcher.Invoke(() => AddLogcatLine(ev.Data, filter));
        _logcatProcess.ErrorDataReceived += (_, ev) => Dispatcher.Invoke(() => AddOutput(ev.Data ?? string.Empty));
        _logcatProcess.Start();
        _logcatProcess.BeginOutputReadLine();
        _logcatProcess.BeginErrorReadLine();
        StatusText.Text = "Logcat streaming";
    }

    private void StopLogcat_Click(object sender, RoutedEventArgs e) => StopLogcatProcess();

    private async void DumpLogcat_Click(object sender, RoutedEventArgs e)
    {
        var result = await RunAdbAsync("logcat -d -v threadtime", 30000);
        foreach (var line in result.output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            AddLogcatLine(line, LogcatFilterBox.Text.Trim());
    }

    private async void ClearLogcat_Click(object sender, RoutedEventArgs e) => AddOutput((await RunAdbAsync("logcat -c", 10000)).output);
    private void ClearLogView_Click(object sender, RoutedEventArgs e) => LogRows.Clear();

    private async Task AddOverviewAsync(string category, string name, string command)
    {
        var result = await RunAdbAsync(command, 10000);
        var value = result.output.Trim().Replace("\r", " ").Replace("\n", " ");
        if (name.Contains("count", StringComparison.OrdinalIgnoreCase))
            value = result.output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ToString(CultureInfo.InvariantCulture);
        ToolRows.Add(new AndroidToolRow(category, name, value.Length > 260 ? value[..260] + "..." : value, command, result.ok ? "ok" : "fail"));
    }

    private async Task<(bool ok, string output)> RunAdbAsync(string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(_adbPath, $"-s {_serial} {args}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ADB failed to start");
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            var wait = proc.WaitForExitAsync();
            if (await Task.WhenAny(wait, Task.Delay(timeoutMs)) != wait)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (false, "timeout");
            }
            var output = ((await stdout) + Environment.NewLine + (await stderr)).Trim();
            return (proc.ExitCode == 0, string.IsNullOrWhiteSpace(output) ? $"exit {proc.ExitCode}" : output);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private void AddLogcatLine(string? line, string filter)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (!string.IsNullOrWhiteSpace(filter) && !line.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var time = parts.Length > 1 ? $"{parts[0]} {parts[1]}" : DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var pid = parts.Length > 2 ? parts[2] : "";
        var pri = parts.Length > 4 ? parts[4] : "";
        var tag = parts.Length > 5 ? parts[5].TrimEnd(':') : "";
        LogRows.Add(new AndroidLogcatRow(time, pid, pri, tag, line));
        while (LogRows.Count > 1000) LogRows.RemoveAt(0);
    }

    private void StopLogcatProcess()
    {
        try { if (_logcatProcess is { HasExited: false }) _logcatProcess.Kill(entireProcessTree: true); } catch { }
        _logcatProcess?.Dispose();
        _logcatProcess = null;
        StatusText.Text = "Logcat stopped";
    }

    private void AddOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        OutputText.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        OutputText.ScrollToEnd();
    }

    private static string NormalizeComponent(string packageName, string activity)
    {
        if (activity.Contains('/')) return activity;
        return activity.StartsWith('.') ? $"{packageName}/{packageName}{activity}" : $"{packageName}/{activity}";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        }
        return new string(chars);
    }
}

public sealed record AndroidToolRow(string Category, string Name, string Value, string Details, string Status);
public sealed record AndroidPackageRow(string PackageName, string ApkPath, string Kind);
public sealed record AndroidProcessRow(string User, string Pid, string Ppid, string Name);
public sealed record AndroidLogcatRow(string Time, string Pid, string Priority, string Tag, string Message);
