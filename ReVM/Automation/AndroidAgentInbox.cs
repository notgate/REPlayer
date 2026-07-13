using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReVM.Automation;

public sealed partial class AndroidAgentInbox : IAsyncDisposable
{
    private readonly AndroidAgentCoordinator _coordinator;
    private readonly string _deviceSerial;
    private readonly string _processingDirectory;
    private readonly string _archiveDirectory;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _loop;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public AndroidAgentInbox(AndroidAgentCoordinator coordinator, string rootDirectory, string deviceSerial)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _deviceSerial = string.IsNullOrWhiteSpace(deviceSerial) ? throw new ArgumentException("Device serial is required.", nameof(deviceSerial)) : deviceSerial;
        RootDirectory = Path.GetFullPath(rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory)));
        InboxDirectory = Path.Combine(RootDirectory, "inbox");
        OutboxDirectory = Path.Combine(RootDirectory, "outbox");
        _processingDirectory = Path.Combine(RootDirectory, "processing");
        _archiveDirectory = Path.Combine(RootDirectory, "archive");
        foreach (var directory in new[] { InboxDirectory, OutboxDirectory, _processingDirectory, _archiveDirectory })
            Directory.CreateDirectory(directory);
        _json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        _loop = Task.Run(ProcessLoopAsync);
    }

    public string RootDirectory { get; }
    public string InboxDirectory { get; }
    public string OutboxDirectory { get; }

    private async Task ProcessLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            ProcessCancellationFiles();
            ClaimPlanFiles();
            try { await Task.Delay(500, _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ProcessCancellationFiles()
    {
        foreach (var path in Directory.EnumerateFiles(InboxDirectory, "*.cancel", SearchOption.TopDirectoryOnly))
        {
            var requestId = Path.GetFileNameWithoutExtension(path);
            if (RequestIdPattern().IsMatch(requestId))
            {
                if (_active.TryGetValue(requestId, out var cancellation)) cancellation.Cancel();
                else _pendingCancellations[requestId] = 0;
            }
            TryDelete(path);
        }
    }

    private void ClaimPlanFiles()
    {
        foreach (var path in Directory.EnumerateFiles(InboxDirectory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
        {
            var claimed = Path.Combine(_processingDirectory, Guid.NewGuid().ToString("N") + ".json");
            try { File.Move(path, claimed); }
            catch (IOException) { continue; }
            var taskId = Path.GetFileNameWithoutExtension(claimed);
            var task = ProcessPlanAsync(claimed);
            _tasks[taskId] = task;
            _ = task.ContinueWith(completed => _tasks.TryRemove(taskId, out _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ProcessPlanAsync(string claimedPath)
    {
        string requestId = Path.GetFileNameWithoutExtension(claimedPath);
        CancellationTokenSource? cancellation = null;
        try
        {
            await using var stream = File.OpenRead(claimedPath);
            var request = await JsonSerializer.DeserializeAsync<InboxRequest>(stream, _json, _shutdown.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Agent plan is empty.");
            if (request.Schema != 1) throw new InvalidDataException("Agent plan schema must be 1.");
            if (!RequestIdPattern().IsMatch(request.RequestId ?? string.Empty))
                throw new InvalidDataException("requestId may contain only letters, numbers, dot, underscore, and hyphen.");
            requestId = request.RequestId!;
            if (!string.Equals(request.DeviceSerial, _deviceSerial, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Plan targets {request.DeviceSerial}; active inbox is {_deviceSerial}.");
            if (request.Steps is null || request.Steps.Count == 0)
                throw new InvalidDataException("Agent plan contains no steps.");

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            if (!_active.TryAdd(requestId, cancellation))
                throw new InvalidDataException("requestId is already active: " + requestId);
            if (_pendingCancellations.TryRemove(requestId, out _)) cancellation.Cancel();
            var plan = new AndroidAgentPlan
            {
                AgentId = request.AgentId ?? string.Empty,
                DeviceSerial = request.DeviceSerial ?? string.Empty,
                AppPackage = request.AppPackage,
                Steps = request.Steps.Select(step => new AndroidAgentStep
                {
                    Name = step.Name ?? string.Empty,
                    Arguments = step.Arguments?.ToArray() ?? Array.Empty<string>(),
                    Access = ParseAccess(step.Access),
                    Timeout = TimeSpan.FromSeconds(step.TimeoutSeconds),
                    ContinueOnFailure = step.ContinueOnFailure,
                }).ToArray(),
            };
            var result = await _coordinator.RunAsync(plan, cancellation.Token).ConfigureAwait(false);
            await WriteResultAsync(requestId, new { schema = 1, requestId, verdict = result.Status.ToString(), result }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteResultAsync(requestId, new { schema = 1, requestId, verdict = "Cancelled", error = "Cancelled" }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteResultAsync(requestId, new { schema = 1, requestId, verdict = "Failed", error = ex.Message }).ConfigureAwait(false);
        }
        finally
        {
            if (cancellation is not null)
            {
                _active.TryRemove(requestId, out _);
                cancellation.Dispose();
            }
            var archived = Path.Combine(_archiveDirectory, requestId + ".json");
            try { File.Move(claimedPath, archived, overwrite: true); }
            catch { TryDelete(claimedPath); }
        }
    }

    private async Task WriteResultAsync(string requestId, object result)
    {
        if (!RequestIdPattern().IsMatch(requestId)) requestId = "invalid-" + Guid.NewGuid().ToString("N");
        var destination = Path.Combine(OutboxDirectory, requestId + ".result.json");
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result, _json) + Environment.NewLine, CancellationToken.None).ConfigureAwait(false);
        File.Move(temporary, destination, overwrite: true);
    }

    private static AndroidAgentAccess ParseAccess(string? access) => access?.Trim().ToLowerInvariant() switch
    {
        "observe" => AndroidAgentAccess.Observe,
        "device-control" or "devicecontrol" or "control" => AndroidAgentAccess.DeviceControl,
        _ => throw new InvalidDataException("Step access must be observe or device-control."),
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (var cancellation in _active.Values) cancellation.Cancel();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await Task.WhenAll(_tasks.Values).ConfigureAwait(false); } catch { }
        foreach (var cancellation in _active.Values) cancellation.Dispose();
        _shutdown.Dispose();
    }

    private sealed class InboxRequest
    {
        public int Schema { get; set; }
        public string? RequestId { get; set; }
        public string? AgentId { get; set; }
        public string? DeviceSerial { get; set; }
        public string? AppPackage { get; set; }
        public List<InboxStep>? Steps { get; set; }
    }

    private sealed class InboxStep
    {
        public string? Name { get; set; }
        public List<string>? Arguments { get; set; }
        public string? Access { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
        public bool ContinueOnFailure { get; set; }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,96}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();
}
