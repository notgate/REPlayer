<div align="center">
  <h1>REPLAYER</h1>
  <p><strong>WINDOWS-NATIVE ANDROID RESEARCH WORKBENCH</strong></p>
  <p>Emulation, inspection, instrumentation, and evidence in one controlled desktop environment.</p>

  <p>
    <a href="https://github.com/notgate/REPlayer/actions/workflows/build.yml"><img alt="Build" src="https://github.com/notgate/REPlayer/actions/workflows/build.yml/badge.svg?branch=main"></a>
    <img alt="Platform: Windows 11" src="https://img.shields.io/badge/PLATFORM-WINDOWS_11-3C3C3C?style=for-the-badge&labelColor=1E1E1E&color=3C3C3C">
    <img alt="Runtime: Android 14" src="https://img.shields.io/badge/RUNTIME-ANDROID_14-3C3C3C?style=for-the-badge&labelColor=1E1E1E&color=3C3C3C">
    <img alt="Desktop: .NET 9" src="https://img.shields.io/badge/DESKTOP-.NET_9-3C3C3C?style=for-the-badge&labelColor=1E1E1E&color=3C3C3C">
    <img alt="Stage: Private Preview" src="https://img.shields.io/badge/STAGE-PRIVATE_PREVIEW-3C3C3C?style=for-the-badge&labelColor=1E1E1E&color=3C3C3C">
  </p>
</div>

---

<div align="center">
  <h2>• OVERVIEW •</h2>
</div>

<details open>
  <summary><strong>What REPlayer is</strong></summary>

REPlayer is a WPF Android analysis and reverse-engineering workbench built around Google's official Android Emulator, WHPX acceleration, a maintained Android 14/API 34 guest, and a native embedded display. It unifies APK workflows, ADB, Frida, network capture, case evidence, and provider-backed analysis agents without using scrcpy as the product display.

</details>

<details>
  <summary><strong>Current development stage</strong></summary>

- The repository is private while architecture, containment, and release behavior are still changing.
- CI artifacts contain the self-contained Windows application build.
- Tagged builds create draft GitHub Releases for review before publication.
- Complete emulator-runtime distributions are assembled only from a trusted, hash-marked API 34 baseline.

</details>

<details>
  <summary><strong>Notable capabilities</strong></summary>

- Native WPF workbench with an embedded official emulator/gfxstream display.
- Concurrent analysis agents with parallel observation and serialized same-device control.
- APK installation, ADB operations, Frida workflows, network capture, and case-scoped evidence.
- Neutral REPlayer Android persona with explicit release and resizable analysis lanes.
- Per-user, non-admin setup with SHA-256 verification and relocatable AVD descriptors.

</details>

<details>
  <summary><strong>End-user requirements and setup</strong></summary>

The complete Windows distribution requires 64-bit Windows 11, Intel VT-x or AMD-V enabled in firmware, the **Windows Hypervisor Platform** optional feature, and enough NTFS storage for the runtime and disposable case data.

Extract the complete distribution and run:

```bat
setup.bat
```

Setup installs REPlayer under `%LOCALAPPDATA%\REPlayer`, verifies application and runtime manifests, repairs relocatable AVD descriptors, creates a Start Menu shortcut, and launches the workbench. Failure returns a nonzero exit code and writes `%TEMP%\REPlayer-setup.log`.

</details>

<div align="center">
  <h2>• PREVIEW •</h2>
  <p><sub>Product captures and motion previews will be added as the private UI stabilizes.</sub></p>
</div>

| Main workbench | Agent Center |
|:--:|:--:|
| `docs/media/workbench.png` | `docs/media/agent-center.png` |
| Runtime and case workflow | Evidence and diagnostics |
| `docs/media/runtime.png` | `docs/media/evidence.png` |

> Media slots are intentionally explicit rather than filled with mock screenshots. See [`docs/media/README.md`](docs/media/README.md) for the drop-in image and linked-video conventions.

## Source-build requirements

- .NET 9 SDK or Visual Studio 2022 for the WPF application;
- a prepublished, hash-marked API 34 runtime payload for complete distributions;
- WSL only for release engineers publishing new Android baselines.

