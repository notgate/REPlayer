#!/usr/bin/env python3
"""Regression harness for revm-vmm --validate-renderer-endpoint.

The harness generates a clean-room shm-frame-ring-v1 endpoint/ring image with the
synthetic producer, then mutates the ring to verify deterministic native renderer
transport rejection for malformed ready-frame cases.

This is validation/control only. It does not add scrcpy, ADB display streaming,
SDL reparenting, encoded video, or proprietary renderer assets.
"""

from __future__ import annotations

import argparse
import json
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Final

REPO_ROOT: Final[Path] = Path(__file__).resolve().parents[3]
PRODUCER: Final[Path] = REPO_ROOT / "scripts" / "validation" / "native" / "synthetic_frame_producer.py"
DEFAULT_VMM: Final[Path] = REPO_ROOT / "runtime-src" / "RevmVmm" / "bin" / "Release" / "net9.0-windows" / "revm-vmm.exe"
DOTNET_EXE: Final[Path] = Path("/mnt/c/Program Files/dotnet/dotnet.exe")
RUNTIME_SOLUTION: Final[str] = subprocess.run(
    ["wslpath", "-w", str(REPO_ROOT / "runtime-src" / "RevmRuntime.sln")],
    text=True, capture_output=True, check=True
).stdout.strip()

HEADER_STRUCT: Final[struct.Struct] = struct.Struct("<IIIIIIQQII")
SLOT_STRUCT: Final[struct.Struct] = struct.Struct("<IQQII")
HEADER_SIZE: Final[int] = HEADER_STRUCT.size
SLOT_SIZE: Final[int] = SLOT_STRUCT.size


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vmm", type=Path, default=DEFAULT_VMM, help="Path to built revm-vmm.exe.")
    parser.add_argument("--keep", action="store_true", help="Keep temporary generated endpoints for inspection.")
    parser.add_argument(
        "--build-if-missing",
        action="store_true",
        help="Build RevmRuntime.sln if the default revm-vmm.exe is missing.",
    )
    return parser.parse_args()


def ensure_vmm(vmm: Path, build_if_missing: bool) -> Path:
    vmm = vmm.resolve()
    if vmm.exists():
        return vmm
    if build_if_missing and DOTNET_EXE.exists():
        subprocess.run(
            [str(DOTNET_EXE), "build", RUNTIME_SOLUTION, "-c", "Release", "--no-restore"],
            cwd=REPO_ROOT,
            check=True,
        )
    if not vmm.exists():
        raise FileNotFoundError(
            f"revm-vmm.exe not found at {vmm}; run the runtime Release build or pass --vmm."
        )
    return vmm


def run_json(cmd: list[str], cwd: Path) -> list[dict[str, object]]:
    result = subprocess.run(cmd, cwd=cwd, text=True, capture_output=True, check=False)
    events: list[dict[str, object]] = []
    for line in result.stdout.splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            events.append(json.loads(line))
        except json.JSONDecodeError:
            pass
    return_code = int(result.returncode)
    return [{"_exitCode": return_code, "_stderr": result.stderr, "_stdout": result.stdout}, *events]


def event_types(events: list[dict[str, object]]) -> list[str]:
    return [str(event.get("Type", "")) for event in events if "Type" in event]


def event_messages(events: list[dict[str, object]]) -> str:
    return "\n".join(str(event.get("Message", "")) for event in events if "Message" in event)


def to_windows_path(path: Path) -> str:
    """Translate a WSL /mnt/<drive>/ path for the Windows .NET VMM process."""
    resolved = path.resolve()
    parts = resolved.parts
    if len(parts) >= 4 and parts[0] == "/" and parts[1] == "mnt" and len(parts[2]) == 1:
        drive = parts[2].upper()
        return drive + ":\\" + "\\".join(parts[3:])
    return str(resolved)


