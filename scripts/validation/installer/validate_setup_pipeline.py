#!/usr/bin/env python3
"""Static contract for the guest-facing REPlayer setup pipeline."""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


def require(condition: bool, message: str, findings: list[str]) -> None:
    if not condition:
        findings.append(message)


def main() -> int:
    findings: list[str] = []
    setup_path = ROOT / "setup.bat"
    installer_path = ROOT / "scripts" / "setup" / "Install-REPlayer.ps1"
    packager_path = ROOT / "scripts" / "setup" / "New-REPlayerDistribution.ps1"
    probe_path = ROOT / "scripts" / "validation" / "installer" / "probe_setup_pipeline.ps1"

    setup = setup_path.read_text(encoding="utf-8", errors="replace") if setup_path.exists() else ""
    installer = installer_path.read_text(encoding="utf-8", errors="replace") if installer_path.exists() else ""
    packager = packager_path.read_text(encoding="utf-8", errors="replace") if packager_path.exists() else ""
    runtime_service_path = ROOT / "ReVM" / "Runtime" / "Google" / "EmulatorService.cs"
    runtime_service = runtime_service_path.read_text(encoding="utf-8", errors="replace") if runtime_service_path.exists() else ""
    revm_paths_path = ROOT / "ReVM" / "Core" / "RevmPaths.cs"
    revm_paths = revm_paths_path.read_text(encoding="utf-8", errors="replace") if revm_paths_path.exists() else ""
    containment_path = ROOT / "ReVM" / "Runtime" / "Google" / "Containment.cs"
    containment = containment_path.read_text(encoding="utf-8", errors="replace") if containment_path.exists() else ""

    require("Install-REPlayer.ps1" in setup, "setup.bat does not delegate to the supported installer", findings)
    require("%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" in setup,
            "setup.bat depends on PATH instead of the inbox PowerShell executable", findings)
    require(re.search(r"exit\s+/b\s+%[A-Z_]+%", setup, re.IGNORECASE) is not None or
            "exit /b !ERRORLEVEL!" in setup,
            "setup.bat does not propagate installer failure", findings)
    for obsolete in ("android-x86", "qemu.weilnetz", "frida-server-16.5.9"):
        require(obsolete.lower() not in setup.lower(), f"setup.bat still contains obsolete dependency: {obsolete}", findings)

    require(installer_path.exists(), "supported PowerShell installer is missing", findings)
    require(packager_path.exists(), "release distribution builder is missing", findings)
    require(probe_path.exists(), "functional installer probe is missing", findings)

    for token in ("RuntimeSource", "InstallDirectory", "NoLaunch", "SkipHostChecks", "UseHardLinks"):
        require(re.search(rf"\${token}\b", installer) is not None,
                f"installer is missing -{token}", findings)
    require("Get-FileHash" in installer, "installer does not verify payload hashes", findings)
    require("replayer-runtime-manifest.json" in installer,
            "installer does not require a runtime package manifest", findings)
    require("WindowsHypervisorPlatform" in installer,
            "installer does not preflight Windows Hypervisor Platform", findings)
    require("LOCALAPPDATA" in installer,
            "installer does not default to a per-user non-admin destination", findings)
    require("emulator-check.exe" in installer and "WHPX" in installer,
            "installer does not use the packaged Google WHPX capability probe", findings)
    require("--self-contained" in (installer + packager) and "win-x64" in (installer + packager),
            "installer lacks a self-contained win-x64 source-build fallback", findings)

    for token in ("replayer-runtime-manifest.json", "replayer-baseline.json", "qemu-img", "Get-FileHash"):
        require(token in packager, f"distribution builder is missing runtime integrity/relocation token: {token}", findings)

    require(not (ROOT / "run-replayer.bat").exists(),
            "obsolete run-replayer.bat launcher was reintroduced", findings)

    baseline_guard = "The prepublished API 34 baseline is missing"
    require(baseline_guard in runtime_service,
            "in-app bootstrap does not fail early when the published baseline is absent", findings)
    if baseline_guard in runtime_service and "DownloadAndExtractAsync" in runtime_service:
        require(runtime_service.index(baseline_guard) < runtime_service.index("DownloadAndExtractAsync"),
                "in-app bootstrap downloads SDK data before checking for the published baseline", findings)
    require("replayer-distribution-manifest.json" in revm_paths and "replayer-runtime-manifest.json" in revm_paths,
            "path resolver does not recognize a pristine installed REPlayer distribution", findings)
    require("relocateExternalBacking: true" in runtime_service and "RebaseExternalQcowBacking" in containment,
            "disposable AVD clones do not relocate package-relative QCOW backing paths", findings)
    require("Assert-OutputDoesNotContainInput" in packager and "OutputDirectory must not equal or contain" in packager,
            "distribution builder can recursively delete a source input through OutputDirectory", findings)

    report = {
        "schema": 1,
        "verdict": "PASS" if not findings else "FAIL",
        "root": str(ROOT),
        "findings": findings,
    }
    print(json.dumps(report, indent=2))
    return 0 if not findings else 1


if __name__ == "__main__":
    sys.exit(main())
