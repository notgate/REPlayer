#!/usr/bin/env python3
"""Bounded live acceptance audit for the REPlayer API 34 release-observation lane."""
from __future__ import annotations

import argparse
import json
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path


def run(command: list[str], timeout: int = 8, check: bool = True) -> str:
    completed = subprocess.run(command, text=True, capture_output=True, timeout=timeout)
    output = (completed.stdout + completed.stderr).replace("\r", "").strip()
    if check and completed.returncode != 0:
        raise RuntimeError(f"Command failed ({completed.returncode}): {' '.join(command)}\n{output}")
    return output


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--serial", default="emulator-5554")
    parser.add_argument("--sample-seconds", type=int, default=60)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    adb = str(root / "runtime/google-emulator/sdk/platform-tools/adb.exe")
    powershell = "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"

    def adb_run(*items: str, check: bool = True, timeout: int = 8) -> str:
        return run([adb, "-s", args.serial, *items], timeout=timeout, check=check)

    def prop(name: str) -> str:
        return adb_run("shell", "getprop", name)

    case_dirs = sorted((root / "runtime/cases").glob("*/*"), key=lambda path: path.stat().st_mtime, reverse=True)
    require(bool(case_dirs), "No case directory found")
    case_dir = case_dirs[0]
    manifest = json.loads((case_dir / "manifest.json").read_text())
    require(manifest["Profile"] == "api34-persona", "Latest case is not api34-persona")
    require(manifest["Failure"] is None, "Latest case already records a failure")
    require(manifest["PersonaVerification"]["success"] is True, "Persona verification did not pass")
    require(manifest["PersonaVerification"]["errors"] == [], "Persona verification retained errors")

    process_script = r"""
$rp=Get-Process REPlayer -ErrorAction Stop
$all=Get-CimInstance Win32_Process
$selected=$all|Where-Object{$_.Name -in @('REPlayer.exe','emulator.exe','qemu-system-x86_64.exe')}|ForEach-Object{[pscustomobject]@{pid=$_.ProcessId;parent=$_.ParentProcessId;name=$_.Name;path=$_.ExecutablePath;commandLine=$_.CommandLine}}
[pscustomobject]@{replayer=[pscustomobject]@{pid=$rp.Id;responding=$rp.Responding;path=$rp.Path};processes=@($selected)}|ConvertTo-Json -Depth 5 -Compress
"""

    def processes() -> dict:
        return json.loads(run([powershell, "-NoProfile", "-Command", process_script], timeout=10))

    process_first = processes()
    require(process_first["replayer"]["responding"] is True, "REPlayer is not responding")
    require("bin\\Release\\net9.0-windows\\REPlayer.exe" in process_first["replayer"]["path"], "REPlayer is not the Release executable")
    process_items = process_first["processes"] if isinstance(process_first["processes"], list) else [process_first["processes"]]
    qemus = [item for item in process_items if item["name"].lower() == "qemu-system-x86_64.exe"]
    emulators = [item for item in process_items if item["name"].lower() == "emulator.exe"]
    require(len(qemus) == 1, f"Expected one QEMU process, found {len(qemus)}")
    require(bool(emulators), "No emulator launcher process found")
    qemu_first = qemus[0]
    require(any(item["pid"] == qemu_first["parent"] for item in emulators), "QEMU is not parented by emulator.exe")
    require("persona-emulator-37.1.7" in (qemu_first["path"] or ""), "QEMU is not from the staged 37.1.7 runtime")
    require(all("persona-emulator-37.1.7" in (item["path"] or "") for item in emulators), "Emulator launcher is not from the staged runtime")

    state = adb_run("get-state")
    require(state == "device", f"ADB state is {state!r}")
    require(prop("sys.boot_completed") == "1", "Android boot is not complete")
    properties = {
        name: prop(name) for name in [
            "ro.product.model", "ro.product.device", "ro.build.fingerprint", "ro.build.type",
            "ro.build.tags", "ro.debuggable", "ro.secure", "ro.adb.secure",
            "ro.product.cpu.abilist", "ro.hardware.egl", "ro.opengles.version",
        ]
    }
    require(properties["ro.product.model"] == "REPlayer Virtual Device" and
            properties["ro.product.device"] == "replayer_x86_64", "Product identity mismatch")
    require(properties["ro.build.type"] == "user" and properties["ro.build.tags"] == "release-keys", "Release build properties mismatch")
    require(properties["ro.debuggable"] == "0" and properties["ro.secure"] == "1" and properties["ro.adb.secure"] == "1", "Release security properties mismatch")
    require(properties["ro.product.cpu.abilist"].startswith("x86_64,"), "x86_64 is not the first ABI")

    root_response = adb_run("root", check=False)
    shell_uid = adb_run("shell", "id", "-u")
    require("cannot run as root" in root_response and shell_uid != "0", "Release ADB root denial failed")

    required_services = ["package", "settings", "window", "wallpaper"]
    services = {name: adb_run("shell", "service", "check", name) for name in required_services}
    require(all("found" in value for value in services.values()), "Required Android service is unavailable")
    home = adb_run("shell", "cmd", "package", "resolve-activity", "--brief", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.HOME")
    require("com.google.android.apps.nexuslauncher/.NexusLauncherActivity" in home, "Nexus Launcher is not HOME")
    require("com.replayer.utility" not in home, "Utility package incorrectly owns HOME")
    utility_path = adb_run("shell", "pm", "path", "com.replayer.utility")
    require(utility_path.startswith("package:"), "Customizer package is missing")
    overlay = adb_run("shell", "cmd", "overlay", "list")
    overlay_lookup = adb_run("shell", "cmd", "overlay", "lookup", "com.android.settings", "com.android.settings:string/about_settings")
    require("[x] com.replayer.settings.overlay" in overlay and "About phone" in overlay_lookup, "Settings overlay is not active")
    theme = adb_run("shell", "settings", "get", "secure", "theme_customization_overlay_packages")
    require("MONOCHROMATIC" in theme, "Monochromatic theme state is absent")
    night_service = adb_run("shell", "cmd", "uimode", "night")
    night_setting = adb_run("shell", "settings", "get", "secure", "ui_night_mode")
    night_dump = adb_run("shell", "dumpsys", "uimode")
    require(night_service == "Night mode: yes" and night_setting == "2" and
            "mComputedNightMode=true" in night_dump and "mCurUiMode=0x21" in night_dump,
            "Persistent Android dark mode is not active")
    installed = set(adb_run("shell", "pm", "list", "packages").splitlines())
    disabled = set(adb_run("shell", "pm", "list", "packages", "-d").splitlines())
    expected_disabled = [line.strip() for line in (root / "android-tools/replayer-customizer/baseline-disabled-packages.txt").read_text().splitlines() if line.strip() and not line.lstrip().startswith("#")]
    installed_policy = [name for name in expected_disabled if f"package:{name}" in installed]
    absent_policy = [name for name in expected_disabled if f"package:{name}" not in installed]
    missing_disabled = [name for name in installed_policy if f"package:{name}" not in disabled]
    require(not missing_disabled, "Package policy missing disabled packages: " + ", ".join(missing_disabled))

    wm_size = adb_run("shell", "wm", "size")
    wm_density = adb_run("shell", "wm", "density")
    require("Physical size: 1080x2400" in wm_size, "Release physical size is not 1080x2400")
    require("Physical density: 420" in wm_density and "Override density" not in wm_density, "Release density is not clean 420 DPI")
    surface = adb_run("shell", "dumpsys", "SurfaceFlinger", timeout=20)
    surface_lines = [line.strip() for line in surface.splitlines() if "GLES:" in line or "OpenGL ES" in line]
    surface_evidence = "\n".join(surface_lines[:8])
    require("REPlayer" in surface_evidence and "REPlayer Virtual GPU" in surface_evidence,
            "Neutral transformed guest GLES identity is absent")

    system_first = adb_run("shell", "pidof", "system_server")
    network_first = adb_run("shell", "pidof", "com.android.networkstack.process")
    started = datetime.now(timezone.utc)
    require(bool(system_first and network_first), "Framework PID sample is empty")
    time.sleep(args.sample_seconds)
    system_last = adb_run("shell", "pidof", "system_server")
    network_last = adb_run("shell", "pidof", "com.android.networkstack.process")
    process_last = processes()
    ended = datetime.now(timezone.utc)
    process_last_items = process_last["processes"] if isinstance(process_last["processes"], list) else [process_last["processes"]]
    qemu_last = [item for item in process_last_items if item["name"].lower() == "qemu-system-x86_64.exe"]
    require(system_first == system_last, "system_server PID changed")
    require(network_first == network_last, "NetworkStack PID changed")
    require(len(qemu_last) == 1 and qemu_last[0]["pid"] == qemu_first["pid"], "QEMU PID changed")
    require(process_last["replayer"]["responding"] is True, "REPlayer became unresponsive")

    runtime_log = (root / "logs/google-emulator-runtime.log").read_text(errors="replace")
    marker = f"Case {manifest['CaseId']}" if f"Case {manifest['CaseId']}" in runtime_log else "StartInstanceWithDebug begin"
    segment = runtime_log[runtime_log.rfind(marker):]
    require("WHPX(10.0.26200) is installed and usable" in segment, "WHPX usability evidence is absent")
    require("100% Android ready: native gfxstream emulator window attached" in segment, "Native gfxstream attachment evidence is absent")
    require("Security lane accepted: api34-persona, ro.debuggable=0" in segment, "Release security lane log evidence is absent")
    require("FATAL" not in segment, "FATAL runtime diagnostic found")

    report = {
        "schema": 1,
        "phase": 2,
        "verdict": "PASS",
        "caseId": manifest["CaseId"],
        "sample": {
            "startedAtUtc": started.isoformat(),
            "endedAtUtc": ended.isoformat(),
            "minimumSeconds": args.sample_seconds,
            "qemuPidFirst": qemu_first["pid"],
            "qemuPidLast": qemu_last[0]["pid"],
            "systemServerFirst": system_first,
            "systemServerLast": system_last,
            "networkStackFirst": network_first,
            "networkStackLast": network_last,
        },
        "replayer": process_last["replayer"],
        "processTree": process_last_items,
        "adbState": state,
        "properties": properties,
        "adbRoot": {"response": root_response, "shellUid": shell_uid},
        "services": services,
        "home": home,
        "utilityPackage": utility_path,
        "overlayLookup": overlay_lookup,
        "darkMode": {"service": night_service, "secureUiNightMode": night_setting, "configuration": "0x21"},
        "packagePolicy": {
            "declaredPackages": len(expected_disabled),
            "installedPackages": len(installed_policy),
            "verifiedDisabledPackages": len(installed_policy),
            "absentPackages": absent_policy,
        },
        "display": {"wmSize": wm_size, "wmDensity": wm_density},
        "surfaceFlinger": surface_evidence,
        "personaVerification": manifest["PersonaVerification"],
        "runtime": {"whpx": True, "nativeGfxstreamAttached": True, "fatalDiagnostics": False},
    }
    rendered = json.dumps(report, indent=2) + "\n"
    output = args.output or (root / "runtime/validation/phase2-runtime.json")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(rendered)
    print(rendered, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
