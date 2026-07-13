#!/usr/bin/env python3
"""
REPlayer official-emulator game performance matrix.

Runs controlled Android Emulator launch scenarios and collects:
- ADB/boot timing
- detected/launched game package
- dumpsys gfxinfo framestats summary
- optional SurfaceFlinger layer latency summary
- host emulator/qemu process CPU and working-set samples
- raw logs/artifacts per scenario

Default target package detection looks for Subway Surfers package names containing:
subway, kiloo, or sybo. Pass --package to override.
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import re
import shutil
import statistics
import subprocess
import sys
import time
from dataclasses import dataclass, asdict
from datetime import datetime
from pathlib import Path
from typing import Iterable

ROOT = Path(__file__).resolve().parents[3]
SDK = ROOT / "runtime" / "google-emulator" / "sdk"
EMULATOR = ROOT / "runtime" / "google-emulator" / "persona-emulator-37.1.7" / "emulator.exe"
ADB = SDK / "platform-tools" / "adb.exe"
AVD_HOME = ROOT / "runtime" / "google-emulator" / "avd-home"
AVD = "ReVM"

SUBWAY_HINTS = ("subway", "kiloo", "sybo")


@dataclass
class Scenario:
    name: str
    renderer: str
    cores: int
    ram_mb: int
    width: int
    height: int
    dpi: int = 240
    fps: int = 60
    gpu_mode: str = "host"
    systemui_renderer: str | None = None
    vsync_rate: int | None = None
    device_profile: str | None = None
    guest_profile: str = "baseline"
    gltransport_profile: str = "current"
    emulator_features: tuple[str, ...] = ()


SCENARIOS = [
    Scenario("A_3c_3gb_opengl_540x960", "opengl", 3, 3072, 540, 960),
    Scenario("B_2c_3gb_opengl_540x960", "opengl", 2, 3072, 540, 960),
    Scenario("C_3c_3gb_host_540x960", "host", 3, 3072, 540, 960),
    Scenario("D_3c_3gb_opengl_480x854", "opengl", 3, 3072, 480, 854),
    Scenario("E_3c_3gb_opengl_432x768", "opengl", 3, 3072, 432, 768),
    Scenario("F_3c_3gb_opengl_360x640", "opengl", 3, 3072, 360, 640),
    Scenario("G_3c_3gb_opengl_540x960_120hz", "opengl", 3, 3072, 540, 960, fps=120),
    Scenario("H_4c_3gb_opengl_540x960_120hz", "opengl", 4, 3072, 540, 960, fps=120),
    Scenario("I_3c_3gb_host_540x960_120hz", "host", 3, 3072, 540, 960, fps=120),
    Scenario("J_3c_3gb_opengl_540x960_120hz_vsync", "opengl", 3, 3072, 540, 960, fps=120, vsync_rate=120),
    Scenario("K_3c_3gb_opengl_540x960_skiagl", "opengl", 3, 3072, 540, 960, systemui_renderer="skiagl"),
    Scenario("L_3c_3gb_opengl_540x960_skiavk", "opengl", 3, 3072, 540, 960, systemui_renderer="skiavk"),
    Scenario("M_3c_3gb_auto_540x960", "host", 3, 3072, 540, 960, gpu_mode="auto"),
    Scenario("N_3c_3gb_swangle_540x960", "host", 3, 3072, 540, 960, gpu_mode="swangle"),
    Scenario("O_3c_3gb_opengl_540x960_120hz_skiagl", "opengl", 3, 3072, 540, 960, fps=120, systemui_renderer="skiagl"),
    Scenario("P_3c_4gb_opengl_540x960_240hz_skiagl", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl"),
    Scenario("Q_rog5_3c_4gb_opengl_240hz_skiagl", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl", device_profile="rog5"),
    Scenario("R_oneplus8t_3c_4gb_opengl_240hz_skiagl", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl", device_profile="oneplus8t"),
    Scenario("S_pixel8pro_3c_4gb_opengl_240hz_skiagl", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl", device_profile="pixel8pro"),
    Scenario("T0_baseline_current_asg", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl", guest_profile="baseline", gltransport_profile="current"),
    Scenario("T1_lean_current_asg", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl", guest_profile="lean", gltransport_profile="current"),
    Scenario("T2_lean_low_latency_asg", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl", guest_profile="lean", gltransport_profile="low-latency"),
    Scenario("T3_lean_throughput_asg", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl", guest_profile="lean", gltransport_profile="throughput"),
    Scenario("T4_lean_virtio_native_sync", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl", guest_profile="lean", gltransport_profile="current", emulator_features=("VirtioGpuNativeSync",)),
    Scenario("T5_lean_gl_direct_mem", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl", guest_profile="lean", gltransport_profile="current", emulator_features=("GLDirectMem",)),
    Scenario("T6_lean_throughput_gl_direct_mem", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl", guest_profile="lean", gltransport_profile="throughput", emulator_features=("GLDirectMem",)),
    Scenario("T7_standard_fixed_performance", "opengl", 3, 4096, 540, 960, fps=240, systemui_renderer="skiagl", guest_profile="performance", gltransport_profile="current"),
]


def win_path(path: Path) -> str:
    s = str(path)
    if s.startswith("/mnt/") and len(s) > 6:
        drive = s[5].upper()
        rest = s[7:].replace("/", "\\")
        return f"{drive}:\\{rest}"
    return s


def run(cmd: list[str], timeout: int = 30, cwd: Path | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(cmd, cwd=str(cwd) if cwd else None, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)


def capture(path: Path, title: str, cp: subprocess.CompletedProcess[str]) -> str:
    text = f"# {title}\n# exit={cp.returncode}\n--- stdout ---\n{cp.stdout}\n--- stderr ---\n{cp.stderr}\n"
    path.write_text(text, encoding="utf-8", errors="ignore")
    return cp.stdout + "\n" + cp.stderr


def adb(args: list[str], timeout: int = 30) -> subprocess.CompletedProcess[str]:
    return run([str(ADB)] + args, timeout=timeout)


def adb_shell(command: str, timeout: int = 30) -> subprocess.CompletedProcess[str]:
    return adb(["-s", "emulator-5554", "shell", command], timeout=timeout)


def kill_emulator() -> None:
    try:
        adb(["-s", "emulator-5554", "emu", "kill"], timeout=8)
    except Exception:
        pass
    time.sleep(3)
    # Sweep common stale Windows processes only if they are from this emulator tree.
    ps = [
        "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe",
        "-NoProfile",
        "-Command",
        "$procs=Get-Process emulator,qemu-system-x86_64 -ErrorAction SilentlyContinue; "
        "$procs | Where-Object { $_.Path -like '*re-vm*google-emulator*' -or $_.Path -like '*re-vm*runt*' } | Stop-Process -Force -ErrorAction SilentlyContinue"
    ]
    try:
        run(ps, timeout=15)
    except Exception:
        pass
    time.sleep(2)


def wait_adb(timeout: int = 120) -> tuple[bool, float]:
    start = time.time()
    while time.time() - start < timeout:
        try:
            out = adb(["devices"], timeout=5).stdout
            if re.search(r"emulator-5554\s+device", out):
                return True, time.time() - start
        except Exception:
            pass
        time.sleep(1)
    return False, time.time() - start


def wait_boot(timeout: int = 180) -> tuple[bool, float]:
    start = time.time()
    while time.time() - start < timeout:
        try:
            boot = adb_shell("getprop sys.boot_completed", timeout=5).stdout.strip()
            if boot == "1":
                # extra stable probe
                probe = adb_shell("echo revm-ready", timeout=5).stdout
                if "revm-ready" in probe:
                    return True, time.time() - start
        except Exception:
            pass
        time.sleep(1)
    return False, time.time() - start


def renderer_feature_args(renderer: str) -> list[str]:
    if renderer == "opengl":
        return ["-feature", "-Vulkan"]
    if renderer == "vulkan":
        return ["-feature", "Vulkan"]
    return []


def device_profile_props(profile: str | None) -> dict[str, str]:
    if profile == "rog5":
        return {
            "ro.product.manufacturer": "asus",
            "ro.product.brand": "asus",
            "ro.product.model": "ASUS_I005DA",
            "ro.product.device": "ASUS_I005D",
            "ro.product.name": "WW_I005D",
            "ro.vendor.product.model": "ROG Phone 5",
        }
    if profile == "oneplus8t":
        return {
            "ro.product.manufacturer": "OnePlus",
            "ro.product.brand": "OnePlus",
            "ro.product.model": "KB2005",
            "ro.product.device": "OnePlus8T",
            "ro.product.name": "OnePlus8T",
            "ro.vendor.product.model": "OnePlus 8T",
        }
    if profile == "pixel8pro":
        return {
            "ro.product.manufacturer": "Google",
            "ro.product.brand": "google",
            "ro.product.model": "Pixel 8 Pro",
            "ro.product.device": "husky",
            "ro.product.name": "husky",
            "ro.vendor.product.model": "Pixel 8 Pro",
        }
    return {}


def update_avd_config_for_scenario(s: Scenario, out: Path) -> None:
    cfg = AVD_HOME / (AVD + ".avd") / "config.ini"
    if not cfg.exists():
        return
    text = cfg.read_text(encoding="utf-8", errors="ignore")
    gltransport = {
        "current": (1048576, 4096, 65536, 800),
        "low-latency": (2097152, 8192, 262144, 200),
        "throughput": (4194304, 16384, 1048576, 800),
    }.get(s.gltransport_profile, (1048576, 4096, 65536, 800))
    updates = {
        "hw.cpu.ncore": str(s.cores),
        "hw.ramSize": str(s.ram_mb),
        "hw.lcd.width": str(s.width),
        "hw.lcd.height": str(s.height),
        "hw.lcd.density": str(s.dpi),
        "hw.lcd.vsync": str(s.fps),
        "hw.gpu.mode": s.gpu_mode,
        "hw.gltransport": "asg",
        "hw.gltransport.asg.writeBufferSize": str(gltransport[0]),
        "hw.gltransport.asg.writeStepSize": str(gltransport[1]),
        "hw.gltransport.asg.dataRingSize": str(gltransport[2]),
        "hw.gltransport.drawFlushInterval": str(gltransport[3]),
        "skin.name": f"{s.width}x{s.height}",
    }
    for key, value in updates.items():
        if re.search(rf"^{re.escape(key)}=.*$", text, flags=re.M):
            text = re.sub(rf"^{re.escape(key)}=.*$", f"{key}={value}", text, flags=re.M)
        else:
            text += f"\r\n{key}={value}"
    cfg.write_text(text, encoding="utf-8")
    (out / "config-ini-after.txt").write_text(text, encoding="utf-8", errors="ignore")


def emulator_args(s: Scenario) -> list[str]:
    args = [
        "-avd", AVD,
        "-no-boot-anim",
        "-gpu", s.gpu_mode,
        *renderer_feature_args(s.renderer),
        "-accel", "on",
        "-port", "5554",
        "-memory", str(s.ram_mb),
        "-cores", str(s.cores),
        "-skin", f"{s.width}x{s.height}",
        "-lcd-scaling-factor", "1.0",
        "-no-mouse-reposition",
        "-use-keycode-forwarding",
        "-no-sim",
        "-no-passive-gps",
        "-netfast",
        "-camera-back", "none",
        "-camera-front", "none",
        "-no-audio",
        "-no-metrics",
        "-crash-report-mode", "never",
    ]
    for key, value in device_profile_props(s.device_profile).items():
        args.extend(["-prop", f"{key}={value}"])
    for feature in s.emulator_features:
        args.extend(["-feature", feature])
    if s.vsync_rate:
        args.extend(["-vsync-rate", str(s.vsync_rate)])
    if s.systemui_renderer:
        args.extend(["-systemui-renderer", s.systemui_renderer])
    if s.ram_mb <= 1536:
        args.append("-lowram")
    return args


def set_emulator_priority(pid: int) -> None:
    cmd = [
        "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe",
        "-NoProfile",
        "-Command",
        f"try {{ (Get-Process -Id {pid}).PriorityClass='High' }} catch {{ }}"
    ]
    try:
        run(cmd, timeout=10)
    except Exception:
        pass


def get_host_process_samples() -> list[dict]:
    cmd = [
        "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe",
        "-NoProfile",
        "-Command",
        "Get-Process emulator,qemu-system-x86_64 -ErrorAction SilentlyContinue | "
        "Select-Object ProcessName,Id,CPU,WorkingSet64,PrivateMemorySize64,Path | ConvertTo-Json -Depth 3 -Compress"
    ]
    try:
        cp = run(cmd, timeout=10)
        text = cp.stdout.strip()
        if not text:
            return []
        parsed = json.loads(text)
        return parsed if isinstance(parsed, list) else [parsed]
    except Exception:
        return []


def sample_host(out: Path, seconds: int) -> dict:
    process_rows = []
    aggregate_rows = []
    start = time.time()
    last_cpu_by_pid: dict[int, tuple[float, float]] = {}
    while time.time() - start < seconds:
        now = time.time()
        samples = get_host_process_samples()
        tick_cpu = []
        tick_ws = 0.0
        for p in samples:
            try:
                path = str(p.get("Path") or "").replace("\\", "/").lower()
                if "re-vm" not in path or "google-emulator" not in path:
                    continue
                pid = int(p.get("Id", 0))
                cpu = float(p.get("CPU") or 0.0)
                ws = int(p.get("WorkingSet64") or 0)
                prev = last_cpu_by_pid.get(pid)
                cpu_pct = None
                if prev:
                    prev_t, prev_cpu = prev
                    elapsed = max(0.001, now - prev_t)
                    cpu_pct = max(0.0, (cpu - prev_cpu) / elapsed * 100.0)
                    tick_cpu.append(cpu_pct)
                last_cpu_by_pid[pid] = (now, cpu)
                ws_mb = ws / 1048576.0
                tick_ws += ws_mb
                process_rows.append({"t": round(now - start, 3), "pid": pid, "name": p.get("ProcessName"), "path": p.get("Path"), "cpu_total_s": cpu, "cpu_pct_one_core": cpu_pct, "working_set_mb": ws_mb})
            except Exception:
                continue
        if tick_ws > 0:
            aggregate_rows.append({"t": round(now - start, 3), "cpu_pct_one_core_total": sum(tick_cpu) if tick_cpu else None, "working_set_mb_total": tick_ws})
        time.sleep(2)
    (out / "host-process-samples.json").write_text(json.dumps({"processes": process_rows, "totals": aggregate_rows}, indent=2), encoding="utf-8")
    cpu_vals = [r["cpu_pct_one_core_total"] for r in aggregate_rows if r.get("cpu_pct_one_core_total") is not None]
    ws_vals = [r["working_set_mb_total"] for r in aggregate_rows]
    return {
        "hostCpuPctOneCoreAvg": round(statistics.mean(cpu_vals), 2) if cpu_vals else None,
        "hostCpuPctOneCoreP95": round(percentile(cpu_vals, 95), 2) if cpu_vals else None,
        "hostWorkingSetMbMax": round(max(ws_vals), 1) if ws_vals else None,
    }


def percentile(vals: list[float], p: float) -> float:
    if not vals:
        return 0.0
    vals = sorted(vals)
    k = (len(vals) - 1) * p / 100.0
    f = int(k)
    c = min(f + 1, len(vals) - 1)
    if f == c:
        return vals[f]
    return vals[f] * (c - k) + vals[c] * (k - f)


LEAN_PACKAGES = [
    "com.google.android.googlequicksearchbox",
    "com.google.android.apps.docs",
    "com.google.android.apps.tachyon",
    "com.google.android.feedback",
    "com.google.android.printservice.recommendation",
    "com.google.android.apps.wellbeing",
    "com.google.android.setupwizard",
    "com.google.android.tts",
    "com.android.printspooler",
    "com.android.wallpaper.livepicker",
]


def apply_guest_profile(profile: str, out: Path) -> None:
    commands: list[str] = []
    if profile == "lean":
        commands += [
            "settings put global window_animation_scale 0",
            "settings put global transition_animation_scale 0",
            "settings put global animator_duration_scale 0",
            "settings put global adaptive_battery_management_enabled 0",
            "settings put global wifi_scan_always_enabled 0",
            "cmd deviceidle disable",
        ]
        commands += [f"pm disable-user --user 0 {pkg}" for pkg in LEAN_PACKAGES]
    elif profile == "performance":
        commands += [
            "settings put global window_animation_scale 1",
            "settings put global transition_animation_scale 1",
            "settings put global animator_duration_scale 1",
            "settings put global adaptive_battery_management_enabled 0",
            "settings put global wifi_scan_always_enabled 0",
            "cmd deviceidle disable",
        ]
        commands += [f"pm enable --user 0 {pkg}" for pkg in LEAN_PACKAGES]
    else:
        commands += [
            "settings put global window_animation_scale 1",
            "settings put global transition_animation_scale 1",
            "settings put global animator_duration_scale 1",
            "cmd deviceidle enable",
        ]
        commands += [f"pm enable --user 0 {pkg}" for pkg in LEAN_PACKAGES]
    lines = []
    for command in commands:
        cp = adb_shell(command, timeout=12)
        lines.append(f"$ {command}\nexit={cp.returncode}\n{cp.stdout}{cp.stderr}")
    (out / "guest-profile-commands.txt").write_text("\n".join(lines), encoding="utf-8", errors="ignore")
    capture(out / "guest-profile-state.txt", "guest profile state", adb_shell(
        "settings get global window_animation_scale; settings get global transition_animation_scale; "
        "settings get global animator_duration_scale; dumpsys deviceidle | head -30; "
        "pm list packages -d", timeout=25))


def benchmark_native_x86(out: Path, size_mb: int = 128) -> dict:
    # Android's /system/bin/toybox is native x86_64 in this AVD. Hashing a cached
    # file gives a deterministic native guest CPU/memory-path comparison.
    prep = adb_shell(f"dd if=/dev/zero of=/data/local/tmp/revm-native.bin bs=1048576 count={size_mb} conv=fsync", timeout=90)
    capture(out / "x86-native-prep.txt", "prepare x86 benchmark", prep)
    times = []
    logs = []
    for i in range(3):
        cp = adb_shell("toybox time -p sha256sum /data/local/tmp/revm-native.bin >/dev/null", timeout=90)
        text = cp.stdout + "\n" + cp.stderr
        logs.append(f"run {i+1} exit={cp.returncode}\n{text}")
        match = re.search(r"(?:^|\n)real\s+([0-9.]+)", text)
        if match:
            times.append(float(match.group(1)))
    adb_shell("rm -f /data/local/tmp/revm-native.bin", timeout=10)
    (out / "x86-native-sha256.txt").write_text("\n".join(logs), encoding="utf-8", errors="ignore")
    if not times:
        return {"x86NativeSha256MbPerSec": None, "x86NativeSha256Seconds": None}
    median_s = statistics.median(times)
    return {
        "x86NativeSha256MbPerSec": round(size_mb / median_s, 2) if median_s > 0 else None,
        "x86NativeSha256Seconds": round(median_s, 3),
    }


def install_apk(apk_path: Path, out: Path) -> bool:
    cp = adb(["-s", "emulator-5554", "install", "-r", "-d", win_path(apk_path)], timeout=120)
    capture(out / "install-apk.txt", f"install {apk_path}", cp)
    return cp.returncode == 0 and "Success" in (cp.stdout + cp.stderr)


def detect_package(cli_package: str | None, out: Path) -> str | None:
    if cli_package:
        return cli_package
    cp = adb_shell("pm list packages", timeout=20)
    capture(out / "packages.txt", "pm list packages", cp)
    packages = []
    for line in cp.stdout.splitlines():
        if line.startswith("package:"):
            packages.append(line.split(":", 1)[1].strip())
    for pkg in packages:
        lower = pkg.lower()
        if any(h in lower for h in SUBWAY_HINTS):
            return pkg
    return None


def capture_guest_screenshot(path: Path) -> bool:
    try:
        cp = subprocess.run([str(ADB), "-s", "emulator-5554", "exec-out", "screencap", "-p"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=25)
        if cp.returncode == 0 and len(cp.stdout) > 1024:
            path.write_bytes(cp.stdout)
            return True
    except Exception:
        pass
    return False


def launch_package(pkg: str, out: Path) -> bool:
    adb_shell(f"am force-stop {pkg}", timeout=10)
    # Reset stats before launch and use monkey for launcher activity discovery.
    adb_shell(f"dumpsys gfxinfo {pkg} reset", timeout=10)
    cp = adb_shell(f"monkey -p {pkg} -c android.intent.category.LAUNCHER 1", timeout=20)
    capture(out / "launch-package.txt", f"launch {pkg}", cp)
    return cp.returncode == 0 and ("Events injected: 1" in cp.stdout or "Events injected: 1" in cp.stderr)


def parse_gfxinfo_framestats(text: str, target_fps: int = 60) -> dict:
    # Android framestats CSV columns include IntendedVsync,Vsync,...,FrameCompleted.
    header = None
    rows = []
    in_profile = False
    for line in text.splitlines():
        line = line.strip()
        if not line:
            continue
        if line.startswith("---PROFILEDATA---"):
            in_profile = not in_profile
            continue
        if not in_profile:
            continue
        if line.startswith("Flags,"):
            header = [x.strip() for x in line.split(",")]
            continue
        if header and re.match(r"^\d+,", line):
            parts = line.split(",")
            if len(parts) == len(header):
                rows.append(dict(zip(header, parts)))
    frame_ms = []
    completed_ts = []
    jank = 0
    frame_deadline_ms = 1000.0 / max(1, target_fps)
    for r in rows:
        try:
            flags = int(r.get("Flags", "0"))
            if flags != 0:
                continue
            intended = int(r["IntendedVsync"])
            completed = int(r["FrameCompleted"])
            dur_ms = (completed - intended) / 1_000_000.0
            if 0 < dur_ms < 1000:
                frame_ms.append(dur_ms)
                completed_ts.append(completed)
                if dur_ms > frame_deadline_ms:
                    jank += 1
        except Exception:
            continue
    if not frame_ms:
        return {"gfxFrames": 0, "gfxFpsApprox": None, "gfxAvgMs": None, "gfxP95Ms": None, "gfxJankPct": None}
    completed_ts = sorted(set(completed_ts))
    span_s = (completed_ts[-1] - completed_ts[0]) / 1_000_000_000.0 if len(completed_ts) > 1 else 0.0
    fps = (len(completed_ts) - 1) / span_s if span_s > 0 else None
    return {
        "gfxFrames": len(frame_ms),
        "gfxFpsApprox": round(fps, 2) if fps else None,
        "gfxAvgMs": round(statistics.mean(frame_ms), 2),
        "gfxP95Ms": round(percentile(frame_ms, 95), 2),
        "gfxJankPct": round(jank / len(frame_ms) * 100.0, 2),
    }


def collect_surface_latency(pkg: str | None, out: Path, seconds: int) -> dict:
    layers_cp = adb_shell("dumpsys SurfaceFlinger --list", timeout=15)
    capture(out / "surfaceflinger-layers.txt", "SurfaceFlinger --list", layers_cp)
    if not pkg:
        return {"sfLayer": None, "sfFrames": 0, "sfFpsApprox": None}
    candidates = [ln.strip() for ln in layers_cp.stdout.splitlines() if pkg.lower() in ln.lower()]
    if not candidates:
        candidates = [ln.strip() for ln in layers_cp.stdout.splitlines() if any(h in ln.lower() for h in SUBWAY_HINTS)]
    # Prefer real app surfaces, not WindowContainer/ActivityRecord/Task bookkeeping layers.
    candidates = sorted(candidates, key=lambda ln: (
        0 if "surfaceview" in ln.lower() else 1,
        0 if (pkg.lower() in ln.lower() and "activityrecord" not in ln.lower() and "{" not in ln and "task=" not in ln.lower()) else 1,
        0 if "/" in ln else 1,
        len(ln)
    ))
    if not candidates:
        return {"sfLayer": None, "sfFrames": 0, "sfFpsApprox": None}
    layer = candidates[0]
    adb_shell(f"dumpsys SurfaceFlinger --latency-clear '{layer}'", timeout=10)
    time.sleep(seconds)
    cp = adb_shell(f"dumpsys SurfaceFlinger --latency '{layer}'", timeout=20)
    capture(out / "surfaceflinger-latency.txt", f"SurfaceFlinger latency {layer}", cp)
    ts = []
    for line in cp.stdout.splitlines()[1:]:
        vals = [v for v in line.split() if v.isdigit()]
        if len(vals) >= 2:
            present = int(vals[1])
            # SurfaceFlinger uses INT64_MAX as an invalid/pending fence sentinel.
            # Including it makes an otherwise valid run look like ~0 FPS.
            if 0 < present < 1_000_000_000_000_000_000:
                ts.append(present)
    if len(ts) < 2:
        return {"sfLayer": layer, "sfFrames": len(ts), "sfFpsApprox": None}
    span_s = (max(ts) - min(ts)) / 1_000_000_000.0
    fps = (len(ts) - 1) / span_s if span_s > 0 else None
    return {"sfLayer": layer, "sfFrames": len(ts), "sfFpsApprox": round(fps, 2) if fps else None}


def run_scenario(s: Scenario, out_root: Path, package: str | None, install_apk_path: Path | None, measure_seconds: int, warmup_seconds: int, reset_app_data: bool = False) -> dict:
    out = out_root / s.name
    out.mkdir(parents=True, exist_ok=True)
    result = asdict(s)
    result.update({"adbReady": False, "bootReady": False, "package": package})
    kill_emulator()
    env = os.environ.copy()
    # When a Windows .exe is launched through WSL interop, arbitrary env vars are
    # not translated unless WSLENV declares them. Keep POSIX paths here and let
    # WSLENV /p convert them to D:\\... for emulator.exe/adb.exe.
    env["ANDROID_AVD_HOME"] = str(AVD_HOME)
    env["ANDROID_HOME"] = str(SDK)
    env["ANDROID_SDK_ROOT"] = str(SDK)
    env["WSLENV"] = "ANDROID_AVD_HOME/p:ANDROID_HOME/p:ANDROID_SDK_ROOT/p"

    update_avd_config_for_scenario(s, out)
    args = emulator_args(s)
    (out / "launch-args.txt").write_text(" ".join(args), encoding="utf-8")
    stdout = open(out / "emulator-stdout.log", "w", encoding="utf-8", errors="ignore")
    stderr = open(out / "emulator-stderr.log", "w", encoding="utf-8", errors="ignore")
    proc = subprocess.Popen([str(EMULATOR)] + args, cwd=str(SDK / "emulator"), stdout=stdout, stderr=stderr, env=env)
    result["emulatorPid"] = proc.pid
    set_emulator_priority(proc.pid)
    start = time.time()
    try:
        adb_ok, adb_s = wait_adb(130)
        result["adbReady"] = adb_ok
        result["adbSeconds"] = round(adb_s, 1)
        if not adb_ok:
            return result
        boot_ok, boot_s = wait_boot(200)
        result["bootReady"] = boot_ok
        result["bootSeconds"] = round(time.time() - start, 1)
        if not boot_ok:
            return result
        if install_apk_path is not None:
            result["installApk"] = str(install_apk_path)
            result["installOk"] = install_apk(install_apk_path, out)
        # Basic deterministic device state.
        for cmd in [
            "settings put system accelerometer_rotation 0",
            "settings put system user_rotation 0",
            f"settings put system peak_refresh_rate {s.fps}",
            f"settings put system min_refresh_rate {min(60, s.fps)}",
            f"settings put global peak_refresh_rate {s.fps}",
            f"settings put global min_refresh_rate {min(60, s.fps)}",
            f"wm size {s.width}x{s.height}",
            f"wm density {s.dpi}",
        ]:
            adb_shell(cmd, timeout=10)
        capture(out / "wm.txt", "wm", adb_shell("wm size; wm density; settings get system user_rotation", timeout=10))
        capture(out / "getprop-graphics.txt", "graphics props", adb_shell("getprop | grep -Ei 'gpu|egl|gles|vulkan|renderer|hwui|sf|qemu|refresh|fps|native_bridge'", timeout=15))
        capture(out / "getprop-device.txt", "device props", adb_shell("getprop | grep -Ei 'ro.product|ro.vendor.product|manufacturer|brand|model|device|native_bridge'", timeout=15))
        apply_guest_profile(s.guest_profile, out)
        result.update(benchmark_native_x86(out))
        capture(out / "refresh-settings.txt", "refresh settings", adb_shell("settings list system | grep -Ei 'refresh|fps'; settings list global | grep -Ei 'refresh|fps'; dumpsys SurfaceFlinger | grep -Ei 'refresh|fps|present|DisplayMode' | head -120", timeout=20))
        capture(out / "native-bridge.txt", "native bridge", adb_shell("getprop ro.dalvik.vm.native.bridge; getprop ro.enable.native.bridge.exec; getprop ro.enable.native.bridge.exec64; find /system /vendor /apex -iname '*ndk*translation*' -o -iname '*houdini*' 2>/dev/null | head -80", timeout=25))
        pkg = detect_package(package, out)
        result["package"] = pkg
        if pkg:
            if reset_app_data:
                capture(out / "reset-app-data.txt", f"pm clear {pkg}", adb_shell(f"pm clear {pkg}", timeout=30))
            result["packageLaunchOk"] = launch_package(pkg, out)
            time.sleep(warmup_seconds)
            result["preMeasureScreenshot"] = capture_guest_screenshot(out / "pre-measure.png")
            # Reset after a long enough warmup to benchmark the loaded Unity state,
            # not ARM native-bridge startup or the animated loading screen.
            adb_shell(f"dumpsys gfxinfo {pkg} reset", timeout=10)
            host_summary = sample_host(out, measure_seconds)
            result.update(host_summary)
            gfx_cp = adb_shell(f"dumpsys gfxinfo {pkg} framestats", timeout=30)
            capture(out / "gfxinfo-framestats.txt", f"gfxinfo {pkg} framestats", gfx_cp)
            result.update(parse_gfxinfo_framestats(gfx_cp.stdout, s.fps))
            # SurfaceFlinger latency is extra; if it times out or no layer exists we still keep gfxinfo/host metrics.
            try:
                result.update(collect_surface_latency(pkg, out, max(8, min(15, measure_seconds // 2))))
            except Exception as ex:
                result.update({"sfError": str(ex), "sfFrames": 0, "sfFpsApprox": None})
        else:
            result["packageLaunchOk"] = False
            result["note"] = "No Subway package detected; pass --package com.kiloo.subwaysurf or install the game. Collected boot/host-only data."
            result.update(sample_host(out, min(measure_seconds, 20)))
        capture(out / "dumpsys-gfxinfo-all.txt", "dumpsys gfxinfo", adb_shell("dumpsys gfxinfo", timeout=30))
        capture(out / "logcat-graphics.txt", "graphics logcat", adb(["-s", "emulator-5554", "logcat", "-d", "-t", "800", "SurfaceFlinger:*", "OpenGLRenderer:*", "HWUI:*", "EGL_emulation:*", "gralloc:*", "vulkan:*", "*:S"], timeout=30))
        return result
    finally:
        try:
            stdout.close(); stderr.close()
        except Exception:
            pass
        kill_emulator()


def write_summary(out_root: Path, rows: list[dict]) -> None:
    (out_root / "summary.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    keys = []
    for r in rows:
        for k in r.keys():
            if k not in keys:
                keys.append(k)
    with open(out_root / "summary.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=keys)
        w.writeheader(); w.writerows(rows)
    # compact markdown
    cols = ["name", "guest_profile", "gltransport_profile", "renderer", "cores", "ram_mb", "width", "height", "adbReady", "bootReady", "package", "packageLaunchOk", "gfxFpsApprox", "gfxAvgMs", "gfxP95Ms", "gfxJankPct", "sfFpsApprox", "hostCpuPctOneCoreAvg", "hostWorkingSetMbMax", "x86NativeSha256MbPerSec", "x86NativeSha256Seconds"]
    lines = ["| " + " | ".join(cols) + " |", "| " + " | ".join(["---"] * len(cols)) + " |"]
    for r in rows:
        lines.append("| " + " | ".join(str(r.get(c, "")) for c in cols) + " |")
    (out_root / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--package", help="Game package to launch, e.g. com.kiloo.subwaysurf")
    ap.add_argument("--install-apk", help="Optional APK to install after each scenario boots before package detection/launch")
    ap.add_argument("--measure-seconds", type=int, default=45)
    ap.add_argument("--warmup-seconds", type=int, default=20)
    ap.add_argument("--only", help="Comma-separated scenario names to run")
    ap.add_argument("--reset-app-data", action="store_true", help="Clear package data before each launch for deterministic onboarding-state comparisons")
    args = ap.parse_args()

    if not EMULATOR.exists() or not ADB.exists():
        print(f"Missing emulator/adb: {EMULATOR} {ADB}", file=sys.stderr)
        return 2
    out_root = ROOT / "runtime" / "reports" / "performance" / ("game-matrix-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    install_apk_path = Path(args.install_apk).resolve() if args.install_apk else None
    if install_apk_path is not None and not install_apk_path.exists():
        print(f"Missing --install-apk file: {install_apk_path}", file=sys.stderr)
        return 2
    out_root.mkdir(parents=True, exist_ok=True)
    capture(out_root / "accel-check.txt", "emulator -accel-check", run([str(EMULATOR), "-accel-check"], timeout=20))
    selected = SCENARIOS
    if args.only:
        wanted = {x.strip() for x in args.only.split(",") if x.strip()}
        selected = [s for s in SCENARIOS if s.name in wanted]
    rows = []
    for s in selected:
        print(f"=== {s.name} ===", flush=True)
        row = run_scenario(s, out_root, args.package, install_apk_path, args.measure_seconds, args.warmup_seconds, args.reset_app_data)
        rows.append(row)
        write_summary(out_root, rows)
        print(json.dumps(row, indent=2), flush=True)
    print(f"RESULT_DIR={out_root}")
    print((out_root / "summary.md").read_text(encoding="utf-8"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
