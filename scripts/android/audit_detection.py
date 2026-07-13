#!/usr/bin/env python3
"""Audit Android persona observables against legacy and modern emulator checks."""
from __future__ import annotations

import argparse
import json
import re
import subprocess
from dataclasses import asdict, dataclass
from pathlib import Path


@dataclass
class Finding:
    id: str
    detected: bool
    value: str
    source: str
    category: str
    fixability: str
    note: str


def run(argv: list[str], check: bool = True) -> str:
    result = subprocess.run(argv, capture_output=True, text=True, errors="replace")
    if check and result.returncode:
        raise RuntimeError((result.stderr or result.stdout).strip())
    return result.stdout.replace("\r", "").strip()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True)
    parser.add_argument("--serial", default="emulator-5554")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    def shell(command: str) -> str:
        return run([args.adb, "-s", args.serial, "shell", command], check=False)

    prop_names = [
        "ro.product.model", "ro.product.manufacturer", "ro.product.brand",
        "ro.product.device", "ro.product.name", "ro.build.fingerprint",
        "ro.build.type", "ro.build.tags", "ro.hardware", "ro.boot.hardware",
        "ro.bootloader", "ro.bootmode", "ro.kernel.qemu", "ro.boot.qemu",
        "ro.serialno", "ro.debuggable", "ro.secure", "ro.adb.secure",
        "ro.product.cpu.abilist", "ro.dalvik.vm.native.bridge",
        "gsm.operator.alpha", "gsm.operator.numeric", "gsm.sim.operator.numeric",
        "sys.usb.config", "persist.sys.usb.config",
    ]
    props = {}
    for line in shell("getprop").splitlines():
        match = re.match(r"\[([^]]+)\]: \[(.*)\]", line)
        if match:
            props[match.group(1)] = match.group(2)
    get = lambda name: props.get(name, "")

    paths = [
        "/dev/socket/qemud", "/dev/qemu_pipe", "/system/lib/libc_malloc_debug_qemu.so",
        "/sys/qemu_trace", "/system/bin/qemu-props", "/dev/socket/genyd",
        "/dev/socket/baseband_genyd", "/sys/hypervisor", "/dev/goldfish_pipe",
        "/dev/goldfish_sync",
    ]
    path_state = {}
    for path in paths:
        path_state[path] = shell(f"if [ -e '{path}' ]; then echo present; else echo absent; fi") == "present"

    cpuinfo = shell("cat /proc/cpuinfo")
    tty_drivers = shell("cat /proc/tty/drivers")
    packages = shell("pm list packages")
    interfaces = shell("ip -o -4 addr show")
    sf = shell("dumpsys SurfaceFlinger")
    proc_tcp = shell("cat /proc/net/tcp")
    dev_settings = shell("settings get global development_settings_enabled")
    adb_enabled = shell("settings get global adb_enabled")
    selinux = shell("getenforce")
    sensors = shell("dumpsys sensorservice")
    battery = shell("dumpsys battery")

    findings: list[Finding] = []
    add = findings.append
    build_blob = " ".join(get(x).lower() for x in (
        "ro.product.model", "ro.product.device", "ro.product.name", "ro.hardware",
        "ro.boot.hardware", "ro.build.fingerprint", "ro.serialno"))
    add(Finding("framgia.basic-build", any(token in build_blob for token in (
        "google_sdk", "android sdk built for x86", "emulator", "generic", "goldfish", "vbox86", "nox")),
        build_blob, "framgia EmulatorDetector.java:226-249", "build", "image-overlay",
        "Legacy Build-field test."))
    legacy_prop_hits = []
    legacy_props = {
        "ro.bootloader": "unknown", "ro.bootmode": "unknown", "ro.hardware": "goldfish",
        "ro.kernel.qemu": "1", "ro.product.device": "generic",
        "ro.product.model": "sdk", "ro.product.name": "sdk",
    }
    for name, token in legacy_props.items():
        if token in get(name):
            legacy_prop_hits.append(f"{name}={get(name)}")
    for name in ("init.svc.qemud", "init.svc.qemu-props", "qemu.hw.mainkeys",
                 "qemu.sf.fake_camera", "qemu.sf.lcd_density", "ro.kernel.android.qemud",
                 "ro.kernel.qemu.gles", "ro.serialno"):
        if get(name):
            legacy_prop_hits.append(f"{name}={get(name)}")
    add(Finding("legacy.qemu-properties", len(legacy_prop_hits) >= 5,
        "; ".join(legacy_prop_hits), "framgia:99-115,394-413; strazzere FindEmulator.java:47-61,261-281",
        "properties", "property-service", "Reports the intended threshold semantics; old code may count empty strings as present."))
    old_files = [p for p in paths[:7] if path_state[p]]
    add(Finding("legacy.qemu-files", bool(old_files), ", ".join(old_files),
        "framgia:65-97,358-391; strazzere:38-42,78-149", "filesystem", "kernel-or-mount-namespace",
        "Legacy QEMU/Genymotion file and pipe checks."))
    goldfish_driver = "goldfish" in (cpuinfo + tty_drivers).lower()
    add(Finding("legacy.goldfish-driver", goldfish_driver, "goldfish" if goldfish_driver else "absent",
        "framgia:70,358-380; strazzere:42,126-149", "kernel", "kernel-build",
        "Checks first 1 KiB of cpuinfo and tty drivers."))
    old_packages = [x for x in ("com.google.android.launcher.layouts.genymotion", "com.bluestacks", "com.bignox.app") if x in packages]
    add(Finding("framgia.known-packages", bool(old_packages), ", ".join(old_packages),
        "framgia EmulatorDetector.java:141-144,264-278", "packages", "baseline",
        "Legacy launcher/vendor package list."))
    ip_hit = "10.0.2.15" in interfaces
    add(Finding("framgia.default-ip", ip_hit, interfaces,
        "framgia EmulatorDetector.java:117,416-456", "network", "network-topology",
        "Legacy detector uses netcfg; modern apps can query interfaces directly."))

    modern_tokens = ("sdk_gphone", "emu64", "ranchu", "emulator")
    modern_build = any(token in build_blob for token in modern_tokens)
    add(Finding("modern.product-identity", modern_build, build_blob,
        "REPlayer modern compatibility audit", "build", "image-overlay",
        "API 34 official-emulator product/model/device/hardware markers."))
    fp = get("ro.build.fingerprint")
    add(Finding("modern.debug-build", get("ro.debuggable") == "1" or get("ro.build.type") != "user" or "dev-keys" in get("ro.build.tags"),
        f"fingerprint={fp}; type={get('ro.build.type')}; tags={get('ro.build.tags')}; debuggable={get('ro.debuggable')}",
        "REPlayer modern compatibility audit", "debug", "separate-user-image",
        "A stock-observation persona must not expose a userdebug/dev-keys/debuggable build."))
    qemu_props = {name: get(name) for name in ("ro.kernel.qemu", "ro.boot.qemu") if get(name)}
    add(Finding("modern.qemu-properties", any(value == "1" for value in qemu_props.values()), json.dumps(qemu_props),
        "REPlayer modern compatibility audit", "properties", "property-service",
        "Kernel/boot QEMU properties are stronger than a changed Settings label."))
    add(Finding("modern.emulator-serial", get("ro.serialno").upper().startswith("EMULATOR"), get("ro.serialno"),
        "REPlayer modern compatibility audit", "identity", "boot-chain",
        "Serial is initialized before normal post-boot settings."))
    abi = get("ro.product.cpu.abilist")
    bridge = get("ro.dalvik.vm.native.bridge")
    add(Finding("modern.x86-native-bridge", "x86" in abi or bool(bridge), f"abi={abi}; bridge={bridge}",
        "REPlayer modern compatibility audit", "architecture", "physical-arm-lane",
        "The neutral REPlayer persona intentionally exposes x86 WHPX execution and does not claim physical ARM hardware."))
    modern_files = [p for p in ("/sys/hypervisor", "/dev/goldfish_pipe", "/dev/goldfish_sync") if path_state[p]]
    add(Finding("modern.hypervisor-devices", bool(modern_files), ", ".join(modern_files),
        "REPlayer modern compatibility audit", "kernel", "kernel-or-mount-namespace",
        "Guest-visible hypervisor/goldfish nodes."))
    emulator_packages = [line.removeprefix("package:") for line in packages.splitlines() if ".emulator" in line.lower()]
    add(Finding("modern.emulator-packages", bool(emulator_packages), ", ".join(emulator_packages),
        "REPlayer modern compatibility audit", "packages", "baseline-or-package-visibility",
        "Official image packages with emulator in their package names."))
    host_cpu = "intel" in cpuinfo.lower() or "amd" in cpuinfo.lower() or " hypervisor" in cpuinfo.lower()
    cpu_summary = next((line for line in cpuinfo.splitlines() if line.lower().startswith("model name")), "")
    add(Finding("modern.host-cpu", host_cpu, cpu_summary,
        "REPlayer modern compatibility audit", "architecture", "physical-arm-lane",
        "CPUID/procfs exposes the x86 host and hypervisor."))
    renderer_lines = [line.strip() for line in sf.splitlines() if re.search(r"GLES|OpenGL|Vulkan|ANGLE|SwiftShader|Emulator", line, re.I)]
    renderer_value = " | ".join(renderer_lines[:8])
    add(Finding("modern.renderer", bool(re.search(r"emulator|translator|swiftshader|gfxstream", renderer_value, re.I)), renderer_value,
        "REPlayer modern compatibility audit", "graphics", "renderer-stack",
        "GL/Vulkan renderer strings can reveal virtualization."))
    debug_visible = dev_settings == "1" or adb_enabled == "1" or "adb" in get("sys.usb.config") or selinux.lower() != "enforcing"
    add(Finding("modern.debug-state", debug_visible,
        f"development={dev_settings}; adb={adb_enabled}; usb={get('sys.usb.config')}; selinux={selinux}",
        "strazzere FindDebugger.java plus REPlayer modern audit", "debug", "separate-stock-persona",
        "Stock observation must not expose development mode, permissive SELinux, Frida, or debugger attachment."))
    suspicious_packages = [line.removeprefix("package:") for line in packages.splitlines() if re.search(r"frida|magisk|supersu|xposed|substrate", line, re.I)]
    add(Finding("modern.instrumentation-packages", bool(suspicious_packages), ", ".join(suspicious_packages),
        "REPlayer modern compatibility audit", "instrumentation", "separate-stock-persona",
        "Package-name check only; native process/socket scans require per-process testing."))
    add(Finding("strazzere.adb-tcp-shape", False, proc_tcp[:2000],
        "strazzere FindDebugger.java:66-107", "network", "per-app-runtime-test",
        "Raw data captured; original parser is obsolete and should be executed inside a detector APK for a verdict."))
    sensor_count = len(re.findall(r"^\s*0x[0-9a-f]+\)", sensors, re.M | re.I))
    add(Finding("modern.sensor-fidelity", sensor_count < 3, f"enumerated={sensor_count}",
        "REPlayer modern compatibility audit", "sensors", "persona-hardware-or-physical-lane",
        "No/constant sensors are behaviorally detectable even when Build fields look physical."))
    add(Finding("modern.static-battery", False, battery[:1000],
        "REPlayer modern compatibility audit", "behavior", "persona-simulation",
        "Captured for time-series comparison; a single sample is not sufficient to flag static behavior."))

    detected = [item for item in findings if item.detected]
    output = {
        "serial": args.serial,
        "properties": {name: get(name) for name in prop_names},
        "summary": {"checks": len(findings), "detected": len(detected), "clear": len(findings) - len(detected)},
        "findings": [asdict(item) for item in findings],
        "limitations": [
            "Passing this matrix does not prove universal emulator indistinguishability.",
            "Timing, sensor, baseband, Play Integrity, hardware attestation, native CPUID, and delayed DLC checks can still distinguish virtualization.",
            "DLCDroid is used as a requirement to re-run checks after dynamically loaded code; it is not itself a detector signature list.",
        ],
    }
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.with_suffix(".json").write_text(json.dumps(output, indent=2), encoding="utf-8")
    lines = ["# Android persona detector audit", "", f"- Checks: **{len(findings)}**", f"- Detected: **{len(detected)}**", "", "| Check | Verdict | Category | Value | Fixability |", "|---|---:|---|---|---|"]
    for item in findings:
        value = item.value.replace("|", "\\|").replace("\n", " ")[:180]
        lines.append(f"| `{item.id}` | {'DETECTED' if item.detected else 'clear'} | {item.category} | {value} | {item.fixability} |")
    lines += ["", "## Limitations", ""] + [f"- {text}" for text in output["limitations"]]
    out.with_suffix(".md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps(output["summary"]))
    return 1 if detected else 0


if __name__ == "__main__":
    raise SystemExit(main())