def write_endpoint_with_windows_ring_path(endpoint_path: Path, ring_path: Path) -> None:
    endpoint = json.loads(endpoint_path.read_text(encoding="utf-8"))
    endpoint["ringImagePath"] = to_windows_path(ring_path)
    endpoint_path.write_text(json.dumps(endpoint, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def generate_endpoint(work: Path) -> tuple[Path, Path]:
    subprocess.run(
        [
            sys.executable,
            str(PRODUCER),
            "--vm-id",
            "probe-regression",
            "--width",
            "4",
            "--height",
            "3",
            "--slots",
            "3",
            "--frames",
            "3",
            "--output-dir",
            str(work),
        ],
        cwd=REPO_ROOT,
        check=True,
    )
    endpoint_path = work / "probe-regression.endpoint.json"
    ring_path = work / "probe-regression.frame-ring.bin"
    write_endpoint_with_windows_ring_path(endpoint_path, ring_path)
    return endpoint_path, ring_path


def read_header(ring: bytearray) -> tuple[int, int, int, int, int, int, int, int, int, int]:
    return HEADER_STRUCT.unpack_from(ring, 0)


def write_header(ring: bytearray, header: tuple[int, int, int, int, int, int, int, int, int, int]) -> None:
    HEADER_STRUCT.pack_into(ring, 0, *header)


def slot_offset(slot_index: int) -> int:
    return HEADER_SIZE + slot_index * SLOT_SIZE


def read_slot(ring: bytearray, slot_index: int) -> tuple[int, int, int, int, int]:
    return SLOT_STRUCT.unpack_from(ring, slot_offset(slot_index))


def write_slot(ring: bytearray, slot_index: int, slot: tuple[int, int, int, int, int]) -> None:
    SLOT_STRUCT.pack_into(ring, slot_offset(slot_index), *slot)


def mutate_ring(source: Path, target: Path, mutator: str) -> None:
    ring = bytearray(source.read_bytes())
    header = list(read_header(ring))
    ready_index = int(header[8])
    slot = list(read_slot(ring, ready_index))

    if mutator == "bad-ready-index":
        header[8] = 9
        write_header(ring, tuple(header))
    elif mutator == "mismatched-frame-id":
        slot[1] = int(header[7]) + 100
        write_slot(ring, ready_index, tuple(slot))
    elif mutator == "bad-payload-size":
        slot[4] = int(slot[4]) - 4
        write_slot(ring, ready_index, tuple(slot))
    elif mutator == "out-of-bounds-payload-offset":
        slot[3] = len(ring) - 2
        write_slot(ring, ready_index, tuple(slot))
    else:
        raise ValueError(mutator)

    target.write_bytes(ring)


def endpoint_with_ring(base_endpoint: Path, ring: Path, target: Path) -> Path:
    endpoint = json.loads(base_endpoint.read_text(encoding="utf-8"))
    endpoint["ringImagePath"] = to_windows_path(ring)
    target.write_text(json.dumps(endpoint, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return target


def probe(vmm: Path, endpoint: Path, work: Path) -> tuple[int, list[str], str]:
    log_dir = work / (endpoint.stem + ".logs")
    cmd = [str(vmm), "--validate-renderer-endpoint", "--renderer-endpoint", to_windows_path(endpoint), "--log-dir", to_windows_path(log_dir)]
    events = run_json(cmd, cwd=REPO_ROOT)
    exit_code = int(events[0]["_exitCode"])
    return exit_code, event_types(events), event_messages(events)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> int:
    args = parse_args()
    vmm = ensure_vmm(args.vmm, args.build_if_missing)
    temp_parent = REPO_ROOT / ".tmp"
    temp_parent.mkdir(exist_ok=True)
    temp_root = Path(tempfile.mkdtemp(prefix="revm-renderer-probe-", dir=temp_parent))
    try:
        endpoint, ring = generate_endpoint(temp_root)

        cases = [
            ("valid", None, 0, "renderer.probe.ready", "first_frame.ready", ""),
            ("bad-ready-index", "bad-ready-index", 2, "renderer.probe.invalid", "transport.invalid", "ready_index"),
            ("mismatched-frame-id", "mismatched-frame-id", 2, "renderer.probe.invalid", "transport.invalid", "frame_id"),
            ("bad-payload-size", "bad-payload-size", 2, "renderer.probe.invalid", "transport.invalid", "size"),
            ("out-of-bounds-payload-offset", "out-of-bounds-payload-offset", 2, "renderer.probe.invalid", "transport.invalid", "outside"),
        ]

        report: list[dict[str, object]] = []
        for name, mutator, expected_exit, expected_probe_event, expected_transport_event, expected_message in cases:
            case_endpoint = endpoint
            if mutator is not None:
                case_ring = temp_root / f"{name}.frame-ring.bin"
                mutate_ring(ring, case_ring, mutator)
                case_endpoint = endpoint_with_ring(endpoint, case_ring, temp_root / f"{name}.endpoint.json")

            exit_code, types, messages = probe(vmm, case_endpoint, temp_root)
            require(exit_code == expected_exit, f"{name}: exit {exit_code}, expected {expected_exit}; events={types}")
            require(expected_probe_event in types, f"{name}: missing event {expected_probe_event}; events={types}")
            require(expected_transport_event in types, f"{name}: missing event {expected_transport_event}; events={types}")
            if expected_message:
                require(expected_message in messages, f"{name}: expected message containing {expected_message!r}; messages={messages}")
            report.append({"case": name, "exitCode": exit_code, "events": types})

        print(json.dumps({"status": "passed", "cases": report}, indent=2, sort_keys=True))
        return 0
    finally:
        if args.keep:
            print(json.dumps({"keptTempDir": str(temp_root)}, sort_keys=True))
        else:
            shutil.rmtree(temp_root, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
