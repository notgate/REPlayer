using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ReVM;

public partial class FridaDialog : Window
{
    private const string DefaultSerial = "emulator-5554";
    private const string RemoteFridaPath = "/data/local/tmp/frida-server";

    private readonly string _adbPath;
    private readonly int _defaultPort;

    public ObservableCollection<FridaProcessItem> Processes { get; } = new();
    public ObservableCollection<FridaScriptTemplate> Scripts { get; } = new();
    public ObservableCollection<FridaLogEvent> Events { get; } = new();

    public FridaDialog() : this("adb", DefaultSerial, 27042)
    {
    }

    public FridaDialog(string adbPath, string serial, int fridaPort)
    {
        InitializeComponent();
        DataContext = this;

        _adbPath = string.IsNullOrWhiteSpace(adbPath) ? "adb" : adbPath;
        _defaultPort = fridaPort is >= 1024 and <= 65535 ? fridaPort : 27042;

        BinaryPath.Text = FindDefaultFridaServerPath();
        SerialBox.Text = string.IsNullOrWhiteSpace(serial) ? DefaultSerial : serial;
        PortBox.Text = _defaultPort.ToString(CultureInfo.InvariantCulture);

        LoadScriptTemplates();
        if (Scripts.Count > 0)
        {
            ScriptsGrid.SelectedIndex = 0;
            ScriptEditor.Text = Scripts[0].Source;
        }

        AddLog("INFO", $"Using ADB: {_adbPath}");
        AddLog("INFO", $"Target serial: {SerialBox.Text}; Frida port: {_defaultPort}");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Frida server|frida-server*|All files|*.*",
            Title = "Select Android x86_64 frida-server binary"
        };
        if (dialog.ShowDialog() == true)
            BinaryPath.Text = dialog.FileName;
    }

    private async void PushStart_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Push + Start", async () =>
        {
            var binary = BinaryPath.Text.Trim();
            var port = ReadPort();
            if (!File.Exists(binary))
            {
                AddLog("ERROR", $"Frida server binary not found: {binary}");
                return;
            }

            AddLog("INFO", "Pushing frida-server to /data/local/tmp ...");
            var push = await RunAdbAsync($"push \"{binary}\" {RemoteFridaPath}", 60_000);
            AddCommandResult("push", push);
            if (!push.Ok) return;

            var chmod = await RunAdbAsync($"shell chmod 755 {RemoteFridaPath}", 10_000);
            AddCommandResult("chmod", chmod);
            if (!chmod.Ok) return;

            await RunAdbAsync("shell pkill -f frida-server", 5_000, allowFailure: true);

            AddLog("INFO", $"Starting frida-server on 0.0.0.0:{port} ...");
            var start = await RunAdbAsync($"shell \"{RemoteFridaPath} -l 0.0.0.0:{port} >/data/local/tmp/frida-server.log 2>&1 &\"", 10_000, allowFailure: true);
            AddCommandResult("start", start);

            await Task.Delay(600);
            var pid = await RunAdbAsync("shell pidof frida-server", 5_000, allowFailure: true);
            if (pid.Ok && !string.IsNullOrWhiteSpace(pid.Output))
                AddLog("OK", $"frida-server is running; pid(s): {pid.Output.Trim()}");
            else
                AddLog("WARN", "Could not confirm frida-server PID. Use Check or inspect /data/local/tmp/frida-server.log.");

            await ForwardPortAsync(port);
        });
    }

    private async void Forward_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Forward", async () => await ForwardPortAsync(ReadPort()));
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Stop", async () =>
        {
            var result = await RunAdbAsync("shell pkill -f frida-server", 5_000, allowFailure: true);
            AddCommandResult("stop", result);
            await RunAdbAsync($"forward --remove tcp:{ReadPort()}", 5_000, allowFailure: true);
            AddLog("OK", "Stopped frida-server if it was running and removed local tcp forward.");
        });
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Check", async () =>
        {
            var devices = await RunAdbAsync("devices", 5_000, includeSerial: false);
            AddCommandResult("devices", devices);
            var pid = await RunAdbAsync("shell pidof frida-server", 5_000, allowFailure: true);
            AddCommandResult("pidof", pid);
            var forward = await RunAdbAsync("forward --list", 5_000, includeSerial: false, allowFailure: true);
            AddCommandResult("forward", forward);
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Refresh", RefreshProcessesAsync);
    }

    private void ProcessesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessesGrid.SelectedItem is FridaProcessItem item)
            TargetBox.Text = item.Kind == "APP" ? item.Name : item.Pid;
    }

    private void ScriptsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScriptsGrid.SelectedItem is FridaScriptTemplate template)
            ScriptEditor.Text = template.Source;
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        await RunFridaClientAsync(spawn: false);
    }

    private async void Spawn_Click(object sender, RoutedEventArgs e)
    {
        await RunFridaClientAsync(spawn: true);
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        var command = BuildFridaCommand(spawn: false, scriptPath: "script.js");
        Clipboard.SetText(command);
        AddLog("OK", $"Copied: {command}");
    }

    private async Task RunFridaClientAsync(bool spawn)
    {
        await RunUiActionAsync(spawn ? "Spawn" : "Attach", async () =>
        {
            var target = TargetBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                AddLog("ERROR", "Select or enter a PID, process name, or package first.");
                return;
            }

            var scriptPath = await WriteCurrentScriptAsync();
            var command = BuildFridaCommand(spawn, scriptPath);
            AddLog("INFO", command);

            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/k {command}") { UseShellExecute = true });
                AddLog("OK", "Opened frida-tools in a terminal. Install with: python -m pip install frida-tools");
            }
            catch (Exception ex)
            {
                AddLog("ERROR", $"Unable to launch frida client: {ex.Message}");
            }
        });
    }

    private async Task RefreshProcessesAsync()
    {
        Processes.Clear();

        var ps = await RunAdbAsync("shell ps -A", 10_000, allowFailure: true);
        if (!ps.Ok)
        {
            ps = await RunAdbAsync("shell ps", 10_000, allowFailure: true);
        }

        if (ps.Ok)
        {
            foreach (var item in ParseProcessList(ps.Output).Take(300))
                Processes.Add(item);
            AddLog("OK", $"Loaded {Processes.Count} running processes.");
        }
        else
        {
            AddCommandResult("ps", ps);
        }

        var apps = await RunAdbAsync("shell pm list packages -3", 10_000, allowFailure: true);
        if (apps.Ok)
        {
            foreach (var package in apps.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(line => line.Trim())
                         .Where(line => line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
                         .Select(line => line[8..])
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Take(300))
            {
                if (!Processes.Any(p => string.Equals(p.Name, package, StringComparison.OrdinalIgnoreCase)))
                    Processes.Add(new FridaProcessItem("", "APP", package));
            }
            AddLog("OK", "Added third-party package list for spawn targets.");
        }
    }

    private async Task ForwardPortAsync(int port)
    {
        var result = await RunAdbAsync($"forward tcp:{port} tcp:{port}", 10_000);
        AddCommandResult("forward", result);
        if (result.Ok)
            AddLog("OK", $"Forwarded host tcp:{port} -> device tcp:{port}. frida-tools target: -H 127.0.0.1:{port}");
    }

    private async Task<string> WriteCurrentScriptAsync()
    {
        var dir = Path.Combine(RuntimeBackendFactory.GetBaseDir(), "runtime", "frida", "scripts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"replayer-frida-{DateTime.Now:yyyyMMdd-HHmmss}.js");
        await File.WriteAllTextAsync(path, ScriptEditor.Text, Encoding.UTF8);
        AddLog("OK", $"Script saved: {path}");
        return path;
    }

    private string BuildFridaCommand(bool spawn, string scriptPath)
    {
        var port = ReadPort();
        var target = TargetBox.Text.Trim();
        var mode = spawn ? "-f" : "-n";
        if (!spawn && int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            mode = "-p";
        return $"frida -H 127.0.0.1:{port} {mode} \"{target}\" -l \"{scriptPath}\"" + (spawn ? " --no-pause" : string.Empty);
    }

    private async Task RunUiActionAsync(string action, Func<Task> operation)
    {
        try
        {
            StatusText.Text = $"  {action.ToLowerInvariant()}...";
            await operation();
            StatusText.Text = "  idle";
        }
        catch (Exception ex)
        {
            StatusText.Text = "  error";
            AddLog("ERROR", ex.Message);
        }
    }

    private async Task<FridaAdbResult> RunAdbAsync(string args, int timeoutMs, bool includeSerial = true, bool allowFailure = false)
    {
        var fullArgs = includeSerial ? $"-s {SerialBox.Text.Trim()} {args}" : args;
        var psi = new ProcessStartInfo(_adbPath, fullArgs)
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
            return new FridaAdbResult(false, fullArgs, "timeout");
        }

        var output = string.Join(Environment.NewLine, new[] { await stdout, await stderr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        var ok = proc.ExitCode == 0 || allowFailure;
        return new FridaAdbResult(ok, fullArgs, string.IsNullOrWhiteSpace(output) ? $"exit {proc.ExitCode}" : output);
    }

    private void AddCommandResult(string label, FridaAdbResult result)
    {
        var level = result.Ok ? "OK" : "ERROR";
        var output = result.Output.Replace("\r", " ").Replace("\n", " ");
        if (output.Length > 420) output = output[..420] + "...";
        AddLog(level, $"adb {label}: {output}");
    }

    private void AddLog(string level, string message)
    {
        Events.Insert(0, new FridaLogEvent(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), level, message));
        while (Events.Count > 500)
            Events.RemoveAt(Events.Count - 1);
    }

    private int ReadPort()
    {
        if (int.TryParse(PortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 1024 and <= 65535)
            return port;
        PortBox.Text = _defaultPort.ToString(CultureInfo.InvariantCulture);
        AddLog("WARN", $"Invalid port; reverted to {_defaultPort}.");
        return _defaultPort;
    }

    private static string FindDefaultFridaServerPath()
    {
        var root = RuntimeBackendFactory.GetBaseDir();
        var candidates = new[]
        {
            Path.Combine(root, "runtime", "frida-server-x86_64"),
            Path.Combine(root, "runtime", "frida-server"),
            Path.Combine(root, "runtime", "tools", "frida-server-x86_64"),
            Path.Combine(root, "frida-server-x86_64"),
            "runtime/frida-server-x86_64"
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static FridaProcessItem[] ParseProcessList(string output)
    {
        return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => new FridaProcessItem(parts[1], "PROC", parts[^1]))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && item.Name != "ps")
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void LoadScriptTemplates()
    {
        Scripts.Add(new FridaScriptTemplate("Java method trace", """
Java.perform(function () {
  const className = 'com.example.TargetClass';
  const methodName = 'targetMethod';
  const Target = Java.use(className);
  Target[methodName].overloads.forEach(function (overload) {
    overload.implementation = function () {
      console.log('[+] ' + className + '.' + methodName + '(' + Array.prototype.join.call(arguments, ', ') + ')');
      const ret = overload.apply(this, arguments);
      console.log('[+] return => ' + ret);
      return ret;
    };
  });
});
"""));
        Scripts.Add(new FridaScriptTemplate("SSL pinning diagnostics", """
Java.perform(function () {
  function hook(name, method) {
    try {
      const C = Java.use(name);
      C[method].overloads.forEach(function (overload) {
        overload.implementation = function () {
          console.log('[TLS] ' + name + '.' + method + ' called');
          return overload.apply(this, arguments);
        };
      });
      console.log('[TLS] hooked ' + name + '.' + method);
    } catch (e) {
      console.log('[TLS] missing ' + name + ': ' + e);
    }
  }
  hook('javax.net.ssl.X509TrustManager', 'checkServerTrusted');
  hook('okhttp3.CertificatePinner', 'check');
  hook('com.android.org.conscrypt.TrustManagerImpl', 'verifyChain');
});
"""));
        Scripts.Add(new FridaScriptTemplate("Android crypto monitor", """
Java.perform(function () {
  const Cipher = Java.use('javax.crypto.Cipher');
  Cipher.getInstance.overload('java.lang.String').implementation = function (transformation) {
    console.log('[crypto] Cipher.getInstance(' + transformation + ')');
    return this.getInstance(transformation);
  };
  const MessageDigest = Java.use('java.security.MessageDigest');
  MessageDigest.getInstance.overload('java.lang.String').implementation = function (algorithm) {
    console.log('[crypto] MessageDigest.getInstance(' + algorithm + ')');
    return this.getInstance(algorithm);
  };
});
"""));
    }
}

public sealed record FridaProcessItem(string Pid, string Kind, string Name);
public sealed record FridaScriptTemplate(string Name, string Source);
public sealed record FridaLogEvent(string Time, string Level, string Message);
internal sealed record FridaAdbResult(bool Ok, string Command, string Output);
