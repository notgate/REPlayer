using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReVM;

public interface IAndroidRuntimeBackend
{
    event Action<string, IntPtr>? WindowCreated;
    event Action<string>? StatusChanged;

    bool CheckEngine();
    bool IsBaseImageReady();
    Task<(bool success, string message)> EnsureBaseImageAsync(CancellationToken ct = default);
    List<VmInstance> GetInstances();
    void CreateInstance(string name, int cpuCount, int ramMB, int storageGB);
    void CreateInstance(string name, int cpuCount, int ramMB, int storageGB, string bootProfile) =>
        CreateInstance(name, cpuCount, ramMB, storageGB);
    void UpdateInstance(string vmId, string name, int cpuCount, int ramMB, int storageGB, string bootProfile) =>
        throw new NotSupportedException("This backend does not support editing instances.");
    (bool success, string debug) StartInstanceWithDebug(string vmId, IntPtr hostHandle, int hostX, int hostY, int hostW, int hostH);
    void StartInstance(string vmId, IntPtr hostHandle, int x, int y, int w, int h);
    void ResizeEmbeddedWindow(string vmId, int x, int y, int w, int h);
    void SetEmbeddedWindowVisible(string vmId, bool visible) { }
    void StopInstance(string vmId);
    Task PauseInstanceAsync(string vmId) => Task.CompletedTask;
    Task ResumeInstanceAsync(string vmId) => Task.CompletedTask;
    void StopAll();
    void DeleteInstance(string vmId);
    Task SetAndroidResolutionAsync(string vmId, int w, int h);
    Task SetAndroidRotationAsync(string vmId, int rotation);
    Task SendAndroidTouchAsync(string vmId, int fromX, int fromY, int toX, int toY, int durationMs);
    string GetAdbPath();
    string? GetAdbSerial(string vmId) => null;
    string? GetRunArtifactsDirectory(string vmId) => null;
    void RegisterRunArtifact(string vmId, string kind, string path) { }
}
