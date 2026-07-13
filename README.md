# REPlayer

REPlayer is a Windows WPF Android analysis workbench. Its production runtime uses Google's official Android Emulator with WHPX acceleration, a maintained Android 14/API 34 analysis image, and a native embedded emulator window.

The repository contains source and reproducible build/publishing tools. Downloaded SDKs, AVD disks, signing keys, logs, captures, and test evidence are intentionally excluded from Git.

## Requirements

- Windows 11 with virtualization enabled
- Windows Hypervisor Platform
- .NET 9 SDK
- Visual Studio 2022 or the `dotnet` CLI
- WSL for the API 34 image publication pipeline

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

## Validation

See [docs/production-validation.md](docs/production-validation.md). GitHub Actions builds the complete solution on `windows-latest`; live WHPX/AVD tests run on a virtualization-enabled Windows workstation.

## Security boundary

The persona pipeline normalizes Android framework identity and GL strings for reproducible authorized testing. It does not claim to remove kernel, hypervisor, QEMU-device, hardware-attestation, or native ABI observables. See [docs/runtime-architecture.md](docs/runtime-architecture.md).
