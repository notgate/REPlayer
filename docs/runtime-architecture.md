# Runtime architecture

## Production backend

REPlayer defaults to `GoogleEmulatorRuntimeService`. The backend owns:

- pinned Google Emulator `37.1.7` / build `15769812`;
- Android 14/API 34 Google APIs x86_64 image;
- WHPX acceleration;
- native emulator-window embedding;
- per-run AVD cloning and case manifests;
- optional process-scoped network isolation;
- deterministic Android persona application.

Downloaded and generated runtime files live below `runtime/google-emulator/` and are not committed.

## Gfxstream runtime

`ReVM/Runtime/Google/GfxstreamRuntime.cs` creates `persona-emulator-37.1.7` from the official SDK tree. Files are NTFS hard links when possible. Only `libgfxstream_backend.dll` is materialized and transformed.

The transform fails closed unless the source DLL SHA-256 is:

```text
f84d3a277e1ecc380470e7ed988b79cf361e0cedf9d9ee9bf7ee188d90b04947
```

It validates every original instruction/literal sequence before applying the pinned edits. The expected transformed SHA-256 is:

```text
4658c1e77b33be807cd164292a53e0a6da376225b6f8999cfc7810031de3ba3a
```

Validated guest strings:

```text
GL_VENDOR   REPlayer
GL_RENDERER REPlayer Virtual GPU (OpenGL ES 3.1 1.0)
GL_VERSION  OpenGL ES 3.1
```

Google's original signed backend remains untouched. The generated backend has a different hash and is described by `replayer-gfxstream-persona.json`.

## API 34 baseline lanes

`scripts/android/Publish-Api34Persona.sh` publishes two independent canonical AVDs from the same API 34 source:

1. Clone the complete source AVD configuration.
2. Cold boot with `-writable-system -wipe-data`.
3. Run `adb root`, `adb disable-verity`, reboot, and `adb remount`.
4. Patch `build.prop` in system, product, system_ext, vendor, and odm.
5. Build and install the static Settings resource overlay and persistent dark monochromatic theme.
6. Reboot with the selected lane properties.
7. Require the selected root/debug policy, enabled RRO, dark mode, x86_64-first ABI, and stable framework processes.
8. Cold-start a new emulator process and re-verify identity, dark mode, package policy, and continuity.
9. Remove volatile AVD state and publish with file hashes.

`ReVM.avd` is the `api34-persona` release-observation lane. It retains `ro.debuggable=0`, authenticated ADB, and root denial. Android 14's emulator framework disables physical resizable-display control in this state, so this lane remains fixed portrait.

`ReVMResizable.avd` is the `api34-resizable` analysis lane. It retains authenticated ADB but uses `ro.debuggable=1`, making `adb root` available and enabling the real API 34 profile controller. REPlayer never silently substitutes this lane for release observation.

Publish the lanes with:

```bash
bash scripts/android/Publish-Api34Persona.sh
REPLAYER_PERSONA_MODE=compatibility bash scripts/android/Publish-Api34Persona.sh
```

The production persona is `android-tools/replayer-persona/personas/replayer-api34.json`. It identifies as a neutral `REPlayer Virtual Device` with `REPlayer Virtual GPU`; it does not claim to be commercial hardware. The legacy `pixel5-api34.json` profile is retained only for explicit compatibility research via `REPLAYER_PERSONA_FILE=...` and is not the release default.

## Deliberate limitations

The compatibility image changes framework and GL identity surfaces. It does not rewrite or conceal:

- the x86_64 execution environment;
- ranchu/QEMU kernel and device characteristics;
- hypervisor timing or CPUID behavior;
- emulator networking topology;
- hardware-backed attestation results;
- unavailable physical sensors, radios, and secure elements.

These remaining observables must be included in assessment scope and test interpretation.
