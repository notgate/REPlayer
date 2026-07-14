using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ReVM.Automation;

namespace ReVM;

public partial class AgentInboxWindow : Window
{
    private readonly AiAgentTaskStore _store;
    private readonly string _externalInboxDirectory;

    public AgentInboxWindow(AiAgentTaskStore store, string externalInboxDirectory)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _externalInboxDirectory = Path.GetFullPath(externalInboxDirectory ?? throw new ArgumentNullException(nameof(externalInboxDirectory)));
        RefreshTasks();
    }

    public void RefreshTasks()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshTasks);
            return;
        }
        var selectedId = (TaskList.SelectedItem as InboxRow)?.Snapshot.TaskId;
        var filter = FilterCombo.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? "All" : "All";
        var tasks = _store.Load();
        if (!string.Equals(filter, "All", StringComparison.OrdinalIgnoreCase))
            tasks = tasks.Where(task => string.Equals(task.Status.ToString(), filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        var rows = tasks.Select(snapshot => new InboxRow(snapshot)).ToArray();
        TaskList.ItemsSource = rows;
        TaskList.SelectedItem = rows.FirstOrDefault(row => string.Equals(row.Snapshot.TaskId, selectedId, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
        var pendingFiles = CountFiles(_externalInboxDirectory, "*.json") + CountFiles(_externalInboxDirectory, "*.cancel");
        var outbox = Path.Combine(Path.GetDirectoryName(_externalInboxDirectory) ?? _externalInboxDirectory, "outbox");
        HarnessText.Text = $"TASKS {tasks.Count}    FILE INBOX {pendingFiles}    FILE RESULTS {CountFiles(outbox, "*.result.json")}";
        StatusText.Text = tasks.Count == 0 ? "No tasks match this filter" : $"Showing {tasks.Count} task{(tasks.Count == 1 ? string.Empty : "s")}";
        UpdateReport();
    }

    private static int CountFiles(string directory, string pattern)
    {
        try { return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Count() : 0; }
        catch { return 0; }
    }

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) RefreshTasks();
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateReport();

    private void UpdateReport()
    {
        if (TaskList.SelectedItem is not InboxRow row)
        {
            ReportBox.Text = string.Empty;
            return;
        }
        var task = row.Snapshot;
        var output = new StringBuilder();
        output.AppendLine($"task      {task.TaskId}");
        output.AppendLine($"agent     {task.AgentName}");
        output.AppendLine($"provider  {task.Provider} / {task.Model}");
        output.AppendLine($"device    {task.DeviceSerial}");
        output.AppendLine($"status    {task.Status}");
        output.AppendLine($"tool runs {task.ToolRunCount}");
        output.AppendLine($"evidence  {task.EvidencePath}");
        output.AppendLine().AppendLine("TASK").AppendLine(task.Prompt);
        if (!string.IsNullOrWhiteSpace(task.Report)) output.AppendLine().AppendLine("REPORT").AppendLine(task.Report);
        if (!string.IsNullOrWhiteSpace(task.Error)) output.AppendLine().AppendLine("ERROR").AppendLine(task.Error);
        ReportBox.Text = output.ToString();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshTasks();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class InboxRow
    {
        public InboxRow(AiAgentTaskSnapshot snapshot)
        {
            Snapshot = snapshot;
            PromptSummary = Summarize(snapshot.Prompt);
            Created = snapshot.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        public AiAgentTaskSnapshot Snapshot { get; }
        public string Status => Snapshot.Status.ToString();
        public string AgentName => Snapshot.AgentName;
        public string Provider => Snapshot.Provider;
        public string Created { get; }
        public string PromptSummary { get; }

        private static string Summarize(string value)
        {
            var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return compact.Length <= 120 ? compact : compact[..117] + "...";
        }
    }
}
