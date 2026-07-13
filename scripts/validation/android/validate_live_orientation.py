#!/usr/bin/env python3
"""Exercise REPlayer's real WPF orientation command against a live resizable guest."""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[3]
ADB = ROOT / "runtime/google-emulator/sdk/platform-tools/adb.exe"
POWERSHELL = Path("/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe")
SERIAL = "emulator-5554"


def run(args: list[str], timeout: int = 30, check: bool = True) -> str:
    completed = subprocess.run(args, cwd=ROOT, text=True, capture_output=True, timeout=timeout)
    output = (completed.stdout + completed.stderr).replace("\r", "").strip()
    if check and completed.returncode != 0:
        raise RuntimeError(f"Command failed ({completed.returncode}): {' '.join(args)}\n{output}")
    return output


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def adb(*args: str, timeout: int = 30, check: bool = True) -> str:
    return run([str(ADB), "-s", SERIAL, *args], timeout=timeout, check=check)


def ps(script: str, timeout: int = 45) -> str:
    return run([str(POWERSHELL), "-NoProfile", "-Command", script], timeout=timeout)


def parse_json(text: str) -> Any:
    return json.loads(text.lstrip("\ufeff"))


def process_snapshot(replayer_pid: int) -> dict[str, Any]:
    script = rf'''
$p=Get-Process -Id {replayer_pid} -ErrorAction Stop
$items=@(Get-CimInstance Win32_Process | Where-Object {{$_.Name -in @('REPlayer.exe','emulator.exe','qemu-system-x86_64.exe')}} | ForEach-Object {{
 [pscustomobject]@{{pid=[int]$_.ProcessId;parent=[int]$_.ParentProcessId;name=$_.Name;path=$_.ExecutablePath;commandLine=$_.CommandLine}}
}})
[pscustomobject]@{{replayer=[pscustomobject]@{{pid=$p.Id;responding=$p.Responding;path=$p.Path}};processes=$items}} | ConvertTo-Json -Depth 5 -Compress
'''
    return parse_json(ps(script))


def qemu_process(snapshot: dict[str, Any]) -> dict[str, Any]:
    items = snapshot["processes"] if isinstance(snapshot["processes"], list) else [snapshot["processes"]]
    matches = [item for item in items if item["name"].lower() == "qemu-system-x86_64.exe"]
    require(len(matches) == 1, f"Expected one QEMU process, found {len(matches)}")
    return matches[0]


def host_window(qemu_pid: int) -> dict[str, Any]:
    script = rf'''
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class ReplayerWindowProbe {{
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
 [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
 [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder b, int n);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
 public struct Rect {{ public int Left,Top,Right,Bottom; }}
 public static object Find(uint target) {{
  object found=null;
  EnumChildWindows(GetDesktopWindow(),(h,l)=>{{
   uint p; GetWindowThreadProcessId(h,out p); if(p!=target || !IsWindowVisible(h)) return true;
   var b=new StringBuilder(512); GetWindowText(h,b,512); var title=b.ToString();
   if(!title.StartsWith("Android Emulator - REPlayer_Run_")) return true;
   Rect r; GetWindowRect(h,out r);
   found=new {{hwnd="0x"+h.ToInt64().ToString("X"),parent="0x"+GetParent(h).ToInt64().ToString("X"),title=title,x=r.Left,y=r.Top,width=r.Right-r.Left,height=r.Bottom-r.Top}};
   return false;
  }},IntPtr.Zero);
  return found;
 }}
}}
"@
[ReplayerWindowProbe]::Find({qemu_pid}) | ConvertTo-Json -Depth 3 -Compress
'''
    output = ps(script)
    require(bool(output), "Visible embedded Qt emulator window was not found")
    return parse_json(output)


def invoke_orientation(replayer_pid: int) -> dict[str, Any]:
    script = rf'''
Add-Type -AssemblyName UIAutomationClient
$p=Get-Process -Id {replayer_pid} -ErrorAction Stop
$root=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$cond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'OrientationProfileButton')
$e=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$cond)
if(-not $e){{throw 'OrientationProfileButton not found'}}
$pattern=$e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$pattern.Invoke()
[pscustomobject]@{{name=$e.Current.Name;automationId=$e.Current.AutomationId;enabled=$e.Current.IsEnabled;invokedAtUtc=[DateTime]::UtcNow.ToString('o')}} | ConvertTo-Json -Compress
'''
    return parse_json(ps(script))


def guest_ui_nodes() -> list[dict[str, str]]:
    remote = "/data/local/tmp/replayer-phase3-ui.xml"
    adb("shell", "uiautomator", "dump", remote, timeout=30)
    root = ElementTree.fromstring(adb("shell", "cat", remote, timeout=30))
    return [dict(node.attrib) for node in root.iter()]


