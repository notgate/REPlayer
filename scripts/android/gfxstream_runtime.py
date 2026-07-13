#!/usr/bin/env python3
"""Create a hash-pinned REPlayer gfxstream backend for Emulator 37.1.7.

The transform is intentionally narrow:
  * replace the 39-byte GLES renderer prefix with a mobile renderer string;
  * jump over the append of the host renderer returned by the Windows driver.

It refuses unknown input hashes or unexpected instruction bytes so an Android
Emulator update cannot be silently patched at stale offsets.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

EXPECTED_SHA256 = "f84d3a277e1ecc380470e7ed988b79cf361e0cedf9d9ee9bf7ee188d90b04947"
VENDOR_OFFSET = 0xF758C0
ORIGINAL_VENDOR = b"Google ("
PERSONA_VENDOR = b"REPlayer"
VENDOR_INLINE_OFFSET = 0x17EB8B
ORIGINAL_VENDOR_INLINE = b"\x48\xb8" + ORIGINAL_VENDOR
PERSONA_VENDOR_INLINE = b"\x48\xb8" + PERSONA_VENDOR
VENDOR_APPEND_OFFSET = 0x17EB9E
ORIGINAL_VENDOR_APPEND = bytes.fromhex("49 8b 4d 10 49")
SKIP_VENDOR_APPEND = bytes.fromhex("e9 9d 00 00 00")
RENDERER_OFFSET = 0xF758D0
ORIGINAL_RENDERER = b"Android Emulator OpenGL ES Translator ("
PERSONA_RENDERER = b"REPlayer Virtual GPU (OpenGL ES 3.1 1.0"
APPEND_CODE_OFFSET = 0x17ED70
ORIGINAL_APPEND_CODE = bytes.fromhex("49 8b 4f 10 49 8b 47 18 48 89")
# Preserve `mov r13, [rsp+0x28]` for the following version-string code, then
# jump from 0x18017f975 to 0x18017f9c5 over only the base-renderer append.
SKIP_APPEND_CODE = bytes.fromhex("4c 8b 6c 24 28 e9 4b 00 00 00")
INLINE_TAIL_OFFSET = 0x17ED5B
ORIGINAL_INLINE_TAIL = bytes.fromhex("48 b8 73 6c 61 74 6f 72 20 28")
PERSONA_INLINE_TAIL = b"\x48\xb8" + PERSONA_RENDERER[-8:]
VERSION_SUFFIX_OFFSET = 0x17EF3A
ORIGINAL_VERSION_SUFFIX = bytes.fromhex("48 8b 46 10 48")
SKIP_VERSION_SUFFIX = bytes.fromhex("e9 ec 00 00 00")


def digest(data: bytes | bytearray) -> str:
    return hashlib.sha256(data).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--manifest", type=Path)
    args = parser.parse_args()

    data = bytearray(args.input.read_bytes())
    source_hash = digest(data)
    if source_hash != EXPECTED_SHA256:
        raise SystemExit(f"Unsupported libgfxstream_backend.dll SHA-256: {source_hash}")
    if len(PERSONA_RENDERER) != len(ORIGINAL_RENDERER):
        raise SystemExit("Renderer replacement must preserve the compiled constant length")
    if len(PERSONA_VENDOR) != len(ORIGINAL_VENDOR):
        raise SystemExit("Vendor replacement must preserve the compiled constant length")
    if data[RENDERER_OFFSET:RENDERER_OFFSET + len(ORIGINAL_RENDERER)] != ORIGINAL_RENDERER:
        raise SystemExit("Renderer constant does not match at the pinned offset")
    if data[VENDOR_OFFSET:VENDOR_OFFSET + len(ORIGINAL_VENDOR)] != ORIGINAL_VENDOR:
        raise SystemExit("Vendor constant does not match at the pinned offset")
    if data[VENDOR_INLINE_OFFSET:VENDOR_INLINE_OFFSET + len(ORIGINAL_VENDOR_INLINE)] != ORIGINAL_VENDOR_INLINE:
        raise SystemExit("Inlined vendor constant does not match at the pinned offset")
    if data[VENDOR_APPEND_OFFSET:VENDOR_APPEND_OFFSET + len(ORIGINAL_VENDOR_APPEND)] != ORIGINAL_VENDOR_APPEND:
        raise SystemExit("Vendor append code does not match at the pinned offset")
    if data[APPEND_CODE_OFFSET:APPEND_CODE_OFFSET + len(ORIGINAL_APPEND_CODE)] != ORIGINAL_APPEND_CODE:
        raise SystemExit("Renderer append code does not match at the pinned offset")
    if data[INLINE_TAIL_OFFSET:INLINE_TAIL_OFFSET + len(ORIGINAL_INLINE_TAIL)] != ORIGINAL_INLINE_TAIL:
        raise SystemExit("Inlined renderer tail does not match at the pinned offset")
    if data[VERSION_SUFFIX_OFFSET:VERSION_SUFFIX_OFFSET + len(ORIGINAL_VERSION_SUFFIX)] != ORIGINAL_VERSION_SUFFIX:
        raise SystemExit("Version suffix code does not match at the pinned offset")

    data[VENDOR_OFFSET:VENDOR_OFFSET + len(PERSONA_VENDOR)] = PERSONA_VENDOR
    data[VENDOR_INLINE_OFFSET:VENDOR_INLINE_OFFSET + len(PERSONA_VENDOR_INLINE)] = PERSONA_VENDOR_INLINE
    data[VENDOR_APPEND_OFFSET:VENDOR_APPEND_OFFSET + len(SKIP_VENDOR_APPEND)] = SKIP_VENDOR_APPEND
    data[RENDERER_OFFSET:RENDERER_OFFSET + len(PERSONA_RENDERER)] = PERSONA_RENDERER
    data[INLINE_TAIL_OFFSET:INLINE_TAIL_OFFSET + len(PERSONA_INLINE_TAIL)] = PERSONA_INLINE_TAIL
    data[APPEND_CODE_OFFSET:APPEND_CODE_OFFSET + len(SKIP_APPEND_CODE)] = SKIP_APPEND_CODE
    data[VERSION_SUFFIX_OFFSET:VERSION_SUFFIX_OFFSET + len(SKIP_VERSION_SUFFIX)] = SKIP_VERSION_SUFFIX
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(data)
    report = {
        "schema": 1,
        "emulatorVersion": "37.1.7",
        "emulatorBuildId": "15769812",
        "input": str(args.input),
        "inputSha256": source_hash,
        "output": str(args.output),
        "outputSha256": digest(data),
        "vendor": PERSONA_VENDOR.decode(),
        "renderer": PERSONA_RENDERER.decode() + ")",
        "patches": [
            {"offset": VENDOR_OFFSET, "length": len(PERSONA_VENDOR)},
            {"offset": VENDOR_INLINE_OFFSET, "before": ORIGINAL_VENDOR_INLINE.hex(), "after": PERSONA_VENDOR_INLINE.hex()},
            {"offset": VENDOR_APPEND_OFFSET, "before": ORIGINAL_VENDOR_APPEND.hex(), "after": SKIP_VENDOR_APPEND.hex()},
            {"offset": RENDERER_OFFSET, "length": len(PERSONA_RENDERER)},
            {"offset": INLINE_TAIL_OFFSET, "before": ORIGINAL_INLINE_TAIL.hex(), "after": PERSONA_INLINE_TAIL.hex()},
            {"offset": APPEND_CODE_OFFSET, "before": ORIGINAL_APPEND_CODE.hex(), "after": SKIP_APPEND_CODE.hex()},
            {"offset": VERSION_SUFFIX_OFFSET, "before": ORIGINAL_VERSION_SUFFIX.hex(), "after": SKIP_VERSION_SUFFIX.hex()},
        ],
    }
    if args.manifest:
        args.manifest.parent.mkdir(parents=True, exist_ok=True)
        args.manifest.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
