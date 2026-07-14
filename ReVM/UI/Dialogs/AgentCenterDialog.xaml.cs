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
    private readonly AiAgentTaskRunner _taskRunner;
    private readonly AiAgentProfileStore _profileStore;
    private readonly AiAgentTaskStore _taskStore;
    private readonly IAiAgentProviderClientFactory _providerFactory;
    private readonly string _deviceSerial;
    private readonly string _inboxDirectory;
    private int _agentSequence = 1;
    private AgentInboxWindow? _inboxWindow;
    private AgentEvidenceWindow? _evidenceWindow;

    public AgentCenterDialog(
        AndroidAgentCoordinator coordinator,
        AiAgentTaskRunner taskRunner,
        AiAgentProfileStore profileStore,
        AiAgentTaskStore taskStore,
        IAiAgentProviderClientFactory providerFactory,
        string deviceSerial,
        string inboxDirectory)
    {
        InitializeComponent();
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _taskRunner = taskRunner ?? throw new ArgumentNullException(nameof(taskRunner));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _deviceSerial = string.IsNullOrWhiteSpace(deviceSerial) ? throw new ArgumentException("Device serial is required.", nameof(deviceSerial)) : deviceSerial;
        _inboxDirectory = Path.GetFullPath(inboxDirectory ?? throw new ArgumentNullException(nameof(inboxDirectory)));
        DeviceText.Text = "DEVICE  " + _deviceSerial;
        RefreshProfiles();
        LoadTaskHistory();
    }

    private void Queue_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AgentProfileCombo.SelectedItem is not AiAgentProfile profile)
                throw new InvalidOperationException("Set up and select an AI agent first.");
            var prompt = TaskPromptBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("Describe the task the agent must complete.");
            if (!int.TryParse(TimeoutBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 600)
                throw new InvalidOperationException("Timeout must be between 1 and 600 seconds.");
            if (!int.TryParse(MaximumTurnsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximumTurns) || maximumTurns is < 1 or > 64)
                throw new InvalidOperationException("Maximum turns must be between 1 and 64.");
            var access = AccessCombo.SelectedItem is ComboBoxItem item && string.Equals(item.Tag?.ToString(), "DeviceControl", StringComparison.Ordinal)
                ? AndroidAgentAccess.DeviceControl
                : AndroidAgentAccess.Observe;
            var taskId = $"{profile.Id}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{_agentSequence}";
            var request = new AiAgentTaskRequest
            {
                TaskId = taskId,
                AgentId = profile.Id,
                DeviceSerial = _deviceSerial,
                Prompt = prompt,
                AppPackage = string.IsNullOrWhiteSpace(PackageBox.Text) ? null : PackageBox.Text.Trim(),
                Access = access,
                Timeout = TimeSpan.FromSeconds(seconds),
                MaximumTurns = maximumTurns,
            };
            var row = new AgentRunRow(taskId, profile.Name, profile.ProviderDisplayName, Summarize(prompt), new CancellationTokenSource());
            RunList.Items.Add(row);
            RunList.SelectedItem = row;
            StatusText.Text = $"Queued {profile.Name} on {_deviceSerial}";
            _agentSequence++;
            TaskPromptBox.Clear();
            _ = ExecuteAsync(row, request, profile);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async Task ExecuteAsync(AgentRunRow row, AiAgentTaskRequest request, AiAgentProfile profile)
    {
        row.Status = AiAgentTaskStatus.Running.ToString();
        row.Started = DateTimeOffset.UtcNow;
        if (RunList.SelectedItem == row) CancelSelectedButton.IsEnabled = true;
        try
        {
            var result = await _taskRunner.RunAsync(request, profile, row.Cancellation.Token);
            row.Status = result.Status.ToString();
            row.EvidencePath = result.EvidencePath;
            row.Duration = (result.EndedAtUtc - result.StartedAtUtc).TotalSeconds.ToString("0.0 s", CultureInfo.InvariantCulture);
            row.Summary = Summarize(result.Error ?? result.Report ?? result.Status.ToString());
            row.Output = RenderOutput(result);
            StatusText.Text = $"{row.AgentId}: {row.Status}";
            if (RunList.SelectedItem == row) OutputBox.Text = row.Output;
            _inboxWindow?.RefreshTasks();
            _evidenceWindow?.RefreshEvidence(row.TaskId);
        }
        catch (Exception ex)
        {
            row.Status = AiAgentTaskStatus.Failed.ToString();
            row.Summary = ex.Message;
            row.Output = ex.ToString();
            StatusText.Text = ex.Message;
            if (RunList.SelectedItem == row) OutputBox.Text = row.Output;
        }
        finally
        {
            if (RunList.SelectedItem == row) CancelSelectedButton.IsEnabled = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (RunList.SelectedItem is not AgentRunRow row) return;
        if (row.Status is nameof(AiAgentTaskStatus.Pending) or nameof(AiAgentTaskStatus.Running))
        {
            row.Cancellation.Cancel();
            StatusText.Text = "Cancellation requested for " + row.AgentId;
        }
    }

    private void OpenEvidence_Click(object sender, RoutedEventArgs e)
    {
        var taskId = (RunList.SelectedItem as AgentRunRow)?.TaskId;
        if (_evidenceWindow is null)
        {
            var window = new AgentEvidenceWindow(_taskStore, taskId) { Owner = this };
            window.Closed += (_, _) => { if (ReferenceEquals(_evidenceWindow, window)) _evidenceWindow = null; };
            _evidenceWindow = window;
            window.Show();
        }
        else
        {
            _evidenceWindow.RefreshEvidence(taskId);
            if (_evidenceWindow.WindowState == WindowState.Minimized) _evidenceWindow.WindowState = WindowState.Normal;
            _evidenceWindow.Activate();
        }
        StatusText.Text = taskId is null ? "Evidence browser opened" : "Evidence: " + taskId;
    }

    private void OpenInbox_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_inboxDirectory);
        if (_inboxWindow is null)
        {
            var window = new AgentInboxWindow(_taskStore, _inboxDirectory) { Owner = this };
            window.Closed += (_, _) => { if (ReferenceEquals(_inboxWindow, window)) _inboxWindow = null; };
            _inboxWindow = window;
            window.Show();
        }
        else
        {
            _inboxWindow.RefreshTasks();
            if (_inboxWindow.WindowState == WindowState.Minimized) _inboxWindow.WindowState = WindowState.Normal;
            _inboxWindow.Activate();
        }
        StatusText.Text = "Agent inbox opened";
    }

    private void ConfigureAgent_Click(object sender, RoutedEventArgs e)
    {
        var selectedId = (AgentProfileCombo.SelectedItem as AiAgentProfile)?.Id;
        var dialog = new AgentSetupDialog(_profileStore, _providerFactory, selectedId) { Owner = this };
        dialog.ShowDialog();
        RefreshProfiles(dialog.SelectedAgentId ?? selectedId);
    }

    private void AgentProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentProfileCombo.SelectedItem is not AiAgentProfile profile)
        {
            ProviderText.Text = "No agent configured";
            QueueTaskButton.IsEnabled = false;
            return;
        }
        ProviderText.Text = $"{profile.ProviderDisplayName}  {profile.Model}";
        MaximumTurnsBox.Text = profile.MaximumTurns.ToString(CultureInfo.InvariantCulture);
        QueueTaskButton.IsEnabled = _profileStore.HasCredential(profile.Id);
        if (!QueueTaskButton.IsEnabled) ProviderText.Text += "  ·  key required";
    }

    private void RefreshProfiles(string? selectedId = null)
    {
        selectedId ??= (AgentProfileCombo.SelectedItem as AiAgentProfile)?.Id;
        var profiles = _profileStore.Load();
        AgentProfileCombo.ItemsSource = profiles;
        AgentProfileCombo.SelectedItem = profiles.FirstOrDefault(profile => string.Equals(profile.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? profiles.FirstOrDefault();
        if (profiles.Count == 0)
        {
            ProviderText.Text = "No agent configured";
            QueueTaskButton.IsEnabled = false;
        }
    }

    private void LoadTaskHistory()
    {
        foreach (var snapshot in _taskStore.RecoverInterrupted())
            RunList.Items.Add(AgentRunRow.FromSnapshot(snapshot));
        RunList.SelectedItem = RunList.Items.OfType<AgentRunRow>().FirstOrDefault();
    }

    private void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = RunList.SelectedItem as AgentRunRow;
        OutputBox.Text = row?.Output ?? string.Empty;
        CancelSelectedButton.IsEnabled = row?.Status is nameof(AiAgentTaskStatus.Pending) or nameof(AiAgentTaskStatus.Running);
    }

    public void CancelActiveTasks()
    {
        foreach (var row in RunList.Items.OfType<AgentRunRow>().Where(row =>
                     row.Status is nameof(AiAgentTaskStatus.Pending) or nameof(AiAgentTaskStatus.Running)))
            row.Cancellation.Cancel();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string Summarize(string? value)
    {
        var summary = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return summary.Length <= 96 ? summary : summary[..93] + "...";
    }

    private static string RenderOutput(AiAgentTaskResult result)
    {
        var output = new StringBuilder();
        output.AppendLine($"task     {result.TaskId}");
        output.AppendLine($"status   {result.Status}");
        output.AppendLine($"duration {(result.EndedAtUtc - result.StartedAtUtc).TotalSeconds:0.0} s");
        output.AppendLine($"tools    {result.ToolRuns.Count}");
        output.AppendLine($"evidence {result.EvidencePath}");
        foreach (var run in result.ToolRuns)
        {
            output.AppendLine().AppendLine($"> ADB run {run.RunId} [{run.Status}]");
            foreach (var step in run.Steps)
            {
                output.AppendLine("  " + string.Join(' ', step.Arguments));
                if (!string.IsNullOrWhiteSpace(step.Command.StandardOutput)) output.AppendLine(step.Command.StandardOutput.TrimEnd());
                if (!string.IsNullOrWhiteSpace(step.Command.StandardError)) output.AppendLine("stderr: " + step.Command.StandardError.TrimEnd());
            }
        }
        if (!string.IsNullOrWhiteSpace(result.Report)) output.AppendLine().AppendLine("REPORT").AppendLine(result.Report);
        if (!string.IsNullOrWhiteSpace(result.Error)) output.AppendLine().AppendLine("ERROR").AppendLine(result.Error);
        return output.ToString();
    }

    private sealed class AgentRunRow : INotifyPropertyChanged
    {
        private string _status = AiAgentTaskStatus.Pending.ToString();
        private string _duration = "—";
        private string _summary = "pending";

        public AgentRunRow(string taskId, string agentId, string provider, string step, CancellationTokenSource cancellation)
        {
            TaskId = taskId;
            AgentId = agentId;
            Provider = provider;
            Step = step;
            Cancellation = cancellation;
        }

        public string TaskId { get; }
        public string AgentId { get; }
        public string Provider { get; }
        public string Step { get; }
        public CancellationTokenSource Cancellation { get; }
        public DateTimeOffset Started { get; set; }
        public string EvidencePath { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
        public string Status { get => _status; set => Set(ref _status, value); }
        public string Duration { get => _duration; set => Set(ref _duration, value); }
        public string Summary { get => _summary; set => Set(ref _summary, value); }

        public static AgentRunRow FromSnapshot(AiAgentTaskSnapshot snapshot)
        {
            var row = new AgentRunRow(snapshot.TaskId, snapshot.AgentName, snapshot.Provider, Summarize(snapshot.Prompt), new CancellationTokenSource())
            {
                Status = snapshot.Status.ToString(),
                Started = snapshot.StartedAtUtc ?? snapshot.CreatedAtUtc,
                EvidencePath = snapshot.EvidencePath,
                Summary = Summarize(snapshot.Error ?? snapshot.Report ?? snapshot.Status.ToString()),
                Duration = snapshot.EndedAtUtc is DateTimeOffset ended
                    ? (ended - (snapshot.StartedAtUtc ?? snapshot.CreatedAtUtc)).TotalSeconds.ToString("0.0 s", CultureInfo.InvariantCulture)
                    : "—",
                Output = RenderSnapshot(snapshot),
            };
            return row;
        }

        private static string RenderSnapshot(AiAgentTaskSnapshot snapshot)
        {
            var output = new StringBuilder();
            output.AppendLine($"task     {snapshot.TaskId}");
            output.AppendLine($"agent    {snapshot.AgentName}");
            output.AppendLine($"provider {snapshot.Provider} / {snapshot.Model}");
            output.AppendLine($"device   {snapshot.DeviceSerial}");
            output.AppendLine($"status   {snapshot.Status}");
            output.AppendLine($"tools    {snapshot.ToolRunCount}");
            output.AppendLine($"evidence {snapshot.EvidencePath}");
            output.AppendLine().AppendLine("TASK").AppendLine(snapshot.Prompt);
            if (!string.IsNullOrWhiteSpace(snapshot.Report)) output.AppendLine().AppendLine("REPORT").AppendLine(snapshot.Report);
            if (!string.IsNullOrWhiteSpace(snapshot.Error)) output.AppendLine().AppendLine("ERROR").AppendLine(snapshot.Error);
            return output.ToString();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set(ref string field, string value, [CallerMemberName] string? propertyName = null)
        {
            if (string.Equals(field, value, StringComparison.Ordinal)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
