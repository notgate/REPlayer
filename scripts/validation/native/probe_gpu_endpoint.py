#!/usr/bin/env python3
"""Probe the revm-gpu-producer-v1 wrapper contract.

This validates the first REPlayer-owned producer seam: a clean-room GPU producer
endpoint wrapper around today's shm-frame-ring-v1 BGRA payload, plus the
virtio-gpu/gfxstream scanout-import wrapper that carries a command endpoint and
a BGRA frame-ring payload for the live scanout experiment. It explicitly
rejects scrcpy/ADB display/SDL/encoded-video producer claims.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOTNET = Path("/mnt/c/Program Files/dotnet/dotnet.exe")
TMP_ROOT = Path("/mnt/d/Downloads/revm-renderer-gpu-producer-probe-tmp")

FRAME_MAGIC = 0x4D465652
ABI_VERSION = 1
FORMAT_BGRA8888 = 1
HEADER_SIZE = 48
SLOT_SIZE = 28


def win_path(path: Path) -> str:
    try:
        return subprocess.check_output(["wslpath", "-w", str(path)], text=True).strip()
    except (FileNotFoundError, subprocess.CalledProcessError):
        resolved = path.resolve()
        if str(resolved).startswith("/mnt/"):
            drive = str(resolved)[5]
            rest = str(resolved)[7:].replace("/", "\\")
            return f"{drive.upper()}:\\{rest}"
        raise


def run(cmd: list[str], cwd: Path | None = None, check: bool = True) -> subprocess.CompletedProcess[str]:
    print("+", " ".join(cmd))
    result = subprocess.run(cmd, cwd=str(cwd) if cwd else None, text=True, capture_output=True)
    if result.stdout:
        print(result.stdout)
    if result.stderr:
        print(result.stderr, file=sys.stderr)
    if check and result.returncode != 0:
        raise subprocess.CalledProcessError(result.returncode, cmd, result.stdout, result.stderr)
    return result


def write_ring(path: Path, width: int = 4, height: int = 3, slots: int = 3) -> None:
    import struct

    stride = width * 4
    frame_bytes = stride * height
    first_data_offset = HEADER_SIZE + SLOT_SIZE * slots
    ready_index = slots - 1
    with path.open("wb") as f:
        f.write(struct.pack("<IIIIIIQQII", FRAME_MAGIC, ABI_VERSION, width, height, stride, FORMAT_BGRA8888, slots, slots, ready_index, 0))
        for slot in range(slots):
            f.write(struct.pack("<IQQII", 2, slot + 1, 0, first_data_offset + slot * frame_bytes, frame_bytes))
        for slot in range(slots):
            frame_id = slot + 1
            for y in range(height):
                for x in range(width):
                    f.write(bytes(((x + frame_id * 17) & 0xFF, (y + frame_id * 29) & 0xFF, ((x ^ y) + frame_id * 43) & 0xFF, 0xFF)))


def frame_endpoint(ring_path: Path, vm_id: str = "gpu-producer-probe", width: int = 4, height: int = 3, slots: int = 3) -> dict:
    return {
        "kind": "shm-frame-ring-v1",
        "abiVersion": 1,
        "vmId": vm_id,
        "mappingName": "Local\\RevmFrameRing-" + vm_id,
        "readyEventName": "Local\\RevmFrameReady-" + vm_id,
        "controlPipeName": "\\\\.\\pipe\\revm-control-" + vm_id,
        "width": width,
        "height": height,
        "stride": width * 4,
        "format": "BGRA8888",
        "formatCode": 1,
        "slots": slots,
        "ringImagePath": win_path(ring_path),
    }


def producer_endpoint(frame: dict, **overrides: object) -> dict:
    endpoint = {
        "kind": "revm-gpu-producer-v1",
        "abiVersion": 1,
        "vmId": frame["vmId"],
        "producerKind": "shm-bgra-frame-ring",
        "displayMode": "native-hwnd-virtio-gpu-gfxstream",
        "width": frame["width"],
        "height": frame["height"],
        "format": "BGRA8888",
        "capabilities": {
            "producesBgraFrames": True,
            "producesGpuCommands": False,
            "supportsHostGpu": False,
            "supportsVirtioGpu": False,
            "supportsGfxstream": False,
            "requiresAdb": False,
            "usesEncodedVideo": False,
        },
        "frameEndpoint": frame,
    }
    for key, value in overrides.items():
        endpoint[key] = value
    return endpoint


def write_probe_project(
    work: Path,
    endpoint_json: str,
    bad_scrcpy_json: str,
    gfxstream_json: str,
    scanout_import_json: str,
    missing_frame_json: str,
    bad_scanout_json: str,
) -> None:
    render_host_project = ROOT / "runtime-src" / "RevmRenderHost" / "RevmRenderHost.csproj"
    (work / "GpuProducerProbe.csproj").write_text(
        f"""<Project Sdk=\"Microsoft.NET.Sdk\">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=\"{win_path(render_host_project)}\" />
  </ItemGroup>
