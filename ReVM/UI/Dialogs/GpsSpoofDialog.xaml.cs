using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;

namespace ReVM;

public partial class GpsSpoofDialog : Window
{
    private readonly string _adbPath;
    private readonly string _serial;

    public GpsSpoofDialog() : this("adb", "emulator-5554")
    {
    }

    public GpsSpoofDialog(string adbPath, string serial)
    {
        InitializeComponent();
        _adbPath = string.IsNullOrWhiteSpace(adbPath) ? "adb" : adbPath;
        _serial = string.IsNullOrWhiteSpace(serial) ? "emulator-5554" : serial;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Set_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var latText = LatBox.Text.Trim();
            var lonText = LonBox.Text.Trim();

            if (!double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(lonText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ||
                lat < -90 || lat > 90 || lon < -180 || lon > 180)
            {
                ResultText.Text = "Invalid coordinates. Latitude must be -90..90 and longitude -180..180.";
                return;
            }

            var lonArg = lon.ToString("R", CultureInfo.InvariantCulture);
            var latArg = lat.ToString("R", CultureInfo.InvariantCulture);
            var result = await RunAdbAsync($"-s {_serial} emu geo fix {lonArg} {latArg}", 5000);
            ResultText.Text = result.ok
                ? $"Location set: {latArg}, {lonArg}"
                : $"GPS command failed: {result.output}";
        }
        catch (Exception ex)
        {
            ResultText.Text = $"Error: {ex.Message}";
        }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await RunAdbAsync($"-s {_serial} emu geo fix 0 0", 5000);
            ResultText.Text = result.ok ? "GPS reset to 0, 0" : $"GPS reset failed: {result.output}";
        }
        catch (Exception ex)
        {
            ResultText.Text = $"Error: {ex.Message}";
        }
    }

    private async Task<(bool ok, string output)> RunAdbAsync(string args, int timeoutMs)
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
            return (false, "timeout waiting for emulator console");
        }

        var output = ((await stdout) + " " + (await stderr)).Trim();
        return (proc.ExitCode == 0, string.IsNullOrWhiteSpace(output) ? $"exit code {proc.ExitCode}" : output);
    }
}
