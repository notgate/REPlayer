using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace ReVM;

public partial class MitmproxyDialog : Window
{
    private const string Serial = "emulator-5554";
    private readonly string _adbPath;
    private Process? _mitmProcess;

    public ObservableCollection<MitmCaptureRow> CaptureRows { get; } = new();

    public MitmproxyDialog() : this("adb")
    {
    }

    public MitmproxyDialog(string adbPath)
    {
        InitializeComponent();
        _adbPath = string.IsNullOrWhiteSpace(adbPath) ? "adb" : adbPath;
        DataContext = this;
        LoadDefaults();
        AppendRow("info", "", "", "", "", "Ready. Start mitmweb, then set Android proxy to 10.0.2.2:<port>.");
    }

    private void LoadDefaults()
    {
        var settingsPath = Path.Combine(RuntimeBackendFactory.GetBaseDir(), "runtime", "backend-settings.json");
        var settings = ReadSettings(settingsPath);
        PortBox.Text = Math.Clamp(settings.MitmProxyPort, 1024, 65535).ToString(CultureInfo.InvariantCulture);
        WebPortBox.Text = Math.Clamp(settings.MitmProxyPort + 1, 1024, 65535).ToString(CultureInfo.InvariantCulture);
        StatusText.Text = "mitmweb not started";
    }

    private static BackendSettings ReadSettings(string path)
    {
        try
        {
            if (!File.Exists(path)) return new BackendSettings();
            return JsonSerializer.Deserialize<BackendSettings>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new BackendSettings();
        }
        catch
        {
            return new BackendSettings();
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopMitmProcess();
        base.OnClosed(e);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_mitmProcess is { HasExited: false })
        {
            SetStatus("mitmweb is already running.");
            return;
        }

        if (!TryReadPort(PortBox.Text, out var listenPort) || !TryReadPort(WebPortBox.Text, out var webPort))
        {
            SetStatus("Invalid port. Use values from 1024 through 65535.");
            return;
        }

        var exe = FindOnPath("mitmweb") ?? FindOnPath("mitmweb.exe") ?? FindOnPath("mitmproxy") ?? FindOnPath("mitmproxy.exe");
        if (exe == null)
        {
            SetStatus("mitmproxy/mitmweb not found in PATH. Install mitmproxy, then reopen REPlayer or add Scripts to PATH.");
            AppendRow("tool", "", "", "", "missing", "mitmproxy executable not found in PATH");
            return;
        }

        var isWeb = Path.GetFileNameWithoutExtension(exe).Equals("mitmweb", StringComparison.OrdinalIgnoreCase);
        var args = $"--listen-host 0.0.0.0 --listen-port {listenPort}";
        if (isWeb)
            args += $" --web-host 127.0.0.1 --web-port {webPort}";

        var upstream = UpstreamBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(upstream))
            args += $" --mode upstream:{QuoteIfNeeded(upstream)}";

        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _mitmProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _mitmProcess.OutputDataReceived += (_, ev) => OnMitmOutput(ev.Data);
            _mitmProcess.ErrorDataReceived += (_, ev) => OnMitmOutput(ev.Data);
            _mitmProcess.Exited += (_, _) => Dispatcher.Invoke(() =>
            {
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                SetStatus($"mitm process exited ({_mitmProcess?.ExitCode}).");
                AppendRow("tool", "", "", "", "exit", $"mitm process exited ({_mitmProcess?.ExitCode})");
            });

            _mitmProcess.Start();
            _mitmProcess.BeginOutputReadLine();
            _mitmProcess.BeginErrorReadLine();
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            SetStatus($"Started {Path.GetFileName(exe)} on 0.0.0.0:{listenPort}; Android proxy target is 10.0.2.2:{listenPort}.");
            AppendRow("tool", "", "0.0.0.0", $":{listenPort}", "running", $"{Path.GetFileName(exe)} {args}");

