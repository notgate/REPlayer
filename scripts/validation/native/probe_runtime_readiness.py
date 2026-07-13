#!/usr/bin/env python3
"""Static guard for RevmNativeRuntimeService renderer readiness threading.

The WPF/runtime service cannot be fully exercised in headless WSL, so this probe
checks that the service owns a deterministic readiness tracker for the native
renderer chain and that the chain remains a REPlayer-owned renderer transport
surface, not an ADB/scrcpy/SDL/video display path.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SERVICE = ROOT / "ReVM" / "Runtime" / "Native" / "Service.cs"
TRACKER = ROOT / "ReVM" / "Rendering" / "Native" / "ReadinessTracker.cs"
MAIN_WINDOW = ROOT / "ReVM" / "UI" / "MainWindow.xaml.cs"

service = SERVICE.read_text(encoding="utf-8")
tracker = TRACKER.read_text(encoding="utf-8")
main_window = MAIN_WINDOW.read_text(encoding="utf-8")
combined = service + "\n" + tracker + "\n" + main_window

required_tracker_tokens = [
    "public sealed class RendererReadinessTracker",
    "public sealed record RendererReadinessSnapshot",
    "public RendererReadinessSnapshot Snapshot(string vmId)",
    "ReadyForPresent",
    "producer.frame_written",
    "transport.attached",
    "renderer.ready",
    "first_frame.ready",
    "IsReadyForPresent => ProducerFrameWritten && TransportAttached && RendererReady && FirstFrameReady && !Failed",
]

required_service_tokens = [
    "Dictionary<string, RendererReadinessTracker>",
    "public RendererReadinessSnapshot? GetRendererReadinessSnapshot(string vmId)",
    "tracker.Snapshot(vmId)",
    "RecordRendererReadiness(ev)",
    "renderer.ready_chain",
    "PresentLatestProducerFrames(ev.Message)",
    "RecordLocalPresentReadiness(vmId)",
    "readiness event={ev.Type}",
    "_rendererReadiness.Clear()",
    "_rendererReadiness.Remove(vmId)",
]

required_wpf_tokens = [
    "AppendRendererReadinessSnapshot()",
    "_engine is not RevmNativeRuntimeService nativeRuntime",
    "nativeRuntime.GetRendererReadinessSnapshot(_activeVm.Id)",
    "snapshot.ReadyForPresent",
    "snapshot.LastEventType",
    "snapshot.Summary",
]

missing = [token for token in required_tracker_tokens if token not in tracker]
missing += [token for token in required_service_tokens if token not in service]
missing += [token for token in required_wpf_tokens if token not in main_window]
if missing:
    raise SystemExit("missing readiness threading token(s): " + ", ".join(missing))

if "public sealed class RendererReadinessTracker" in service:
    raise SystemExit("RevmNativeRuntimeService must use ReVM.NativeRendering.RendererReadinessTracker, not a duplicate local tracker")

# Guardrails: display transport remains native/shared-memory/gfxstream only. The
# production code may mention banned tokens only as rejection/validation strings,
# so check for launch/control verbs rather than the validator deny-list itself.
banned_display_substrings = [
    "launch-scrcpy",
    "scrcpy server",
    "StartScrcpy",
    "adb display stream",
    "adb-display endpoint",
    "sdl reparent",
    "StartSdl",
    "H264Decoder",
    "StartWebRtc",
    "WebRtcDecoder",
    "WebRtcDisplay",
    "encoded-video endpoint",
]
violations = [token for token in banned_display_substrings if token.lower() in combined.lower()]
if violations:
    raise SystemExit("banned display transport implementation token(s) present: " + ", ".join(violations))

print("RevmNativeRuntimeService renderer readiness threading probe passed.")
