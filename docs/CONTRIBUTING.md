# Contributing

REPlayer is in open preview. Focused issues and pull requests are welcome for emulator integration, automation, containment, WPF UX, reproducible testing, and documentation.

## Development gate

```powershell
dotnet restore REPlayer.sln -r win-x64
dotnet build REPlayer.sln -c Release --no-restore -warnaserror
dotnet run --project runtime-src/ReplayerAutomationProbe/ReplayerAutomationProbe.csproj -c Release --no-build
python scripts/validation/agent-center/validate_agent_center.py
python scripts/validation/installer/validate_setup_pipeline.py
python scripts/validation/repository/audit_release_tree.py --root . --history
```

Live WHPX/emulator validation requires a virtualization-enabled Windows host and a separately published, hash-marked runtime baseline.

## Pull requests

- Keep changes scoped and explain the user-visible or security-boundary effect.
- Include reproducible validation output; do not claim renderer, boot, containment, or persona behavior from logs alone when live evidence is required.
- Never commit API keys, signing material, APK samples, VM disks, Android SDK/runtime payloads, case evidence, captures containing private data, or generated build output.
- Preserve the official-emulator/native-display architecture; ADB is a control channel, not the product display.

By contributing, you agree that your contribution is licensed under the repository's MIT License.