def send_host_click(window: dict[str, Any], guest_x: int, guest_y: int,
                    guest_width: int, guest_height: int) -> dict[str, Any]:
    native_x = round(guest_x * window["width"] / guest_width)
    native_y = round(guest_y * window["height"] / guest_height)
    hwnd = int(window["hwnd"], 16)
    script = rf'''
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class ReplayerBackgroundClick {{
 [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h,uint m,UIntPtr w,IntPtr l);
 public static void Send(long h,int x,int y) {{
  var p=(IntPtr)((y<<16)|(x&0xffff));
  SendMessage((IntPtr)h,0x0200,UIntPtr.Zero,p);
  SendMessage((IntPtr)h,0x0201,(UIntPtr)1,p);
  SendMessage((IntPtr)h,0x0202,UIntPtr.Zero,p);
 }}
}}
"@
[ReplayerBackgroundClick]::Send({hwnd},{native_x},{native_y})
'''
    ps(script)
    return {"targetHwnd": window["hwnd"], "guestCoordinate": [guest_x, guest_y],
            "nativeCoordinate": [native_x, native_y], "method": "SendMessage(WM_LBUTTONDOWN/UP)"}


def display_snapshot(label: str, screenshot_dir: Path) -> dict[str, Any]:
    wm_size = adb("shell", "wm", "size")
    wm_density = adb("shell", "wm", "density")
    input_dump = adb("shell", "dumpsys", "input", timeout=30)
    frames = [line.strip() for line in input_dump.splitlines()
              if "logicalFrame=" in line or "physicalFrame=" in line or "deviceSize=" in line]
    surface = adb("shell", "dumpsys", "SurfaceFlinger", timeout=30)
    gles = [line.strip() for line in surface.splitlines() if "GLES:" in line]
    focus_dump = adb("shell", "dumpsys", "activity", "activities", timeout=30)
    focus = next((line.strip() for line in focus_dump.splitlines() if "mResumedActivity:" in line), "")
    screenshot_dir.mkdir(parents=True, exist_ok=True)
    screenshot_path = screenshot_dir / f"phase3-{label}.png"
    display_ids = adb("shell", "dumpsys", "SurfaceFlinger", "--display-id")
    display_match = re.search(r"Display (\d+) \(HWC display 0\)", display_ids)
    if display_match is None:
        raise RuntimeError(f"{label}: active HWC display 0 was not found")
    completed = subprocess.run([str(ADB), "-s", SERIAL, "exec-out", "screencap", "-d", display_match.group(1), "-p"], cwd=ROOT,
                               capture_output=True, timeout=30)
    require(completed.returncode == 0 and completed.stdout.startswith(b"\x89PNG"), f"{label}: screenshot capture failed")
    screenshot_path.write_bytes(completed.stdout)
    return {
        "capturedAtUtc": datetime.now(timezone.utc).isoformat(),
        "wmSize": wm_size,
        "wmDensity": wm_density,
        "systemServerPid": adb("shell", "pidof", "system_server"),
        "networkStackPid": adb("shell", "pidof", "com.android.networkstack.process"),
        "inputFrames": frames[:16],
        "resumedActivity": focus,
        "surfaceFlinger": "\n".join(gles[:4]),
        "screenshot": str(screenshot_path),
    }


def wait_profile(size: str, density: str, timeout: int = 45) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if size in adb("shell", "wm", "size") and density in adb("shell", "wm", "density"):
            return
        time.sleep(1)
    raise RuntimeError(f"Timed out waiting for display {size} @ {density}")


