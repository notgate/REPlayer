#!/usr/bin/env python3
"""Regression harness for revm-vmm --validate-manifest-only.

Creates a minimal throwaway revm-engine package with dummy component files and
verifies deterministic package/manifest acceptance and rejection for the native
renderer backend contract. The cases intentionally cover banned display
transports (scrcpy/ADB/SDL/encoded video) as validation only; no display path is
implemented here.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Final

REPO_ROOT: Final[Path] = Path(__file__).resolve().parents[3]
DEFAULT_VMM: Final[Path] = REPO_ROOT / "runtime-src" / "RevmVmm" / "bin" / "Release" / "net9.0-windows" / "revm-vmm.exe"
DOTNET_EXE: Final[Path] = Path("/mnt/c/Program Files/dotnet/dotnet.exe")
RUNTIME_SOLUTION: Final[str] = subprocess.run(
    ["wslpath", "-w", str(REPO_ROOT / "runtime-src" / "RevmRuntime.sln")],
    text=True, capture_output=True, check=True
).stdout.strip()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vmm", type=Path, default=DEFAULT_VMM, help="Path to built revm-vmm.exe.")
    parser.add_argument("--keep", action="store_true", help="Keep generated manifest packages for inspection.")
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
        raise FileNotFoundError(f"revm-vmm.exe not found at {vmm}; run the runtime Release build or pass --vmm.")
    return vmm


def to_windows_path(path: Path) -> str:
    resolved = path.resolve()
    parts = resolved.parts
    if len(parts) >= 4 and parts[0] == "/" and parts[1] == "mnt" and len(parts[2]) == 1:
        drive = parts[2].upper()
        return drive + ":\\" + "\\".join(parts[3:])
    return str(resolved)


def base_manifest(**overrides: object) -> dict[str, object]:
    manifest: dict[str, object] = {
        "runtimeVersion": "regression-test",
        "androidArch": "x86_64",
        "engineKind": "revm-managed-bootstrap",
        "vmmPath": "bin/revm-vmm.exe",
        "rendererPath": "bin/revm-render-host.dll",
        "systemImage": "images/android-x86_64-system.img",
        "dataImageTemplate": "images/android-x86_64-data-template.img",
        "guestAgentApk": "guest/revm-agent.apk",
        "adbMode": "tcp",
        "displayMode": "native-hwnd-shm-ring",
    }
    manifest.update(overrides)
    return manifest


def create_engine_package(root: Path, manifest: dict[str, object]) -> Path:
    engine = root / "runtime" / "revm-engine"
    config = engine / "config"
    for rel in (
        "bin/revm-vmm.exe",
        "bin/revm-render-host.dll",
        "images/android-x86_64-system.img",
        "images/android-x86_64-data-template.img",
        "guest/revm-agent.apk",
    ):
        target = engine / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(b"placeholder")
    config.mkdir(parents=True, exist_ok=True)
    manifest_path = config / "runtime.json"
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return manifest_path


def run_json(cmd: list[str], cwd: Path) -> tuple[int, list[dict[str, object]], str]:
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
    return int(result.returncode), events, result.stderr


def event_types(events: list[dict[str, object]]) -> list[str]:
    return [str(event.get("Type", "")) for event in events if "Type" in event]


def event_messages(events: list[dict[str, object]]) -> str:
    return "\n".join(str(event.get("Message", "")) for event in events if "Message" in event)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> int:
    args = parse_args()
    vmm = ensure_vmm(args.vmm, args.build_if_missing)
    temp_parent = REPO_ROOT / ".tmp"
    temp_parent.mkdir(exist_ok=True)
    temp_root = Path(tempfile.mkdtemp(prefix="revm-manifest-probe-", dir=temp_parent))
    try:
        cases = [
            ("valid-shm-ring", base_manifest(), 0, "runtime.manifest.ready", "native-hwnd-shm-ring"),
            (
                "valid-virtio-gpu-gfxstream",
                base_manifest(engineKind="revm-gfxstream", displayMode="native-hwnd-virtio-gpu-gfxstream"),
                0,
                "runtime.manifest.ready",
                "native-hwnd-virtio-gpu-gfxstream",
            ),
            ("scrcpy-display", base_manifest(displayMode="scrcpy"), 2, "runtime.manifest.invalid", "scrcpy"),
            ("adb-display", base_manifest(displayMode="adb-display"), 2, "runtime.manifest.invalid", "adb"),
            ("sdl-display", base_manifest(displayMode="sdl-reparent"), 2, "runtime.manifest.invalid", "sdl"),
            ("encoded-video-display", base_manifest(displayMode="h264-video-decoder"), 2, "runtime.manifest.invalid", "video"),
            ("webrtc-display", base_manifest(displayMode="native-hwnd-webrtc"), 2, "runtime.manifest.invalid", "webrtc"),
            ("escaping-renderer", base_manifest(rendererPath="../outside-renderer.dll"), 2, "runtime.manifest.invalid", "escapes"),
            ("bad-adb-mode", base_manifest(adbMode="display"), 2, "runtime.manifest.invalid", "control/readiness"),
            ("bad-engine", base_manifest(engineKind="external-emulator"), 2, "runtime.manifest.invalid", "engineKind"),
        ]

        report: list[dict[str, object]] = []
        for name, manifest, expected_exit, expected_event, expected_message in cases:
            case_root = temp_root / name
            manifest_path = create_engine_package(case_root, manifest)
            log_dir = case_root / "logs"
            cmd = [
                str(vmm),
                "--validate-manifest-only",
                "--manifest",
                to_windows_path(manifest_path),
                "--log-dir",
                to_windows_path(log_dir),
            ]
            exit_code, events, stderr = run_json(cmd, REPO_ROOT)
            types = event_types(events)
            messages = event_messages(events)
            require(exit_code == expected_exit, f"{name}: exit {exit_code}, expected {expected_exit}; stderr={stderr}; events={types}")
            require(expected_event in types, f"{name}: missing event {expected_event}; events={types}")
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
