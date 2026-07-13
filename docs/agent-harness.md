# Concurrent Android agent harness

REPlayer includes a structured ADB scheduler for multiple autonomous or human-driven workers. It does not execute host shell command strings. Every operation is an argument array passed directly to the bundled `adb.exe` through `ProcessStartInfo.ArgumentList`.

## Scheduling contract

| Access | Concurrency | Intended operations |
|---|---|---|
| `observe` | Concurrent on the same device | `dumpsys`, package queries, screenshots, log collection, read-only inspection |
| `device-control` | Serialized per ADB serial | activity launch, input injection, install/uninstall, settings mutation, UI automation |

Jobs execute their own steps in order. Different devices may execute control steps concurrently. The coordinator currently admits at most eight active jobs per REPlayer process. Every step has a 1–600 second timeout, cancellation, exit code, stdout, and stderr.

## Interactive use

1. Start an Android instance.
2. Open **Agent Center** from the right toolbar or overflow menu.
3. Select `Observe (concurrent)` or `Device control`.
4. Enter ADB arguments such as `shell dumpsys activity activities`.
5. Queue as many workers as required.

The Android display remains interactive because Agent Center is modeless.

## Automated JSON inbox

Agent Center's **Open inbox** button opens the active device's intake directory:

```text
runtime/agent-harness/<adb-serial>/inbox/
```

An external planner should write to a temporary filename in that directory and atomically rename it to `<requestId>.json`. REPlayer claims each file exactly once, validates it, and submits it through the same coordinator used by the UI.

Example plan:

```json
{
  "schema": 1,
  "requestId": "inspect-settings-001",
  "agentId": "settings-observer",
  "deviceSerial": "emulator-5554",
  "appPackage": "com.android.settings",
  "steps": [
    {
      "name": "launch settings",
      "arguments": ["shell", "am", "start", "-a", "android.settings.SETTINGS"],
      "access": "device-control",
      "timeoutSeconds": 30,
      "continueOnFailure": false
    },
    {
      "name": "capture activity state",
      "arguments": ["shell", "dumpsys", "activity", "activities"],
      "access": "observe",
      "timeoutSeconds": 30,
      "continueOnFailure": false
    }
  ]
}
```

Results are written atomically to:

```text
runtime/agent-harness/<adb-serial>/outbox/<requestId>.result.json
```

Processed requests are retained under `archive/`. In-flight requests are under `processing/`. To cancel an active request, atomically create:

```text
runtime/agent-harness/<adb-serial>/inbox/<requestId>.cancel
```

## Evidence

Each run receives an immutable run ID and JSONL event log. Interactive runs link to evidence from Agent Center. Events include plan start, step start, command result, final status, and any error. Runtime evidence and inbox contents are excluded from Git.

## Planner integration

An LLM or deterministic agent should remain outside the ADB transport boundary:

1. Observe app/device state.
2. Generate a structured plan with explicit access classifications.
3. Write the plan atomically to the inbox.
4. Await the matching outbox result.
5. Consume command evidence and generate the next bounded plan.

This allows independent planners to run concurrently without letting them bypass per-device control serialization. A planner may target a different package, but control remains device-wide because Android exposes only one foreground UI per serial.

## Maintained validation

Run:

```powershell
dotnet run --project runtime-src/ReplayerAutomationProbe/ReplayerAutomationProbe.csproj -c Release
```

The probe verifies same-device serialization, concurrent observation, cross-device concurrency, timeout, cancellation, legacy root normalization, JSON inbox/outbox processing, and JSONL evidence integrity without requiring a live emulator or third-party test package.

Against a running canonical guest, exercise the real bundled ADB transport with:

```powershell
dotnet run --project runtime-src/ReplayerAutomationProbe/ReplayerAutomationProbe.csproj -c Release -- --live-adb runtime\google-emulator\sdk\platform-tools\adb.exe emulator-5554 runtime\validation\agents\live
```

The live mode concurrently reads the neutral model and dark-mode state, launches Settings through a serialized device-control step, and verifies all evidence files.
