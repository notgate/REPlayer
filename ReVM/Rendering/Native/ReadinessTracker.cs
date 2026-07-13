using System;
using System.Collections.Generic;

namespace ReVM.NativeRendering;

/// <summary>
/// Small deterministic state machine for the REPlayer-owned renderer readiness chain.
/// It intentionally models only native renderer / shared-memory GPU producer events;
/// ADB, scrcpy, SDL and encoded-video display paths are excluded from the contract.
/// </summary>
public sealed class RendererReadinessTracker
{
    private static readonly HashSet<string> ReadinessEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "renderer.pending",
        "gpu_producer.pending",
        "gpu_producer.configured",
        "gfxstream.bridge.configured",
        "producer.created",
        "producer.frame_written",
        "transport.attached",
        "renderer.ready",
        "first_frame.ready",
        "renderer.probe.ready",
        "gpu_producer.invalid",
        "producer.failed",
        "renderer.failed"
    };

    public bool RendererPending { get; private set; }
    public bool GpuProducerConfigured { get; private set; }
    public bool ProducerFrameWritten { get; private set; }
    public bool TransportAttached { get; private set; }
    public bool RendererReady { get; private set; }
    public bool FirstFrameReady { get; private set; }
    public bool ProbeReady { get; private set; }
    public bool Failed { get; private set; }
    public string LastEventType { get; private set; } = "renderer.pending";
    public string LastMessage { get; private set; } = "Native renderer readiness not observed yet";

    public string Summary =>
        $"pending={RendererPending};gpuProducer={GpuProducerConfigured};frame={ProducerFrameWritten};" +
        $"transport={TransportAttached};renderer={RendererReady};firstFrame={FirstFrameReady};probe={ProbeReady};failed={Failed}";

    public bool IsReadyForPresent => ProducerFrameWritten && TransportAttached && RendererReady && FirstFrameReady && !Failed;

    public RendererReadinessSnapshot Snapshot(string vmId) => new(
        vmId,
        RendererPending,
        GpuProducerConfigured,
        ProducerFrameWritten,
        TransportAttached,
        RendererReady,
        FirstFrameReady,
        ProbeReady,
        Failed,
        IsReadyForPresent,
        LastEventType,
        LastMessage,
        Summary);

    public bool TryRecord(string eventType, string message, out string summary)
    {
        summary = Summary;
        if (string.IsNullOrWhiteSpace(eventType) || !ReadinessEvents.Contains(eventType))
            return false;

        LastEventType = eventType;
        LastMessage = message;

        if (eventType.EndsWith(".failed", StringComparison.OrdinalIgnoreCase) ||
            eventType.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase))
        {
            Failed = true;
        }

        switch (eventType)
        {
            case "renderer.pending":
            case "gpu_producer.pending":
                RendererPending = true;
                break;
            case "gpu_producer.configured":
            case "gfxstream.bridge.configured":
            case "producer.created":
                GpuProducerConfigured = true;
                break;
            case "producer.frame_written":
                ProducerFrameWritten = true;
                break;
            case "transport.attached":
                TransportAttached = true;
                break;
            case "renderer.ready":
                RendererReady = true;
                break;
            case "first_frame.ready":
                FirstFrameReady = true;
                break;
            case "renderer.probe.ready":
                ProbeReady = true;
                break;
        }

        summary = Summary;
        return true;
    }
}

public sealed record RendererReadinessSnapshot(
    string VmId,
    bool RendererPending,
    bool GpuProducerConfigured,
    bool ProducerFrameWritten,
    bool TransportAttached,
    bool RendererReady,
    bool FirstFrameReady,
    bool ProbeReady,
    bool Failed,
    bool ReadyForPresent,
    string LastEventType,
    string LastMessage,
    string Summary);