def start_settings_when_ready(timeout: int = 45) -> str:
    """Wait for ActivityTaskManager's permission policy to finish boot registration."""
    deadline = time.monotonic() + timeout
    last_output = ""
    while time.monotonic() < deadline:
        last_output = adb("shell", "am", "start", "-a", "android.settings.SETTINGS", check=False)
        if "Exception occurred" not in last_output and "Error:" not in last_output:
            return last_output
        time.sleep(1)
    raise RuntimeError(f"Settings did not become launchable within {timeout}s:\n{last_output}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--replayer-pid", type=int, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "runtime/validation/phase3-orientation.json")
    args = parser.parse_args()

    require(adb("get-state") == "device", "ADB device is unavailable")
    properties = {
        key: adb("shell", "getprop", key)
        for key in ("ro.product.model", "ro.product.device", "ro.debuggable", "ro.adb.secure")
    }
    root_response = adb("root", check=False)
    time.sleep(1)
    require(adb("get-state") == "device", "ADB did not reconnect after root verification")
    uid = adb("shell", "id", "-u")
    require(properties["ro.debuggable"] == "1" and properties["ro.adb.secure"] == "1" and uid == "0",
            "Resizable Analysis debug/root lane is invalid")

    adb("shell", "am", "force-stop", "com.android.settings")
    start_settings_when_ready()
    time.sleep(2)

    initial_process = process_snapshot(args.replayer_pid)
    qemu_initial = qemu_process(initial_process)
    screenshot_dir = ROOT / "runtime/validation"
    initial = display_snapshot("portrait-before", screenshot_dir)
    initial_window = host_window(qemu_initial["pid"])
    require("1080x2400" in initial["wmSize"] and "420" in initial["wmDensity"], "Initial profile is not portrait profile 0")
    require(initial_window["height"] > initial_window["width"], "Initial native Qt window is not portrait")

    to_landscape = invoke_orientation(args.replayer_pid)
    wait_profile("1920x1200", "240")
    time.sleep(2)
    landscape = display_snapshot("landscape", screenshot_dir)
    landscape_process = process_snapshot(args.replayer_pid)
    qemu_landscape = qemu_process(landscape_process)
    landscape_window = host_window(qemu_landscape["pid"])
    require(qemu_landscape["pid"] == qemu_initial["pid"], "QEMU restarted during portrait-to-landscape transition")
    require(landscape["systemServerPid"] == initial["systemServerPid"], "system_server restarted during portrait-to-landscape transition")
    require(landscape["networkStackPid"] == initial["networkStackPid"], "NetworkStack restarted during portrait-to-landscape transition")
    require(landscape_window["hwnd"] == initial_window["hwnd"], "Native emulator HWND changed during portrait-to-landscape transition")
    require(landscape_window["width"] > landscape_window["height"], "Native Qt window did not become landscape")

    before_nodes = guest_ui_nodes()
    target = next((node for node in before_nodes if node.get("text") == "Connected devices"), None)
    if target is None:
        raise RuntimeError("Connected devices target is absent from the landscape guest UI hierarchy")
    bounds = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", target.get("bounds", ""))
    if bounds is None:
        raise RuntimeError("Connected devices target has malformed guest bounds")
    left, top, right, bottom = map(int, bounds.groups())
    before_texts = {node.get("text", "") for node in before_nodes}
    host_input = send_host_click(landscape_window, (left + right) // 2, (top + bottom) // 2, 1920, 1200)
    time.sleep(2)
    after_nodes = guest_ui_nodes()
    added_texts = sorted({node.get("text", "") for node in after_nodes} - before_texts)
    require("Pair new device" in added_texts and "Connection preferences" in added_texts,
            "Native host click did not navigate the guest to Connected devices")
    host_input["guestTarget"] = "Connected devices"
    host_input["addedGuestTexts"] = added_texts
    host_input["verified"] = True

    to_portrait = invoke_orientation(args.replayer_pid)
    wait_profile("1080x2400", "420")
    time.sleep(2)
    final = display_snapshot("portrait-after", screenshot_dir)
    final_process = process_snapshot(args.replayer_pid)
    qemu_final = qemu_process(final_process)
    final_window = host_window(qemu_final["pid"])
    require(qemu_final["pid"] == qemu_initial["pid"], "QEMU restarted during landscape-to-portrait transition")
    require(final["systemServerPid"] == initial["systemServerPid"], "system_server restarted during landscape-to-portrait transition")
    require(final["networkStackPid"] == initial["networkStackPid"], "NetworkStack restarted during landscape-to-portrait transition")
    require(final_window["hwnd"] == initial_window["hwnd"], "Native emulator HWND changed during landscape-to-portrait transition")
    require(final_window["height"] > final_window["width"], "Native Qt window did not return to portrait")
    require(final_process["replayer"]["responding"] is True, "REPlayer is unresponsive after orientation cycle")

    log = (ROOT / "logs/google-emulator-runtime.log").read_text(errors="replace")
    segment = log[log.rfind("StartInstanceWithDebug"):]
    require("Applying native resizable display profile 2 (1920x1200)" in segment, "Profile 2 command is absent from runtime log")
    require(segment.count("Applying native resizable display profile 0 (1080x2400)") >= 2, "Profile 0 restore command is absent from runtime log")
    require("FATAL" not in segment, "FATAL diagnostic found in Phase 3 segment")

    case_dirs = sorted((ROOT / "runtime/cases").glob("*/*"), key=lambda path: path.stat().st_mtime, reverse=True)
    manifest = json.loads((case_dirs[0] / "manifest.json").read_text())
    report = {
        "schema": 1,
        "phase": 3,
        "verdict": "PASS",
        "caseId": manifest["CaseId"],
        "replayer": final_process["replayer"],
        "properties": properties,
        "adbRoot": {"response": root_response, "shellUid": uid},
        "qemuPid": qemu_initial["pid"],
        "transitions": {
            "portraitBefore": {"guest": initial, "nativeWindow": initial_window},
            "toLandscapeControl": to_landscape,
            "landscape": {"guest": landscape, "nativeWindow": landscape_window},
            "toPortraitControl": to_portrait,
            "portraitAfter": {"guest": final, "nativeWindow": final_window},
        },
        "continuity": {
            "qemuPidStable": True,
            "systemServerPidStable": True,
            "networkStackPidStable": True,
            "nativeHwndStable": True,
            "replayerResponsive": True,
        },
        "nativeHostInput": host_input,
        "runtime": {"profile2Logged": True, "profile0RestoreLogged": True, "fatalDiagnostics": False},
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    rendered = json.dumps(report, indent=2) + "\n"
    args.output.write_text(rendered)
    print(rendered, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
