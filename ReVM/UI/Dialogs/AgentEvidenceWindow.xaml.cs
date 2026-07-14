using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ReVM.Automation;

namespace ReVM;

public partial class AgentEvidenceWindow : Window
{
    private readonly AiAgentTaskStore _store;
    private readonly JsonSerializerOptions _prettyJson = new() { WriteIndented = true };

    public AgentEvidenceWindow(AiAgentTaskStore store, string? selectedTaskId = null)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        RefreshEvidence(selectedTaskId);
    }

    public void RefreshEvidence(string? selectedTaskId = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => RefreshEvidence(selectedTaskId));
            return;
        }
        selectedTaskId ??= (TaskList.SelectedItem as EvidenceTaskRow)?.Snapshot.TaskId;
        var rows = _store.Load()
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.EvidencePath))
            .Select(snapshot => new EvidenceTaskRow(snapshot))
            .ToArray();
        TaskList.ItemsSource = rows;
        TaskList.SelectedItem = rows.FirstOrDefault(row => string.Equals(row.Snapshot.TaskId, selectedTaskId, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
        if (rows.Length == 0)
        {
            EventList.ItemsSource = null;
            DetailBox.Clear();
            StatusText.Text = "No agent evidence has been recorded";
        }
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskList.SelectedItem is not EvidenceTaskRow row)
        {
            EventList.ItemsSource = null;
            DetailBox.Clear();
            return;
        }
        var events = ReadEvents(row.Snapshot.EvidencePath);
        EventList.ItemsSource = events;
        EventList.SelectedItem = events.FirstOrDefault();
        StatusText.Text = File.Exists(row.Snapshot.EvidencePath)
            ? $"{events.Count} event{(events.Count == 1 ? string.Empty : "s")} · {row.Snapshot.EvidencePath}"
            : "Evidence file is not available: " + row.Snapshot.EvidencePath;
    }

    private IReadOnlyList<EvidenceEventRow> ReadEvents(string path)
    {
        var rows = new List<EvidenceEventRow>();
        if (!File.Exists(path)) return rows;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() ?? "event" : "event";
                var timestamp = root.TryGetProperty("timestampUtc", out var timeNode) && timeNode.TryGetDateTimeOffset(out var parsed)
                    ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")
                    : "—";
                rows.Add(new EvidenceEventRow(timestamp, type, Summarize(root), JsonSerializer.Serialize(root, _prettyJson)));
            }
            catch (JsonException ex)
            {
                rows.Add(new EvidenceEventRow("—", "invalid-json", ex.Message, line));
            }
        }
        return rows;
    }

    private static string Summarize(JsonElement root)
    {
        foreach (var property in new[] { "error", "report", "message", "status", "name" })
        {
            if (root.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString() ?? string.Empty;
                var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return compact.Length <= 140 ? compact : compact[..137] + "...";
            }
        }
        if (root.TryGetProperty("toolCallCount", out var count)) return "tool calls: " + count;
        if (root.TryGetProperty("arguments", out var arguments)) return arguments.GetRawText();
        return string.Empty;
    }

    private void EventList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailBox.Text = EventList.SelectedItem is EvidenceEventRow row ? row.Raw : string.Empty;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshEvidence();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class EvidenceTaskRow
    {
        public EvidenceTaskRow(AiAgentTaskSnapshot snapshot)
        {
            Snapshot = snapshot;
            Display = $"{snapshot.Status,-10}  {snapshot.AgentName}\n{snapshot.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }
        public AiAgentTaskSnapshot Snapshot { get; }
        public string Display { get; }
    }

    private sealed record EvidenceEventRow(string Time, string Event, string Detail, string Raw);
}
