#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import math
import re
import statistics
import subprocess
import time
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--adb", required=True)
parser.add_argument("--serial", default="emulator-5554")
parser.add_argument("--refresh", type=float, required=True)
parser.add_argument("--out", required=True)
args = parser.parse_args()
out_base = Path(args.out)
out_base.parent.mkdir(parents=True, exist_ok=True)


def adb(*parts: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run([args.adb, "-s", args.serial, *parts], capture_output=True, text=True, errors="replace")
    if check and result.returncode:
        raise RuntimeError(result.stderr or result.stdout)
    return result


def shell(command: str) -> str:
    return adb("shell", command).stdout.replace("\r", "")

shell("input keyevent KEYCODE_WAKEUP; wm dismiss-keyguard; settings put system screen_off_timeout 1800000; input keyevent KEYCODE_HOME")
shell("am start -W -a android.settings.SETTINGS")
time.sleep(2)
for package in ("com.android.settings", "com.android.systemui"):
    shell(f"dumpsys gfxinfo {package} reset")
for _ in range(8):
    shell("input swipe 540 1700 540 450 260")
    time.sleep(0.15)
for _ in range(8):
    shell("input swipe 540 450 540 1700 260")
    time.sleep(0.15)

def parse(package: str) -> dict:
    raw = shell(f"dumpsys gfxinfo {package} framestats")
    Path(args.out + f"-{package}.txt").write_text(raw, encoding="utf-8")
    frames: list[tuple[float, bool]] = []
    in_profile = False
    for line in raw.splitlines():
        stripped = line.strip()
        if stripped == "---PROFILEDATA---":
            in_profile = not in_profile
            continue
        if not in_profile or not re.match(r"^[01],", stripped):
            continue
        cols = stripped.split(",")
        if len(cols) < 17:
            continue
        try:
            # API 34 inserts FrameTimelineVsyncId after Flags. Use the
            # frame deadline for the jank verdict rather than an assumed
            # refresh budget; an app may intentionally render below the
            # physical display's advertised mode.
            intended = int(cols[2])
            deadline = int(cols[9])
            completed = int(cols[16])
        except ValueError:
            continue
        if intended > 0 and completed >= intended:
            frames.append(((completed - intended) / 1_000_000.0, completed > deadline))
    durations = [item[0] for item in frames]
    budget = 1000.0 / args.refresh
    jank = [item for item in frames if item[1]]
    ordered = sorted(durations)
    def percentile(p: float) -> float | None:
        if not ordered:
            return None
        index = min(len(ordered) - 1, max(0, math.ceil(p * len(ordered)) - 1))
        return ordered[index]
    return {
        "package": package,
        "frames": len(frames),
        "refresh_hz": args.refresh,
        "budget_ms": budget,
        "janky_frames": len(jank),
        "jank_percent": (100.0 * len(jank) / len(frames)) if frames else None,
        "p50_ms": percentile(0.50),
        "p95_ms": percentile(0.95),
        "p99_ms": percentile(0.99),
        "max_ms": max(durations) if durations else None,
    }

report = {"results": [parse("com.android.settings"), parse("com.android.systemui")]}
Path(args.out + ".json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report))
