#!/usr/bin/env python3
"""Fail-closed static validation for both published API 34 baseline lanes."""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(8 * 1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def parse_time(value: str) -> dt.datetime:
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))


def input_paths(root: Path) -> dict[str, Path]:
    return {
        "personaJson": root / "android-tools/replayer-persona/personas/replayer-api34.json",
        "propertyTransformer": root / "scripts/android/persona_properties.py",
        "publisher": root / "scripts/android/Publish-Api34Persona.sh",
        "settingsOverlayBuilder": root / "scripts/android/Build-SettingsOverlay.ps1",
        "customizerBuilder": root / "scripts/android/Build-Customizer.ps1",
        "customizerApk": root / "ReVM/Assets/Android/replayer-customizer.apk",
        "settingsOverlayApk": root / "runtime/google-emulator/build/settings-overlay/REPlayerSettingsIdentityOverlay.apk",
        "disabledPackages": root / "android-tools/replayer-customizer/baseline-disabled-packages.txt",
        "sourceAvdConfig": root / "runtime/google-emulator/avd-home/Api34Official.avd/config.ini",
        "emulatorMetadata": root / "runtime/google-emulator/sdk/emulator/source.properties",
        "systemImageMetadata": root / "runtime/google-emulator/sdk/system-images/android-34/google_apis/x86_64/source.properties",
        "systemImage": root / "runtime/google-emulator/sdk/system-images/android-34/google_apis/x86_64/system.img",
        "vendorImage": root / "runtime/google-emulator/sdk/system-images/android-34/google_apis/x86_64/vendor.img",
        "userdataImage": root / "runtime/google-emulator/sdk/system-images/android-34/google_apis/x86_64/userdata.img",
        "gfxstreamManifest": root / "runtime/google-emulator/persona-emulator-37.1.7/replayer-gfxstream-persona.json",
    }


