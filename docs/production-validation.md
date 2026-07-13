# Production validation

## Build gate

```powershell
dotnet restore REPlayer.sln
dotnet build REPlayer.sln -c Release --no-restore -warnaserror
```

Expected: zero warnings and zero errors.

## Gfxstream gate

The generated backend manifest must report:

```text
emulatorVersion = 37.1.7
inputSha256     = f84d3a277e1ecc380470e7ed988b79cf361e0cedf9d9ee9bf7ee188d90b04947
outputSha256    = 4658c1e77b33be807cd164292a53e0a6da376225b6f8999cfc7810031de3ba3a
vendor          = REPlayer
renderer        = REPlayer Virtual GPU (OpenGL ES 3.1 1.0)
```

After boot, `dumpsys SurfaceFlinger` must report:

```text
GLES: REPlayer, REPlayer Virtual GPU (OpenGL ES 3.1 1.0), OpenGL ES 3.1
```

WebGL must report the same vendor and renderer. NVIDIA/AMD/Intel host model and driver-version strings are release blockers.

## Phase 1 — baseline publication gate

Both immutable API 34 lanes must exist and carry different, matching markers:

- `ReVM.avd`: marker mode `stealth`, target `ReVM`, security lane `release-observation`;
- `ReVMResizable.avd`: marker mode `compatibility`, target `ReVMResizable`, security lane `resizable-analysis`.

Both publication reports must retain file hashes, the official three-profile display declaration, enabled Settings RRO, x86_64-first ABI, Nexus Launcher HOME, and stable framework processes.

Run the fail-closed static validator after both publishers exit:

```bash
python3 scripts/validation/android/validate_api34_publication.py \
  --output runtime/validation/phase1-publication.json
```

It recomputes every published image and provenance-input hash, compares each embedded marker with its report, checks both continuity intervals and signer digests, rejects volatile canonical state, and requires cross-lane inputs to match.

The release-observation AVD must satisfy:

- model `REPlayer Virtual Device`;
- device/product `replayer_x86_64`;
- neutral REPlayer Android 14 release fingerprint;
- build type `user`;
- tags `release-keys`;
- `ro.debuggable=0`;
- `ro.adb.secure=1`;
- ABI begins `x86_64,arm64-v8a`;
- `adb root` is denied;
- `com.replayer.settings.overlay` is enabled;
- Settings resource `about_settings` resolves to `About phone`;
- Android night service reports `Night mode: yes`, secure `ui_night_mode=2`, and `mCurUiMode=0x21` after warm and cold boundaries;
- `system_server` and NetworkStack PIDs remain unchanged for at least 60 seconds.

The resizable-analysis AVD must satisfy:

- `ro.debuggable=1` and `ro.adb.secure=1`;
- `adb root` is available and is explicitly reported as a lane capability;
- profile `0` produces physical `1080×2400`, density `420`;
- profile `2` produces physical `1920×1200`, density `240`;
- profile `0 → 2 → 0` does not change the QEMU PID.

## Phase 2 — REPlayer release-observation gate

Launch the Release executable and require:

- one responsive `REPlayer.exe` process;
- a contained persona-emulator process tree;
- WHPX reported usable;
- the embedded Android window attached to REPlayer rather than a separate user-facing emulator window;
- `sys.boot_completed=1`;
- the canonical baseline cloned into run storage;
- no `scrcpy`, SDL display, encoded-video display, or ADB framebuffer transport;
- no fatal errors in the case manifest or emulator stderr.

The runtime must reject a baseline whose marker or live `ro.debuggable` value does not match the selected lane.

## Phase 3 — resizable-window gate

Launch a Release instance with `api34-resizable` and require:

- real guest transitions `1080×2400 @ 420 → 1920×1200 @ 240 → 1080×2400 @ 420`;
- unchanged QEMU, `system_server`, and NetworkStack PIDs;
- WPF viewport and Qt native window change between `0.45` portrait and `1.6` landscape aspect ratios;
- no stretched portrait framebuffer, gray flash, detached emulator window, or stale orientation preference;
- touch coordinates remain aligned after both transitions;
- a rejected transition rolls both host geometry and saved orientation back atomically.

## Phase 4 — product semantics gate

The production default must use `replayer-api34`, `api34-persona`, `AdbRoot=false`, and persistent Android dark mode. Commercial-device identity is permitted only through an explicit compatibility-research persona override. Loading an older settings file with `AdbRoot=true` must normalize the legacy field to false; root capability comes only from the immutable `api34-resizable` lane.

Run the Phase 1 publication validator plus the Release live validator after a cold process start. Both must reject Pixel/Adreno defaults, light mode, or contradictory root settings.

## Phase 5 — concurrent-agent gate

```powershell
dotnet run --project runtime-src/ReplayerAutomationProbe/ReplayerAutomationProbe.csproj -c Release
```

Expected: same-device control concurrency `1`, same-device observe concurrency at least `2`, cross-device control concurrency at least `2`, timeout `TimedOut`, cancellation `Cancelled`, legacy root normalized, three inbox results, and valid JSONL evidence.

Against a running canonical Release guest:

```powershell
dotnet run --project runtime-src/ReplayerAutomationProbe/ReplayerAutomationProbe.csproj -c Release -- --live-adb runtime\google-emulator\sdk\platform-tools\adb.exe emulator-5554 runtime\validation\agents\live
```

Expected: neutral model, `ui_night_mode=2`, successful serialized Settings launch, and three evidence files.

## Phase 6 — repository and rewritten-history gate

```bash
git diff --check
python3 scripts/validation/repository/audit_release_tree.py --history
git status --short
git log --oneline --decorate
```

The audit scans every candidate source file and every reachable Git blob for private keys, credential patterns, forbidden runtime artifacts, and files over 10 MiB. APKs, VM disks, generated evidence, SDK downloads, Gradle outputs, signing material, and compiled guest binaries must remain outside source history.

After the local history rewrite, clone without hardlinks and repeat restore, Release build with warnings as errors, automation probe, script syntax checks, repository audit with `--history`, and `git fsck --full`. The fresh clone must contain no reachable QCOW2 blob and no uncommitted files.
