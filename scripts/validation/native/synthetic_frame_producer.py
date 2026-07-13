#!/usr/bin/env python3
"""Emit a deterministic shm-frame-ring-v1 endpoint and synthetic BGRA frame ring.

This is a bring-up producer for the REPlayer-owned native renderer transport.
It does not use ADB, scrcpy, SDL, encoded video, or any third-party display path.
The binary ring image mirrors the v1 shared-memory layout so the future
revm-render-host consumer can be tested before the VM-side producer exists.
"""

from __future__ import annotations

import argparse
import json
import struct
from pathlib import Path
from typing import Final

FRAME_MAGIC: Final[int] = 0x4D465652  # "RVFM" little-endian bytes: R V F M
ABI_VERSION: Final[int] = 1
FORMAT_BGRA8888: Final[int] = 1
DEFAULT_SLOTS: Final[int] = 3
SLOT_FREE: Final[int] = 0
SLOT_READY: Final[int] = 2

HEADER_STRUCT: Final[struct.Struct] = struct.Struct("<IIIIIIQQII")
SLOT_STRUCT: Final[struct.Struct] = struct.Struct("<IQQII")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vm-id", default="native-0", help="Stable VM/instance id for endpoint names.")
    parser.add_argument("--width", type=int, default=720, help="Frame width in pixels.")
    parser.add_argument("--height", type=int, default=1520, help="Frame height in pixels.")
    parser.add_argument("--slots", type=int, default=DEFAULT_SLOTS, help="Ring slot count; v1 supports 1..3.")
    parser.add_argument("--frames", type=int, default=DEFAULT_SLOTS, help="Number of ready synthetic frames to write.")
    parser.add_argument("--output-dir", type=Path, default=Path("."), help="Directory for endpoint JSON and ring image.")
    return parser.parse_args()


def validate(width: int, height: int, slots: int, frames: int) -> None:
    if width <= 0 or height <= 0:
        raise ValueError("width and height must be positive")
    if slots < 1 or slots > DEFAULT_SLOTS:
        raise ValueError("shm-frame-ring-v1 supports 1..3 slots")
    if frames < 1 or frames > slots:
        raise ValueError("frames must be in the range 1..slots")
    stride = width * 4
    frame_bytes = stride * height
    if frame_bytes > 128 * 1024 * 1024:
        raise ValueError("single synthetic frame exceeds 128 MiB safety cap")


def endpoint_for(vm_id: str, width: int, height: int, slots: int, ring_path: Path) -> dict[str, object]:
    return {
        "kind": "shm-frame-ring-v1",
        "abiVersion": ABI_VERSION,
        "vmId": vm_id,
        "mappingName": f"Global\\RevmFrameRing-{vm_id}",
        "readyEventName": f"Global\\RevmFrameReady-{vm_id}",
        "controlPipeName": f"\\\\.\\pipe\\revm-control-{vm_id}",
        "width": width,
        "height": height,
        "stride": width * 4,
        "format": "BGRA8888",
        "formatCode": FORMAT_BGRA8888,
        "slots": slots,
        "ringImagePath": str(ring_path),
    }


def synthetic_bgra(width: int, height: int, frame_id: int) -> bytes:
    data = bytearray(width * height * 4)
    offset = 0
    for y in range(height):
        for x in range(width):
            data[offset + 0] = (x + frame_id * 17) & 0xFF  # B
            data[offset + 1] = (y + frame_id * 29) & 0xFF  # G
            data[offset + 2] = ((x ^ y) + frame_id * 43) & 0xFF  # R
            data[offset + 3] = 0xFF  # A
            offset += 4
    return bytes(data)


def build_ring_image(width: int, height: int, slots: int, frames: int) -> bytes:
    stride = width * 4
    frame_size = stride * height
    header_size = HEADER_STRUCT.size
    slot_table_size = SLOT_STRUCT.size * slots
    first_data_offset = header_size + slot_table_size
    ready_index = frames - 1

    header = HEADER_STRUCT.pack(
        FRAME_MAGIC,
        ABI_VERSION,
        width,
        height,
        stride,
        FORMAT_BGRA8888,
        slots,
        frames,
        ready_index,
        0,
    )

    slot_records = []
    frame_payloads = []
    for slot in range(slots):
        is_ready = slot < frames
        frame_id = slot + 1 if is_ready else 0
        data_offset = first_data_offset + slot * frame_size
        slot_records.append(
            SLOT_STRUCT.pack(
                SLOT_READY if is_ready else SLOT_FREE,
                frame_id,
                0,
                data_offset,
                frame_size if is_ready else 0,
            )
        )
        frame_payloads.append(synthetic_bgra(width, height, frame_id) if is_ready else bytes(frame_size))

    return header + b"".join(slot_records) + b"".join(frame_payloads)


def main() -> int:
    args = parse_args()
    validate(args.width, args.height, args.slots, args.frames)

    args.output_dir.mkdir(parents=True, exist_ok=True)
    safe_vm_id = "".join(c if c.isalnum() or c in "-." else "-" for c in args.vm_id)
    ring_path = args.output_dir / f"{safe_vm_id}.frame-ring.bin"
    endpoint_path = args.output_dir / f"{safe_vm_id}.endpoint.json"

    ring_path.write_bytes(build_ring_image(args.width, args.height, args.slots, args.frames))
    endpoint = endpoint_for(args.vm_id, args.width, args.height, args.slots, ring_path.resolve())
    endpoint_path.write_text(json.dumps(endpoint, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    print(json.dumps({"endpoint": str(endpoint_path), "ringImage": str(ring_path), "bytes": ring_path.stat().st_size}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