## Build

```powershell
dotnet restore REPlayer.sln
dotnet build REPlayer.sln -c Release --no-restore
```

The executable is written to:

```text
ReVM/bin/Release/net9.0-windows/REPlayer.exe
```

## Runtime setup

REPlayer downloads the pinned Google Emulator, platform tools, build tools, and API 34 image into `runtime/google-emulator/`. It then generates a separate, hash-pinned gfxstream runtime that reports the neutral `REPlayer` / `REPlayer Virtual GPU` identity without modifying Google's original SDK files.

The stock Google SDK download is not a distributable REPlayer baseline. Direct
use requires the prepublished `ReVM.avd` marker and immutable disk hashes from
the complete release payload. The setup wizard fails before downloading SDK
data when that payload is absent and directs the user back to `setup.bat`.

Release engineers build a relocatable, self-contained distribution with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup/New-REPlayerDistribution.ps1 `
  -RuntimeSource .\runtime `
  -OutputDirectory .\dist\REPlayer-Distribution
```

For a local source-tree setup using an already published runtime:

```powershell
.\setup.bat -RuntimeSource D:\path\to\published-runtime
```

REPlayer maintains two explicit API 34 baselines:

- `ReVM.avd` — release observation, `ro.debuggable=0`, `adb root` denied, fixed portrait;
- `ReVMResizable.avd` — resizable analysis, real portrait/landscape profiles, `adb root` capable.

Release engineering publishes them independently:

```bash
bash scripts/android/Publish-Api34Persona.sh
REPLAYER_PERSONA_MODE=compatibility bash scripts/android/Publish-Api34Persona.sh
```

The publisher performs a clean writable-system boot, disables verity for publication, applies the multi-partition persona properties, persistent Android dark mode, and static Settings RRO, validates the selected lane's root/debug policy and process continuity, and writes a hashed baseline manifest. REPlayer verifies both the marker and live security properties before accepting a run. The legacy Pixel property profile remains an explicit release-engineering override through `REPLAYER_PERSONA_FILE`; it is not the product default.

## Concurrent agents

Agent Center schedules multiple structured ADB workers with per-device control serialization, concurrent read-only observation, cancellation, timeouts, and JSONL evidence. External planners automate the same coordinator through a validated JSON inbox/outbox protocol. See [docs/agent-harness.md](docs/agent-harness.md).

## Source layout

```text
ReVM/
  Automation/
  Core/
  Rendering/Native/
  Runtime/
    Abstractions/
    Configuration/
    Google/
    Native/
    Legacy/
  UI/
    Controls/
    Dialogs/
android-tools/
  replayer-customizer/
  replayer-persona/
  replayer-settings-overlay/
scripts/
  android/
  diagnostics/
  validation/
```

## CI artifacts and draft releases

Every push and pull request builds the complete solution, runs the deterministic concurrent-agent probe and static release validators, and publishes a self-contained `win-x64` application archive with a SHA-256 sidecar.

Pushing a version tag creates a **draft** GitHub Release. Hyphenated preview tags are also marked as prereleases:

```powershell
git tag v0.1.0-preview.1
git push origin v0.1.0-preview.1
```

The automated archive is deliberately named `REPlayer-<version>-win-x64-app.zip`: it contains the desktop application but not the private API 34 runtime baseline. Release engineers use `scripts/setup/New-REPlayerDistribution.ps1` on the trusted Windows release workstation to produce and verify the complete runtime distribution before attaching or publishing it.

## Validation

See [docs/production-validation.md](docs/production-validation.md). GitHub Actions builds and packages the application on `windows-latest`; live WHPX/AVD and complete-distribution tests run on a virtualization-enabled Windows release workstation.

## Security boundary

The persona pipeline normalizes Android framework identity and GL strings for reproducible authorized testing. It does not claim to remove kernel, hypervisor, QEMU-device, hardware-attestation, or native ABI observables. See [docs/runtime-architecture.md](docs/runtime-architecture.md).