def validate_lane(root: Path, spec: dict[str, str]) -> tuple[dict, dict]:
    report_path = root / spec["report"]
    avd = root / spec["avd"]
    require(report_path.is_file(), f"{spec['lane']}: publication report is missing")
    require(avd.is_dir(), f"{spec['lane']}: canonical AVD is missing")
    report = json.loads(report_path.read_text())
    marker = json.loads((avd / "replayer-baseline.json").read_text())
    require(report == marker, f"{spec['lane']}: report and embedded marker differ")
    require(report.get("mode") == spec["mode"], f"{spec['lane']}: wrong marker mode")
    require(report.get("targetAvd") == spec["target"], f"{spec['lane']}: wrong marker target")
    require(report.get("securityLane") == spec["security"], f"{spec['lane']}: wrong security lane")
    require(report.get("persona") == "replayer-api34", f"{spec['lane']}: canonical persona is not neutral REPlayer")
    props = report.get("properties", [])
    require(props[:2] == ["REPlayer Virtual Device", "replayer_x86_64"], f"{spec['lane']}: identity mismatch")
    require(props[3:8] == ["user", "release-keys", spec["debug"], "1", "x86_64,arm64-v8a"],
            f"{spec['lane']}: security/build properties mismatch")
    root_evidence = report.get("adbRoot", "")
    require(("cannot run as root" in root_evidence) if spec["debug"] == "0" else ("uid=0" in root_evidence),
            f"{spec['lane']}: root-policy evidence mismatch")
    require(report.get("displayProfiles") == {
        "0": {"width": 1080, "height": 2400, "density": 420},
        "2": {"width": 1920, "height": 1200, "density": 240},
    }, f"{spec['lane']}: display profiles mismatch")
    dark = report.get("darkMode", {})
    require(dark.get("requested") == "yes" and dark.get("secureUiNightMode") == 2,
            f"{spec['lane']}: dark mode was not requested")
    for boundary in ("warm", "cold"):
        evidence = dark.get(boundary, [])
        require(len(evidence) == 3 and evidence[0] == "Night mode: yes" and evidence[1] == "2" and
                "mComputedNightMode=true" in evidence[2] and "mCurUiMode=0x21" in evidence[2],
                f"{spec['lane']}: dark mode was not retained at {boundary} boundary")
    expected_policy_count = len([
        line.split("#", 1)[0].strip()
        for line in (root / "android-tools/replayer-customizer/baseline-disabled-packages.txt").read_text().splitlines()
        if line.split("#", 1)[0].strip()
    ])
    package_policy = report.get("packagePolicy", {})
    policy_counts = [
        package_policy.get("preRebootDisabledInstalledPackages"),
        package_policy.get("warmDisabledInstalledPackages"),
        package_policy.get("coldDisabledInstalledPackages"),
    ]
    require(all(isinstance(value, int) and 0 < value <= expected_policy_count for value in policy_counts) and
            len(set(policy_counts)) == 1,
            f"{spec['lane']}: complete package policy was not retained across warm/cold boots")

    for phase, minimum in (("warm", 60), ("cold", 30)):
        sample = report["continuity"][phase]
        duration = (parse_time(sample["endedAtUtc"]) - parse_time(sample["startedAtUtc"])).total_seconds()
        require(duration >= minimum, f"{spec['lane']}: {phase} sample was too short")
        require(sample["systemServerFirst"] == sample["systemServerLast"] and sample["systemServerFirst"],
                f"{spec['lane']}: {phase} system_server changed")
        require(sample["networkStackFirst"] == sample["networkStackLast"] and sample["networkStackFirst"],
                f"{spec['lane']}: {phase} NetworkStack changed")

    for name, evidence in report["files"].items():
        path = avd / name
        require(path.is_file(), f"{spec['lane']}: published file missing: {name}")
        require(path.stat().st_size == evidence["bytes"] and sha256(path) == evidence["sha256"],
                f"{spec['lane']}: published file mismatch: {name}")

    paths = input_paths(root)
    gfxstream = json.loads(paths["gfxstreamManifest"].read_text())
    require(gfxstream.get("vendor") == "REPlayer" and
            gfxstream.get("renderer") == "REPlayer Virtual GPU (OpenGL ES 3.1 1.0)",
            f"{spec['lane']}: neutral gfxstream identity is absent")
    for name, evidence in report["provenance"]["inputs"].items():
        path = paths[name]
        require(path.is_file(), f"{spec['lane']}: provenance input missing: {name}")
        require(path.stat().st_size == evidence["bytes"] and sha256(path) == evidence["sha256"],
                f"{spec['lane']}: provenance input mismatch: {name}")

    signers = report["provenance"]["apkSignerSha256"]
    require(signers["customizer"] == signers["settingsOverlay"] and len(signers["customizer"]) == 64,
            f"{spec['lane']}: APK signer mismatch")
    forbidden = [
        "snapshots", "tmpAdbCmds", "multiinstance.lock", "emulator-user.ini",
        "emu-launch-params.txt", "read-snapshot.txt", "bootcompleted.ini",
        "quickbootChoice.ini", "hardware-qemu.ini", "cache.img", "cache.img.qcow2",
        "data/misc/pstore",
    ]
    present = [name for name in forbidden if (avd / name).exists()]
    require(not present, f"{spec['lane']}: volatile state remains: {', '.join(present)}")
    return report, {
        "lane": spec["lane"],
        "verdict": "PASS",
        "publishedAtUtc": report["publishedAtUtc"],
        "outputFiles": len(report["files"]),
        "provenanceInputs": len(report["provenance"]["inputs"]),
        "verifiedDisabledInstalledPackages": policy_counts[0],
        "declaredPackagePolicyEntries": expected_policy_count,
        "sourceTreeDirty": report["provenance"]["sourceTreeDirty"],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    specs = [
        {"lane": "release", "report": "runtime/google-emulator/persona-publication.json", "avd": "runtime/google-emulator/avd-home/ReVM.avd", "mode": "stealth", "target": "ReVM", "security": "release-observation", "debug": "0"},
        {"lane": "resizable", "report": "runtime/google-emulator/persona-publication-resizable.json", "avd": "runtime/google-emulator/avd-home/ReVMResizable.avd", "mode": "compatibility", "target": "ReVMResizable", "security": "resizable-analysis", "debug": "1"},
    ]
    reports, results = [], []
    for spec in specs:
        report, result = validate_lane(root, spec)
        reports.append(report)
        results.append(result)
    release_inputs = reports[0]["provenance"]["inputs"]
    resizable_inputs = reports[1]["provenance"]["inputs"]
    require(release_inputs == resizable_inputs, "Cross-lane provenance inputs differ")
    require(reports[0]["provenance"]["apkSignerSha256"] == reports[1]["provenance"]["apkSignerSha256"],
            "Cross-lane APK signers differ")
    require(reports[0]["packagePolicy"] == reports[1]["packagePolicy"],
            "Cross-lane package-policy evidence differs")
    output = {"schema": 1, "phase": 1, "verdict": "PASS", "lanes": results, "crossLaneInputsMatch": True}
    rendered = json.dumps(output, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered)
    print(rendered, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
