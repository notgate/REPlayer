#!/usr/bin/env python3
"""Probe revm-vmm's bounded host-owned shm-frame-ring-v1 synthetic producer."""
from __future__ import annotations

import json
import shutil
import struct
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOTNET = Path("/mnt/c/Program Files/dotnet/dotnet.exe")
TMP_ROOT = Path("/mnt/d/Downloads/revm-vmm-synthetic-producer-probe-tmp")
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


def run(cmd: list[str], check: bool = True) -> subprocess.CompletedProcess[str]:
    print("+", " ".join(cmd))
    result = subprocess.run(cmd, text=True, capture_output=True)
    if result.stdout:
        print(result.stdout)
    if result.stderr:
        print(result.stderr, file=sys.stderr)
    if check and result.returncode != 0:
        raise subprocess.CalledProcessError(result.returncode, cmd, result.stdout, result.stderr)
    return result


def endpoint(ring: Path, width: int = 5, height: int = 4, slots: int = 3) -> dict:
    vm_id = "synthetic-producer-probe"
    return {
        "kind": "revm-gpu-producer-v1",
        "abiVersion": 1,
        "vmId": vm_id,
        "producerKind": "shm-bgra-frame-ring",
        "displayMode": "native-hwnd-virtio-gpu-gfxstream",
        "width": width,
        "height": height,
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
        "frameEndpoint": {
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
            "ringImagePath": win_path(ring),
        },
    }


def read_ring(path: Path) -> tuple[int, int, int, int]:
    data = path.read_bytes()
    magic, abi, width, height, stride, fmt, slots, last_frame_id, ready_index, _ = struct.unpack_from("<IIIIIIQQII", data, 0)
    assert magic == FRAME_MAGIC, magic
    assert abi == ABI_VERSION, abi
    assert fmt == FORMAT_BGRA8888, fmt
    assert slots == 3, slots
    assert last_frame_id == 4, last_frame_id
    assert ready_index == 0, ready_index
    slot_offset = HEADER_SIZE + ready_index * SLOT_SIZE
    state, slot_frame_id, _qpc, data_offset, data_size = struct.unpack_from("<IQQII", data, slot_offset)
    assert state == 2, state
    assert slot_frame_id == last_frame_id, (slot_frame_id, last_frame_id)
    assert data_size == stride * height, data_size
    first_pixel = data[data_offset : data_offset + 4]
    assert first_pixel == bytes((68, 116, 172, 255)), first_pixel
    return width, height, stride, data_size


def main() -> int:
    if not DOTNET.exists():
        print(f"error: dotnet not found at {DOTNET}", file=sys.stderr)
        return 2
    if TMP_ROOT.exists():
        shutil.rmtree(TMP_ROOT)
    TMP_ROOT.mkdir(parents=True)
    try:
        run([str(DOTNET), "build", win_path(ROOT / "runtime-src" / "RevmRuntime.sln"), "-c", "Release", "--no-restore"])
        vmm = ROOT / "runtime-src" / "RevmVmm" / "bin" / "Release" / "net9.0-windows" / "revm-vmm.exe"
        ring = TMP_ROOT / "synthetic-producer.frame-ring.bin"
        endpoint_path = TMP_ROOT / "producer-endpoint.json"
        endpoint_path.write_text(json.dumps(endpoint(ring), separators=(",", ":")), encoding="utf-8")
        result = run([
            str(vmm),
            "--renderer-endpoint", win_path(endpoint_path),
            "--synthetic-producer-frames", "4",
            "--synthetic-producer-interval-ms", "1",
            "--validate-after-synthetic-producer",
            "--log-dir", win_path(TMP_ROOT / "logs"),
        ])
        required_events = (
            "producer.created",
            "producer.frame_written",
            "producer.stopped",
            "transport.attached",
            "renderer.ready",
            "first_frame.ready",
            "renderer.probe.ready",
        )
        for needle in required_events:
            if needle not in result.stdout:
                raise RuntimeError(f"VMM synthetic producer output missing {needle}")
        event_positions = [result.stdout.index(needle) for needle in (
            "producer.frame_written",
            "transport.attached",
            "renderer.ready",
            "first_frame.ready",
        )]
        if event_positions != sorted(event_positions):
            raise RuntimeError("synthetic producer/renderer readiness events were not emitted in the expected order")
        dims = read_ring(ring)
        print(f"VMM synthetic frame producer probe passed; final ring dims={dims}")
        return 0
    finally:
        shutil.rmtree(TMP_ROOT, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
