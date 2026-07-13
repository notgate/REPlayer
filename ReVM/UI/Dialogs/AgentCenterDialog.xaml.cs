using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ReVM.Automation;

namespace ReVM;

public partial class AgentCenterDialog : Window
{
    private readonly AndroidAgentCoordinator _coordinator;
    private readonly string _deviceSerial;
    private readonly string _inboxDirectory;
    private int _agentSequence = 1;

    public AgentCenterDialog(AndroidAgentCoordinator coordinator, string deviceSerial, string inboxDirectory)
    {
        InitializeComponent();
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _deviceSerial = string.IsNullOrWhiteSpace(deviceSerial) ? throw new ArgumentException("Device serial is required.", nameof(deviceSerial)) : deviceSerial;
        _inboxDirectory = Path.GetFullPath(inboxDirectory ?? throw new ArgumentNullException(nameof(inboxDirectory)));
        DeviceText.Text = "DEVICE  " + _deviceSerial;
    }

    private void Queue_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var arguments = ParseArguments(CommandBox.Text);
            if (arguments.Count == 0) throw new InvalidOperationException("Enter at least one ADB argument.");
            if (!int.TryParse(TimeoutBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 600)
                throw new InvalidOperationException("Timeout must be between 1 and 600 seconds.");
            var access = AccessCombo.SelectedItem is ComboBoxItem item && string.Equals(item.Tag?.ToString(), "DeviceControl", StringComparison.Ordinal)
                ? AndroidAgentAccess.DeviceControl
                : AndroidAgentAccess.Observe;
            var agentId = AgentIdBox.Text.Trim();
            var plan = new AndroidAgentPlan
            {
                AgentId = agentId,
                DeviceSerial = _deviceSerial,
                AppPackage = string.IsNullOrWhiteSpace(PackageBox.Text) ? null : PackageBox.Text.Trim(),
                Steps = new[]
                {
                    new AndroidAgentStep
                    {
                        Name = BuildStepName(arguments),
                        Arguments = arguments,
                        Access = access,
                        Timeout = TimeSpan.FromSeconds(seconds),
                    },
                },
            };
            var row = new AgentRunRow(agentId, access.ToString(), plan.Steps[0].Name, new CancellationTokenSource());
            RunList.Items.Add(row);
            RunList.SelectedItem = row;
            StatusText.Text = $"Queued {agentId} on {_deviceSerial}";
            _agentSequence++;
            AgentIdBox.Text = "agent-" + _agentSequence.ToString(CultureInfo.InvariantCulture);
            _ = ExecuteAsync(row, plan);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async Task ExecuteAsync(AgentRunRow row, AndroidAgentPlan plan)
    {
        row.Status = AndroidAgentRunStatus.Running.ToString();
        row.Started = DateTimeOffset.UtcNow;
        try
        {
            var result = await _coordinator.RunAsync(plan, row.Cancellation.Token);
            row.Status = result.Status.ToString();
            row.EvidencePath = result.EvidencePath;
            row.Duration = (result.EndedAtUtc - result.StartedAtUtc).TotalSeconds.ToString("0.0 s", CultureInfo.InvariantCulture);
            row.Summary = result.Error ?? (result.Steps.LastOrDefault()?.Command.ExitCode is int exitCode ? "exit " + exitCode : result.Status.ToString());
            row.Output = RenderOutput(result);
            StatusText.Text = $"{row.AgentId}: {row.Status}";
            if (RunList.SelectedItem == row) OutputBox.Text = row.Output;
        }
        catch (Exception ex)
        {
            row.Status = AndroidAgentRunStatus.Failed.ToString();
            row.Summary = ex.Message;
            row.Output = ex.ToString();
            StatusText.Text = ex.Message;
            if (RunList.SelectedItem == row) OutputBox.Text = row.Output;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (RunList.SelectedItem is not AgentRunRow row) return;
        if (row.Status is nameof(AndroidAgentRunStatus.Queued) or nameof(AndroidAgentRunStatus.Running))
        {
            row.Cancellation.Cancel();
            StatusText.Text = "Cancellation requested for " + row.AgentId;
        }
    }

    private void OpenEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (RunList.SelectedItem is not AgentRunRow row || string.IsNullOrWhiteSpace(row.EvidencePath) || !File.Exists(row.EvidencePath))
        {
            StatusText.Text = "Select a completed run with evidence";
            return;
        }
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add("/select," + row.EvidencePath);
        Process.Start(start);
    }

    private void OpenInbox_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_inboxDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _inboxDirectory) { UseShellExecute = true });
        StatusText.Text = "Agent inbox: " + _inboxDirectory;
    }

    private void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OutputBox.Text = RunList.SelectedItem is AgentRunRow row ? row.Output : string.Empty;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal static IReadOnlyList<string> ParseArguments(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var escaping = false;
        foreach (var character in commandLine ?? string.Empty)
        {
            if (escaping)
            {
                current.Append(character);
                escaping = false;
                continue;
            }
            if (character == '\\')
            {
                escaping = true;
                continue;
            }
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (escaping) current.Append('\\');
        if (quoted) throw new InvalidOperationException("ADB arguments contain an unclosed quote.");
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static string BuildStepName(IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.Take(3)).Trim() is { Length: > 0 } name ? name : "ADB step";

    private static string RenderOutput(AndroidAgentRunResult result)
    {
        var output = new StringBuilder();
        output.AppendLine($"run      {result.RunId}");
        output.AppendLine($"agent    {result.AgentId}");
        output.AppendLine($"device   {result.DeviceSerial}");
        output.AppendLine($"status   {result.Status}");
        output.AppendLine($"evidence {result.EvidencePath}");
        foreach (var step in result.Steps)
        {
            output.AppendLine().AppendLine("> " + string.Join(' ', step.Arguments));
            if (!string.IsNullOrWhiteSpace(step.Command.StandardOutput)) output.AppendLine(step.Command.StandardOutput.TrimEnd());
            if (!string.IsNullOrWhiteSpace(step.Command.StandardError)) output.AppendLine("stderr: " + step.Command.StandardError.TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(result.Error)) output.AppendLine().AppendLine(result.Error);
        return output.ToString();
    }

    private sealed class AgentRunRow : INotifyPropertyChanged
    {
        private string _status = AndroidAgentRunStatus.Queued.ToString();
        private string _duration = "—";
        private string _summary = "queued";

        public AgentRunRow(string agentId, string access, string step, CancellationTokenSource cancellation)
        {
            AgentId = agentId;
            Access = access;
            Step = step;
            Cancellation = cancellation;
        }

        public string AgentId { get; }
        public string Access { get; }
        public string Step { get; }
        public CancellationTokenSource Cancellation { get; }
        public DateTimeOffset Started { get; set; }
        public string EvidencePath { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
        public string Status { get => _status; set => Set(ref _status, value); }
        public string Duration { get => _duration; set => Set(ref _duration, value); }
        public string Summary { get => _summary; set => Set(ref _summary, value); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set(ref string field, string value, [CallerMemberName] string? propertyName = null)
        {
            if (string.Equals(field, value, StringComparison.Ordinal)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
