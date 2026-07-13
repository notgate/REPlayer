#!/usr/bin/env python3
"""Probe revm-vmm's one-shot QMP guest scanout producer path.

The probe uses a tiny fake QMP server that implements qmp_capabilities plus
human-monitor-command/screendump and writes a deterministic P6 PPM. revm-vmm
then imports that PPM into the REPlayer-owned shm-frame-ring-v1 BGRA endpoint
and validates renderer/first-frame readiness. This is a raw scanout test only:
no scrcpy, no ADB display streaming, no SDL reparenting, and no encoded video.
"""
from __future__ import annotations

import json
import re
import shutil
import socket
import struct
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOTNET = Path("/mnt/c/Program Files/dotnet/dotnet.exe")
TMP_ROOT = Path("/mnt/d/Downloads/revm-vmm-qmp-scanout-probe-tmp")
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


def wsl_path(path: str) -> Path:
    normalized = path.replace("\\\\", "\\")
    match = re.match(r"^([A-Za-z]):\\(.*)$", normalized)
    if match:
        drive = match.group(1).lower()
        rest = match.group(2).replace("\\", "/")
        return Path(f"/mnt/{drive}/{rest}")
    return Path(normalized)


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


def endpoint(ring: Path, width: int = 3, height: int = 2, slots: int = 3) -> dict:
    vm_id = "qmp-scanout-probe"
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


def write_ppm(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    # 3x2 RGB image. The first pixel RGB=(10, 20, 30) must become BGRA=(30,20,10,255).
    pixels = bytes([
        10, 20, 30,  40, 50, 60,  70, 80, 90,
        15, 25, 35,  45, 55, 65,  75, 85, 95,
    ])
    payload = b"P6\n3 2\n255\n" + pixels
    with path.open("wb") as f:
        f.write(payload)
        f.flush()
        try:
            import os
            os.fsync(f.fileno())
        except OSError:
            pass
    time.sleep(0.05)


def qmp_server(port_holder: list[int], ready: threading.Event, stop: threading.Event) -> None:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind(("127.0.0.1", 0))
        server.listen(1)
        server.settimeout(10)
        port_holder.append(server.getsockname()[1])
        ready.set()
        try:
            conn, _addr = server.accept()
        except TimeoutError:
            return
        with conn:
            conn.settimeout(10)
            conn.sendall(b'{"QMP":{"version":{"qemu":{"major":10,"minor":2,"micro":0}},"capabilities":[]}}\r\n')
            buf = b""
            while not stop.is_set():
                chunk = conn.recv(4096)
                if not chunk:
                    break
                buf += chunk
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    if not line.strip():
                        continue
                    message = json.loads(line.decode("utf-8-sig"))
                    if message.get("execute") == "qmp_capabilities":
                        conn.sendall(b'{"return":{}}\r\n')
                        continue
                    args = message.get("arguments", {})
                    command = args.get("command-line", "")
                    if command.startswith("screendump "):
                        write_ppm(wsl_path(command[len("screendump "):]))
                        conn.sendall(b'{"return":""}\r\n')
                    else:
                        conn.sendall(b'{"error":{"class":"CommandNotFound","desc":"unsupported fake QMP command"}}\r\n')
                    stop.set()
                    return


def read_ring(path: Path) -> None:
    data = path.read_bytes()
    magic, abi, width, height, stride, fmt, slots, last_frame_id, ready_index, _ = struct.unpack_from("<IIIIIIQQII", data, 0)
    assert magic == FRAME_MAGIC, magic
    assert abi == ABI_VERSION, abi
    assert (width, height, stride, fmt, slots, last_frame_id, ready_index) == (3, 2, 12, FORMAT_BGRA8888, 3, 1, 0), (width, height, stride, fmt, slots, last_frame_id, ready_index)
    state, slot_frame_id, _qpc, data_offset, data_size = struct.unpack_from("<IQQII", data, HEADER_SIZE)
    assert (state, slot_frame_id, data_size) == (2, 1, stride * height), (state, slot_frame_id, data_size)
    assert data[data_offset:data_offset + 4] == bytes((30, 20, 10, 255)), data[data_offset:data_offset + 4]


def main() -> int:
    if not DOTNET.exists():
        print(f"error: dotnet not found at {DOTNET}", file=sys.stderr)
        return 2
    if TMP_ROOT.exists():
        shutil.rmtree(TMP_ROOT)
    TMP_ROOT.mkdir(parents=True)
    stop = threading.Event()
    ready = threading.Event()
    ports: list[int] = []
    server = threading.Thread(target=qmp_server, args=(ports, ready, stop), daemon=True)
    server.start()
    if not ready.wait(5):
        raise RuntimeError("fake QMP server did not start")
    try:
        run([str(DOTNET), "build", win_path(ROOT / "runtime-src" / "RevmRuntime.sln"), "-c", "Release", "--no-restore"])
        vmm = ROOT / "runtime-src" / "RevmVmm" / "bin" / "Release" / "net9.0-windows" / "revm-vmm.exe"
        ring = TMP_ROOT / "qmp-scanout.frame-ring.bin"
        endpoint_path = TMP_ROOT / "qmp-scanout-endpoint.json"
        endpoint_path.write_text(json.dumps(endpoint(ring), separators=(",", ":")), encoding="utf-8")
        result = run([
            str(vmm),
            "--renderer-endpoint", win_path(endpoint_path),
            "--probe-qmp-scanout-once",
            "--qmp-port", str(ports[0]),
            "--log-dir", win_path(TMP_ROOT / "logs"),
        ])
        for needle in (
            "producer.created",
            "producer.frame_written",
            "producer.stopped",
            "transport.attached",
            "renderer.ready",
            "first_frame.ready",
            "renderer.probe.ready",
        ):
            if needle not in result.stdout:
                raise RuntimeError(f"VMM QMP scanout probe output missing {needle}")
        read_ring(ring)
        print("VMM QMP scanout producer probe passed; first RGB pixel imported as BGRA and renderer readiness validated")
        return 0
    finally:
        stop.set()
        server.join(timeout=2)
        shutil.rmtree(TMP_ROOT, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