            await Task.Delay(350);
            if (_mitmProcess.HasExited)
                SetStatus($"mitm process exited immediately ({_mitmProcess.ExitCode}). Check output rows.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to start mitmproxy: {ex.Message}");
            AppendRow("tool", "", "", "", "error", ex.Message);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopMitmProcess();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        SetStatus("mitmproxy stopped.");
    }

    private void StopMitmProcess()
    {
        try
        {
            if (_mitmProcess is { HasExited: false })
                _mitmProcess.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            _mitmProcess?.Dispose();
            _mitmProcess = null;
        }
    }

    private async void SetProxy_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadPort(PortBox.Text, out var port))
        {
            SetStatus("Invalid listen port.");
            return;
        }

        var target = $"10.0.2.2:{port}";
        var result = await RunAdbAsync($"-s {Serial} shell settings put global http_proxy {target}", 8000);
        SetStatus(result.ok ? $"Android global HTTP proxy set to {target}." : $"ADB proxy set failed: {result.output}");
        AppendRow("adb", "", "global", "http_proxy", result.ok ? "ok" : "fail", result.ok ? target : result.output);
    }

    private async void ClearProxy_Click(object sender, RoutedEventArgs e)
    {
        var first = await RunAdbAsync($"-s {Serial} shell settings put global http_proxy :0", 8000);
        var second = await RunAdbAsync($"-s {Serial} shell settings delete global global_http_proxy_host", 8000);
        var third = await RunAdbAsync($"-s {Serial} shell settings delete global global_http_proxy_port", 8000);
        var ok = first.ok || second.ok || third.ok;
        var output = string.Join(" | ", new[] { first.output, second.output, third.output });
        SetStatus(ok ? "Android global proxy cleared." : $"ADB proxy clear failed: {output}");
        AppendRow("adb", "", "global", "http_proxy", ok ? "cleared" : "fail", output);
    }

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadPort(WebPortBox.Text, out var webPort)) webPort = 8081;
        OpenUrl($"http://127.0.0.1:{webPort}/");
    }

    private void CaHelp_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("http://mitm.it/");
        SetStatus("CA flow: set Android proxy first, open http://mitm.it inside Android, download Android cert, install/name it. Patch apps that ignore user CAs.");
        AppendRow("help", "", "mitm.it", "/", "ca", "Open mitm.it from the proxied emulator to install mitmproxy CA");
    }

    private void ClearRows_Click(object sender, RoutedEventArgs e)
    {
        CaptureRows.Clear();
    }

    private void OnMitmOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Dispatcher.Invoke(() => ParseAndAppendOutput(line.Trim()));
    }

    private void ParseAndAppendOutput(string line)
    {
        // Typical mitmproxy console lines contain METHOD URL and sometimes a trailing status.
        var match = Regex.Match(line, @"\b(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS|CONNECT)\b\s+(?<url>\S+)(?:.*?\s(?<status>\d{3})\b)?", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var method = match.Groups[1].Value.ToUpperInvariant();
            var url = match.Groups["url"].Value;
            var status = match.Groups["status"].Success ? match.Groups["status"].Value : "";
            SplitUrl(url, out var host, out var path);
            AppendRow(method, method, host, path, status, line);
            return;
        }

        AppendRow("log", "", "", "", "", line);
    }

    private static void SplitUrl(string url, out string host, out string path)
    {
        host = "";
        path = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
            path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        }
        else if (url.Contains('/'))
        {
            var idx = url.IndexOf('/');
            host = url[..idx];
            path = url[idx..];
        }
        else
        {
            host = url;
            path = "/";
        }
    }

    private void AppendRow(string kind, string method, string host, string path, string status, string notes)
    {
        CaptureRows.Add(new MitmCaptureRow(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), method, host, path, status, notes));
        while (CaptureRows.Count > 500)
            CaptureRows.RemoveAt(0);
        if (CaptureRows.Count > 0)
            CaptureGrid.ScrollIntoView(CaptureRows[^1]);
    }

    private async Task<(bool ok, string output)> RunAdbAsync(string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(_adbPath, args)
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
            var completed = await Task.WhenAny(wait, Task.Delay(timeoutMs));
            if (completed != wait)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (false, "timeout waiting for adb");
            }

            var output = ((await stdout) + " " + (await stderr)).Trim();
            return (proc.ExitCode == 0, string.IsNullOrWhiteSpace(output) ? $"exit code {proc.ExitCode}" : output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? FindOnPath(string fileName)
    {
        if (Path.IsPathRooted(fileName) && File.Exists(fileName)) return fileName;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in paths)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static bool TryReadPort(string text, out int port)
    {
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is >= 1024 and <= 65535;
    }

    private static string QuoteIfNeeded(string value)
    {
        return value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}

public sealed record MitmCaptureRow(string Timestamp, string Method, string Host, string Path, string Status, string Notes);