</Project>
""",
        encoding="utf-8",
    )
    (work / "Program.cs").write_text(
        f'''using RevmRenderHost;

static void Require(bool condition, string message)
{{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"ok {{message}}");
}}

var endpoint = @"{endpoint_json.replace('"', '""')}";
var badScrcpy = @"{bad_scrcpy_json.replace('"', '""')}";
var gfxstream = @"{gfxstream_json.replace('"', '""')}";
var scanoutImport = @"{scanout_import_json.replace('"', '""')}";
var missingFrame = @"{missing_frame_json.replace('"', '""')}";
var badScanout = @"{bad_scanout_json.replace('"', '""')}";

Require(RevmRendererAbi.ValidateTransportEndpoint(endpoint) == RevmRendererStatus.Ok, "GPU producer wrapper validates through renderer ABI");
Require(RevmGpuProducerEndpoint.TryParse(endpoint, out var producer) == RevmRendererStatus.Ok, "GPU producer endpoint parses");
Require(producer is not null, "GPU producer endpoint object is non-null");
Require(producer!.ProducerKind == "shm-bgra-frame-ring", "GPU producer kind is shm-bgra-frame-ring");
Require(producer.Capabilities.ProducesBgraFrames, "GPU producer capabilities advertise BGRA frames");
Require(!producer.Capabilities.RequiresAdb && !producer.Capabilities.UsesEncodedVideo, "GPU producer capabilities reject ADB/video display dependencies");
Require(producer.FrameEndpoint is not null, "GPU producer wraps a frame-ring endpoint");
var session = new RenderHostSession(new IntPtr(0x1234), 4, 3);
Require(session.AttachVmTransport(endpoint) == RevmRendererStatus.Ok, "RenderHostSession attaches GPU producer wrapper");
Require(session.HasReadyFrame, "RenderHostSession sees first ready frame through producer wrapper");
Require(session.TryReadLatestFrame(out var frame) == RevmRendererStatus.Ok && frame is not null, "RenderHostSession reads latest frame through producer wrapper");
Require(frame!.Info.FrameId == 3, "producer wrapper preserves latest frame id");
Require(frame.Pixels[0] == 51 && frame.Pixels[3] == 255, "producer wrapper preserves deterministic BGRA payload");
Require(session.Destroy() == RevmRendererStatus.Ok, "RenderHostSession destroys after producer wrapper probe");
Require(RevmRendererAbi.ValidateTransportEndpoint(badScrcpy) == RevmRendererStatus.UnsupportedTransportKind, "GPU producer wrapper rejects scrcpy/ADB/video display tokens");
Require(RevmRendererAbi.ValidateTransportEndpoint(gfxstream) == RevmRendererStatus.Ok, "virtio-gpu/gfxstream command bridge validates through renderer ABI");
Require(RevmGpuProducerEndpoint.TryParse(gfxstream, out var gfxProducer) == RevmRendererStatus.Ok && gfxProducer is not null, "virtio-gpu/gfxstream producer endpoint parses");
Require(gfxProducer!.ProducerKind == "virtio-gpu-gfxstream", "virtio-gpu/gfxstream producer kind is preserved");
Require(gfxProducer.Capabilities.ProducesGpuCommands && gfxProducer.Capabilities.SupportsVirtioGpu && gfxProducer.Capabilities.SupportsGfxstream, "virtio-gpu/gfxstream capabilities advertise command stream support");
var gfxSession = new RenderHostSession(new IntPtr(0x1234), 4, 3);
Require(gfxSession.AttachVmTransport(gfxstream) == RevmRendererStatus.Ok, "gfxstream command bridge attaches without QMP/frame-ring fallback");
Require(gfxSession.GfxstreamCommandEndpoint is not null, "gfxstream command bridge is tracked as command-only endpoint");
Require(gfxSession.PresentLatestFrame() == RevmRendererStatus.InvalidState, "gfxstream command bridge does not fake a first frame before host renderer output exists");
Require(gfxSession.Destroy() == RevmRendererStatus.Ok, "RenderHostSession destroys after gfxstream bridge probe");
Require(RevmRendererAbi.ValidateTransportEndpoint(scanoutImport) == RevmRendererStatus.Ok, "virtio-gpu/gfxstream scanout-import endpoint validates through renderer ABI");
Require(RevmGpuProducerEndpoint.TryParse(scanoutImport, out var scanoutProducer) == RevmRendererStatus.Ok && scanoutProducer is not null, "virtio-gpu/gfxstream scanout-import endpoint parses");
Require(scanoutProducer!.ProducerKind == "virtio-gpu-gfxstream-scanout-import", "scanout-import producer kind is preserved");
Require(scanoutProducer.Capabilities.ProducesBgraFrames && scanoutProducer.Capabilities.ProducesGpuCommands, "scanout-import advertises BGRA frames plus GPU commands");
Require(scanoutProducer.Capabilities.SupportsVirtioGpu && scanoutProducer.Capabilities.SupportsGfxstream, "scanout-import advertises virtio-gpu/gfxstream support");
Require(!scanoutProducer.Capabilities.RequiresAdb && !scanoutProducer.Capabilities.UsesEncodedVideo, "scanout-import rejects ADB/video display dependencies");
Require(scanoutProducer.FrameEndpoint is not null, "scanout-import wraps a frame-ring payload endpoint");
var scanoutSession = new RenderHostSession(new IntPtr(0x1234), 4, 3);
Require(scanoutSession.AttachVmTransport(scanoutImport) == RevmRendererStatus.Ok, "RenderHostSession attaches scanout-import payload ring");
Require(scanoutSession.HasReadyFrame, "RenderHostSession sees ready frame through scanout-import wrapper");
Require(scanoutSession.PresentLatestFrame() == RevmRendererStatus.Ok, "RenderHostSession presents latest frame through scanout-import wrapper");
Require(scanoutSession.Destroy() == RevmRendererStatus.Ok, "RenderHostSession destroys after scanout-import probe");
Require(RevmRendererAbi.ValidateTransportEndpoint(badScanout) == RevmRendererStatus.UnsupportedTransportKind, "scanout-import rejects missing gfxstream command endpoint");
Require(RevmRendererAbi.ValidateTransportEndpoint(missingFrame) == RevmRendererStatus.InvalidTransportEndpoint, "shm producer wrapper requires nested frameEndpoint");
Console.WriteLine("renderer GPU producer endpoint probe passed");
''',
        encoding="utf-8",
    )


def main() -> int:
    if not DOTNET.exists():
        print(f"error: dotnet not found at {DOTNET}", file=sys.stderr)
        return 2

    if TMP_ROOT.exists():
        shutil.rmtree(TMP_ROOT)
    TMP_ROOT.mkdir(parents=True)
    try:
        # Endpoint validation checks declared artifact presence; it does not load
        # the backend. Keep this probe independent of the ignored local SDK.
        gfxstream_backend_fixture = TMP_ROOT / "libgfxstream_backend.dll"
        gfxstream_backend_fixture.write_bytes(b"REPlayer gfxstream endpoint probe fixture\n")
        ring = TMP_ROOT / "gpu-producer-probe.frame-ring.bin"
        write_ring(ring)
        frame = frame_endpoint(ring)
        valid = producer_endpoint(frame)
        bad_scrcpy = producer_endpoint(frame, displayMode="scrcpy-adb-display-video")
        gfxstream = producer_endpoint(frame, producerKind="virtio-gpu-gfxstream", capabilities={
            "producesBgraFrames": False,
            "producesGpuCommands": True,
            "supportsHostGpu": True,
            "supportsVirtioGpu": True,
            "supportsGfxstream": True,
            "requiresAdb": False,
            "usesEncodedVideo": False,
        })
        gfxstream.pop("frameEndpoint", None)
        gfxstream["commandEndpoint"] = {
            "kind": "gfxstream-command-stream-v1",
            "rendererStack": "gfxstream-fastpipe",
            "rendererApi": "vulkan",
            "transport": "virtio-gpu-pci",
            "protocol": "gfxstream-vulkan-or-gles",
            "rendererBackendPath": win_path(gfxstream_backend_fixture),
        }
        scanout_import = producer_endpoint(frame, producerKind="virtio-gpu-gfxstream-scanout-import", capabilities={
            "producesBgraFrames": True,
            "producesGpuCommands": True,
            "supportsHostGpu": True,
            "supportsVirtioGpu": True,
            "supportsGfxstream": True,
            "requiresAdb": False,
            "usesEncodedVideo": False,
        })
        scanout_import["commandEndpoint"] = {
            "kind": "gfxstream-command-stream-v1",
            "rendererStack": "gfxstream-fastpipe",
            "rendererApi": "vulkan",
            "transport": "virtio-gpu-pci",
            "protocol": "gfxstream-or-virgl-scanout-experiment",
        }
        bad_scanout = dict(scanout_import)
        bad_scanout["commandEndpoint"] = {"kind": "scrcpy-adb-display"}
        missing_frame = producer_endpoint(frame)
        missing_frame.pop("frameEndpoint", None)

        valid_json = json.dumps(valid, separators=(",", ":"))
        bad_scrcpy_json = json.dumps(bad_scrcpy, separators=(",", ":"))
        gfxstream_json = json.dumps(gfxstream, separators=(",", ":"))
        scanout_import_json = json.dumps(scanout_import, separators=(",", ":"))
        missing_frame_json = json.dumps(missing_frame, separators=(",", ":"))
        bad_scanout_json = json.dumps(bad_scanout, separators=(",", ":"))

        write_probe_project(TMP_ROOT, valid_json, bad_scrcpy_json, gfxstream_json, scanout_import_json, missing_frame_json, bad_scanout_json)
        run([str(DOTNET), "run", "--project", win_path(TMP_ROOT / "GpuProducerProbe.csproj"), "-c", "Release"])

        vmm = ROOT / "runtime-src" / "RevmVmm" / "bin" / "Release" / "net9.0-windows" / "revm-vmm.exe"
        if not vmm.exists():
            print(f"skip: VMM exe not built at {vmm}")
        else:
            endpoint_path = TMP_ROOT / "gpu-producer-endpoint.json"
            endpoint_path.write_text(valid_json, encoding="utf-8")
            result = run([str(vmm), "--validate-renderer-endpoint", "--renderer-endpoint", win_path(endpoint_path), "--log-dir", win_path(TMP_ROOT / "logs")], check=False)
            if result.returncode != 0:
                print(result.stderr, file=sys.stderr)
                raise SystemExit(result.returncode)
            RequireText = ["gpu_producer.configured", "transport.attached", "renderer.ready", "first_frame.ready"]
            for needle in RequireText:
                if needle not in result.stdout:
                    raise RuntimeError(f"VMM validation output missing {needle}")

            gfxstream_path = TMP_ROOT / "gfxstream-producer-endpoint.json"
            gfxstream_path.write_text(gfxstream_json, encoding="utf-8")
            gfx_result = run([str(vmm), "--validate-renderer-endpoint", "--renderer-endpoint", win_path(gfxstream_path), "--log-dir", win_path(TMP_ROOT / "gfxstream-logs")], check=False)
            if gfx_result.returncode != 2:
                raise RuntimeError(f"gfxstream bridge validation should remain pending with exit 2 until scanout import exists, got {gfx_result.returncode}")
            for needle in ("gpu_producer.configured", "gfxstream.bridge.configured", "transport.attached", "first_frame.pending"):
                if needle not in gfx_result.stdout:
                    raise RuntimeError(f"VMM gfxstream bridge output missing {needle}")

            scanout_path = TMP_ROOT / "scanout-import-producer-endpoint.json"
            scanout_path.write_text(scanout_import_json, encoding="utf-8")
            scanout_result = run([str(vmm), "--validate-renderer-endpoint", "--renderer-endpoint", win_path(scanout_path), "--log-dir", win_path(TMP_ROOT / "scanout-import-logs")], check=False)
            if scanout_result.returncode != 0:
                raise RuntimeError(f"scanout-import validation should be ready after a ready BGRA ring exists, got {scanout_result.returncode}")
            for needle in ("gpu_producer.configured", "gfxstream.bridge.configured", "transport.attached", "renderer.ready", "first_frame.ready"):
                if needle not in scanout_result.stdout:
                    raise RuntimeError(f"VMM scanout-import output missing {needle}")

        print("renderer GPU producer endpoint VMM probe passed")
    finally:
        shutil.rmtree(TMP_ROOT, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
