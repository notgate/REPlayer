#!/usr/bin/env python3
"""Publish and verify the clean-room revm-render-host native export table.

The WPF renderer controller can optionally bind the C ABI only when a native/AOT
artifact exports the expected revm_renderer_* symbols. This probe publishes the
managed contract shim as a temporary Windows NativeAOT shared library outside the
repository, parses the PE export table directly, and fails unless the deterministic
renderer lifecycle ABI is present. It does not add or exercise scrcpy, ADB display,
SDL reparenting, encoded video, or proprietary renderer code.
"""

from __future__ import annotations

import shutil
import struct
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOTNET = Path("/mnt/c/Program Files/dotnet/dotnet.exe")
TMP_ROOT = Path("/mnt/d/Downloads/revm-render-host-native-export-probe-tmp")
PROJECT = ROOT / "runtime-src" / "RevmRenderHost" / "RevmRenderHost.csproj"
OUTPUT_DLL = TMP_ROOT / "revm-render-host.dll"
EXPECTED_EXPORTS = {
    "revm_renderer_create",
    "revm_renderer_attach_transport",
    "revm_renderer_resize",
    "revm_renderer_set_rotation",
    "revm_renderer_present_latest_frame",
    "revm_renderer_destroy",
}


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


def run(cmd: list[str]) -> None:
    print("+", " ".join(cmd))
    subprocess.run(cmd, check=True)


def u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def c_string(data: bytes, offset: int) -> str:
    end = data.index(b"\0", offset)
    return data[offset:end].decode("ascii")


class PeExportReader:
    def __init__(self, path: Path) -> None:
        self.path = path
        self.data = path.read_bytes()
        self.sections: list[tuple[int, int, int, int]] = []
        self.export_rva = 0
        self._parse_headers()

    def _parse_headers(self) -> None:
        if self.data[:2] != b"MZ":
            raise ValueError(f"{self.path} is not a PE image: missing MZ header")

        pe_offset = u32(self.data, 0x3C)
        if self.data[pe_offset : pe_offset + 4] != b"PE\0\0":
            raise ValueError(f"{self.path} is not a PE image: missing PE signature")

        file_header = pe_offset + 4
        section_count = u16(self.data, file_header + 2)
        optional_size = u16(self.data, file_header + 16)
        optional = file_header + 20
        magic = u16(self.data, optional)
        if magic == 0x10B:
            data_directory = optional + 96
        elif magic == 0x20B:
            data_directory = optional + 112
        else:
            raise ValueError(f"{self.path} has unsupported PE optional-header magic 0x{magic:x}")

        self.export_rva = u32(self.data, data_directory)
        section_table = optional + optional_size
        for index in range(section_count):
            section = section_table + index * 40
            virtual_size = u32(self.data, section + 8)
            virtual_address = u32(self.data, section + 12)
            raw_size = u32(self.data, section + 16)
            raw_pointer = u32(self.data, section + 20)
            self.sections.append((virtual_address, max(virtual_size, raw_size), raw_pointer, raw_size))

    def rva_to_offset(self, rva: int) -> int:
        for virtual_address, virtual_size, raw_pointer, raw_size in self.sections:
            if virtual_address <= rva < virtual_address + virtual_size:
                delta = rva - virtual_address
                if delta >= raw_size:
                    raise ValueError(f"RVA 0x{rva:x} maps past raw section data in {self.path}")
                return raw_pointer + delta
        raise ValueError(f"RVA 0x{rva:x} does not map to a section in {self.path}")

    def exports(self) -> set[str]:
        if self.export_rva == 0:
            return set()

        export_dir = self.rva_to_offset(self.export_rva)
        name_count = u32(self.data, export_dir + 24)
        names_rva = u32(self.data, export_dir + 32)
        names_offset = self.rva_to_offset(names_rva)
        exports: set[str] = set()
        for index in range(name_count):
            name_rva = u32(self.data, names_offset + index * 4)
            exports.add(c_string(self.data, self.rva_to_offset(name_rva)))
        return exports


def main() -> int:
    if not DOTNET.exists():
        print(f"error: dotnet not found at {DOTNET}", file=sys.stderr)
        return 2
    if not PROJECT.exists():
        print(f"error: render-host project not found at {PROJECT}", file=sys.stderr)
        return 2

    if TMP_ROOT.exists():
        shutil.rmtree(TMP_ROOT)
    TMP_ROOT.mkdir(parents=True)

    try:
        run(
            [
                str(DOTNET),
                "publish",
                win_path(PROJECT),
                "-c",
                "Release",
                "-r",
                "win-x64",
                "--self-contained",
                "true",
                "-p:PublishAot=true",
                "-p:NativeLib=Shared",
                "-p:OutputType=Library",
                "-o",
                win_path(TMP_ROOT),
            ]
        )

        if not OUTPUT_DLL.exists():
            print(f"error: expected NativeAOT DLL was not produced: {OUTPUT_DLL}", file=sys.stderr)
            return 1

        exports = PeExportReader(OUTPUT_DLL).exports()
        missing = sorted(EXPECTED_EXPORTS - exports)
        unexpected_absence_context = ", ".join(sorted(exports)) if exports else "<none>"
        if missing:
            print(f"error: missing renderer ABI exports: {', '.join(missing)}", file=sys.stderr)
            print(f"exports found: {unexpected_absence_context}", file=sys.stderr)
            return 1

        print("render-host NativeAOT export probe passed")
        print("verified exports:")
        for name in sorted(EXPECTED_EXPORTS):
            print(f"  {name}")
        return 0
    finally:
        shutil.rmtree(TMP_ROOT, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
